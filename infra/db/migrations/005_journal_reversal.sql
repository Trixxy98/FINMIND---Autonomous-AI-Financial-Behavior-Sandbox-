-- ADR-0006: full journal reversal and reversal-aware obligation state.
-- Append-only: existing journals and postings are not updated or backfilled.
BEGIN;

-- 1. Reversal journal kind and linkage columns.
ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_kind_check;
ALTER TABLE journal_entries
    ADD CONSTRAINT journal_entries_kind_check
    CHECK (kind IN ('opening_funding', 'transfer', 'savings_move', 'bill_payment', 'reversal'));

ALTER TABLE journal_entries
    ADD COLUMN IF NOT EXISTS reverses_entry_id UUID,
    ADD COLUMN IF NOT EXISTS obligation_id UUID,
    ADD COLUMN IF NOT EXISTS reversal_reason TEXT,
    ADD COLUMN IF NOT EXISTS initiator_type TEXT;

ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_reverses_entry_fkey;
ALTER TABLE journal_entries
    ADD CONSTRAINT journal_entries_reverses_entry_fkey
    FOREIGN KEY (reverses_entry_id, run_id, currency)
    REFERENCES journal_entries (id, run_id, currency);

ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_obligation_fkey;
ALTER TABLE journal_entries
    ADD CONSTRAINT journal_entries_obligation_fkey
    FOREIGN KEY (obligation_id, run_id)
    REFERENCES obligations (id, run_id);

ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_initiator_type_check;
ALTER TABLE journal_entries
    ADD CONSTRAINT journal_entries_initiator_type_check
    CHECK (initiator_type IS NULL OR initiator_type IN ('researcher', 'scenario_engine'));

-- Reversal fields are present exactly on reversal journals.
ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_reversal_fields_check;
ALTER TABLE journal_entries
    ADD CONSTRAINT journal_entries_reversal_fields_check
    CHECK (
        (kind = 'reversal'
            AND reverses_entry_id IS NOT NULL
            AND reverses_entry_id <> id
            AND initiator_type IS NOT NULL
            AND reversal_reason IS NOT NULL
            AND length(btrim(reversal_reason)) > 0)
        OR
        (kind <> 'reversal'
            AND reverses_entry_id IS NULL
            AND initiator_type IS NULL
            AND reversal_reason IS NULL)
    );

-- Only bill payments and their reversals carry an obligation.
ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_obligation_kind_check;
ALTER TABLE journal_entries
    ADD CONSTRAINT journal_entries_obligation_kind_check
    CHECK (obligation_id IS NULL OR kind IN ('bill_payment', 'reversal'));

-- New bill payments must bind to an obligation. NOT VALID: legacy rows are not rewritten.
ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS journal_entries_bill_payment_obligation_check;
ALTER TABLE journal_entries
    ADD CONSTRAINT journal_entries_bill_payment_obligation_check
    CHECK (kind <> 'bill_payment' OR obligation_id IS NOT NULL) NOT VALID;

-- Reversal uniqueness backstop.
CREATE UNIQUE INDEX IF NOT EXISTS journal_entries_one_reversal_per_entry
    ON journal_entries (reverses_entry_id)
    WHERE reverses_entry_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS journal_entries_obligation_idx
    ON journal_entries (obligation_id)
    WHERE obligation_id IS NOT NULL;

-- 2. Shape trigger: reversal shape is validated by finmind_assert_reversal_valid.
CREATE OR REPLACE FUNCTION finmind_assert_journal_balanced()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    target_entry_id UUID;
    target_run_id UUID;
    journal_kind TEXT;
    posting_count BIGINT;
    distinct_account_count BIGINT;
    posting_total NUMERIC;
    debit_kind TEXT;
    credit_kind TEXT;
    valid_shape BOOLEAN;
BEGIN
    IF TG_TABLE_NAME = 'journal_entries' THEN
        target_entry_id := NEW.id;
    ELSE
        target_entry_id := NEW.entry_id;
    END IF;
    target_run_id := NEW.run_id;

    SELECT kind INTO journal_kind
    FROM journal_entries
    WHERE id = target_entry_id AND run_id = target_run_id;

    IF NOT FOUND THEN
        RETURN NULL;
    END IF;

    SELECT COUNT(*), COUNT(DISTINCT account_id), COALESCE(SUM(signed_amount_sen::NUMERIC), 0)
    INTO posting_count, distinct_account_count, posting_total
    FROM journal_postings
    WHERE entry_id = target_entry_id AND run_id = target_run_id;

    IF posting_count <> 2 OR distinct_account_count <> 2 OR posting_total <> 0 THEN
        RAISE EXCEPTION 'journal % must contain exactly two distinct, balanced postings', target_entry_id
            USING ERRCODE = '23514';
    END IF;

    SELECT
        MAX(a.kind) FILTER (WHERE p.signed_amount_sen < 0),
        MAX(a.kind) FILTER (WHERE p.signed_amount_sen > 0)
    INTO debit_kind, credit_kind
    FROM journal_postings p
    JOIN accounts a ON a.id = p.account_id AND a.run_id = p.run_id AND a.currency = p.currency
    WHERE p.entry_id = target_entry_id AND p.run_id = target_run_id;

    valid_shape := CASE journal_kind
        WHEN 'opening_funding' THEN debit_kind = 'simulator_treasury' AND credit_kind = 'agent_cash'
        WHEN 'transfer' THEN debit_kind IN ('agent_cash', 'agent_savings') AND credit_kind IN ('merchant', 'agent_cash', 'agent_savings')
        WHEN 'savings_move' THEN debit_kind = 'agent_cash' AND credit_kind = 'agent_savings'
        WHEN 'bill_payment' THEN debit_kind = 'agent_cash' AND credit_kind = 'merchant'
        WHEN 'reversal' THEN TRUE
        ELSE FALSE
    END;

    IF NOT valid_shape THEN
        RAISE EXCEPTION 'journal % has account kinds not allowed for %', target_entry_id, journal_kind
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$$;

-- 3. Reversal validity, checked at commit: reversible original, same obligation, exact negated postings.
CREATE OR REPLACE FUNCTION finmind_assert_reversal_valid()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    target_entry_id UUID;
    target_run_id UUID;
    reversal_row RECORD;
    original_row RECORD;
BEGIN
    IF TG_TABLE_NAME = 'journal_entries' THEN
        target_entry_id := NEW.id;
    ELSE
        target_entry_id := NEW.entry_id;
    END IF;
    target_run_id := NEW.run_id;

    SELECT kind, reverses_entry_id, obligation_id INTO reversal_row
    FROM journal_entries
    WHERE id = target_entry_id AND run_id = target_run_id;

    IF NOT FOUND OR reversal_row.kind <> 'reversal' THEN
        RETURN NULL;
    END IF;

    SELECT kind, obligation_id INTO original_row
    FROM journal_entries
    WHERE id = reversal_row.reverses_entry_id AND run_id = target_run_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'reversal % references a journal outside its run', target_entry_id
            USING ERRCODE = '23514';
    END IF;

    IF original_row.kind NOT IN ('bill_payment', 'transfer', 'savings_move') THEN
        RAISE EXCEPTION 'journal kind % is not reversible', original_row.kind
            USING ERRCODE = '23514';
    END IF;

    IF original_row.kind = 'bill_payment' THEN
        IF original_row.obligation_id IS NULL
           OR reversal_row.obligation_id IS DISTINCT FROM original_row.obligation_id THEN
            RAISE EXCEPTION 'reversal % must reference the obligation of the original payment', target_entry_id
                USING ERRCODE = '23514';
        END IF;
    ELSIF reversal_row.obligation_id IS NOT NULL THEN
        RAISE EXCEPTION 'reversal % of a % journal cannot carry an obligation', target_entry_id, original_row.kind
            USING ERRCODE = '23514';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM (
            SELECT account_id, SUM(signed_amount_sen) AS amount
            FROM journal_postings
            WHERE entry_id = reversal_row.reverses_entry_id AND run_id = target_run_id
            GROUP BY account_id
        ) original_postings
        FULL JOIN (
            SELECT account_id, SUM(signed_amount_sen) AS amount
            FROM journal_postings
            WHERE entry_id = target_entry_id AND run_id = target_run_id
            GROUP BY account_id
        ) reversal_postings USING (account_id)
        WHERE original_postings.amount IS NULL
           OR reversal_postings.amount IS NULL
           OR original_postings.amount + reversal_postings.amount <> 0
    ) THEN
        RAISE EXCEPTION 'reversal % must negate every posting of journal %', target_entry_id, reversal_row.reverses_entry_id
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS journal_entries_reversal_check ON journal_entries;
CREATE CONSTRAINT TRIGGER journal_entries_reversal_check
    AFTER INSERT ON journal_entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finmind_assert_reversal_valid();

DROP TRIGGER IF EXISTS journal_postings_reversal_check ON journal_postings;
CREATE CONSTRAINT TRIGGER journal_postings_reversal_check
    AFTER INSERT ON journal_postings
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finmind_assert_reversal_valid();

-- 4. Obligation state guard (immediate). Checks linkage; posting correctness is checked at commit by step 3.
CREATE OR REPLACE FUNCTION finmind_guard_obligation_state()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.paid OR NEW.paid_entry_id IS NOT NULL THEN
            RAISE EXCEPTION 'obligations must be created unpaid'
                USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;

    IF OLD.paid AND NOT NEW.paid THEN
        IF NOT EXISTS (
            SELECT 1 FROM journal_entries
            WHERE kind = 'reversal'
              AND run_id = OLD.run_id
              AND reverses_entry_id = OLD.paid_entry_id
              AND obligation_id = OLD.id
        ) THEN
            RAISE EXCEPTION 'obligation % can return to unpaid only after its payment is reversed', OLD.id
                USING ERRCODE = '23514';
        END IF;
    ELSIF NOT OLD.paid AND NEW.paid THEN
        IF NOT EXISTS (
            SELECT 1 FROM journal_entries payment
            WHERE payment.id = NEW.paid_entry_id
              AND payment.run_id = OLD.run_id
              AND payment.kind = 'bill_payment'
              AND payment.obligation_id = OLD.id
              AND NOT EXISTS (
                  SELECT 1 FROM journal_entries reversal
                  WHERE reversal.reverses_entry_id = payment.id
              )
        ) THEN
            RAISE EXCEPTION 'obligation % can be paid only by an unreversed bill payment for it', OLD.id
                USING ERRCODE = '23514';
        END IF;
    ELSIF OLD.paid AND NEW.paid AND NEW.paid_entry_id IS DISTINCT FROM OLD.paid_entry_id THEN
        RAISE EXCEPTION 'obligation % payment reference cannot change while paid', OLD.id
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS obligations_state_guard ON obligations;
CREATE TRIGGER obligations_state_guard
    BEFORE INSERT OR UPDATE ON obligations
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_obligation_state();

-- 5. Payment effectiveness, checked at commit for each affected obligation.
CREATE OR REPLACE FUNCTION finmind_assert_obligation_payment_effective()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    target_obligation_id UUID;
    obligation_row RECORD;
    active_payment_count BIGINT;
    active_payment_id UUID;
    legacy_payment_effective BOOLEAN;
BEGIN
    IF TG_TABLE_NAME = 'obligations' THEN
        target_obligation_id := NEW.id;
    ELSE
        target_obligation_id := NEW.obligation_id;
    END IF;

    IF target_obligation_id IS NULL THEN
        RETURN NULL;
    END IF;

    SELECT run_id, paid, paid_entry_id INTO obligation_row
    FROM obligations
    WHERE id = target_obligation_id;

    SELECT COUNT(*), MAX(payment.id::TEXT)::UUID
    INTO active_payment_count, active_payment_id
    FROM journal_entries payment
    WHERE payment.obligation_id = target_obligation_id
      AND payment.kind = 'bill_payment'
      AND NOT EXISTS (
          SELECT 1 FROM journal_entries reversal
          WHERE reversal.reverses_entry_id = payment.id
      );

    IF active_payment_count > 1 THEN
        RAISE EXCEPTION 'obligation % has more than one effective payment', target_obligation_id
            USING ERRCODE = '23514';
    END IF;

    IF obligation_row.paid THEN
        -- Legacy payments (before migration 005) have no obligation_id and cannot be reversed.
        SELECT EXISTS (
            SELECT 1 FROM journal_entries legacy
            WHERE legacy.id = obligation_row.paid_entry_id
              AND legacy.kind = 'bill_payment'
              AND legacy.obligation_id IS NULL
        ) INTO legacy_payment_effective;

        IF NOT ((active_payment_count = 1 AND active_payment_id = obligation_row.paid_entry_id)
                OR (active_payment_count = 0 AND legacy_payment_effective)) THEN
            RAISE EXCEPTION 'paid obligation % must reference its single effective payment', target_obligation_id
                USING ERRCODE = '23514';
        END IF;
    ELSIF active_payment_count <> 0 THEN
        RAISE EXCEPTION 'unpaid obligation % has an effective payment', target_obligation_id
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS obligations_payment_effective_check ON obligations;
CREATE CONSTRAINT TRIGGER obligations_payment_effective_check
    AFTER UPDATE ON obligations
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finmind_assert_obligation_payment_effective();

DROP TRIGGER IF EXISTS journal_entries_payment_effective_check ON journal_entries;
CREATE CONSTRAINT TRIGGER journal_entries_payment_effective_check
    AFTER INSERT ON journal_entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finmind_assert_obligation_payment_effective();

COMMIT;
