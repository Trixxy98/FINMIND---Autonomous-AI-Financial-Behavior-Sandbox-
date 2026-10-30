BEGIN;

CREATE TABLE IF NOT EXISTS experiment_runs (
    id UUID PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS accounts (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    kind TEXT NOT NULL CHECK (kind IN ('simulator_treasury', 'agent_cash', 'agent_savings', 'merchant')),
    currency CHAR(3) NOT NULL DEFAULT 'MYR' CHECK (currency = 'MYR'),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE (id, run_id, currency)
);

CREATE TABLE IF NOT EXISTS journal_entries (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    currency CHAR(3) NOT NULL DEFAULT 'MYR' CHECK (currency = 'MYR'),
    kind TEXT NOT NULL CHECK (kind IN ('opening_funding', 'transfer', 'savings_move', 'bill_payment')),
    idempotency_key TEXT NOT NULL CHECK (length(btrim(idempotency_key)) > 0),
    description TEXT NOT NULL CHECK (length(btrim(description)) > 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (run_id, idempotency_key),
    UNIQUE (id, run_id, currency)
);

CREATE UNIQUE INDEX IF NOT EXISTS one_opening_funding_per_run
    ON journal_entries (run_id)
    WHERE kind = 'opening_funding';

CREATE TABLE IF NOT EXISTS journal_postings (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entry_id UUID NOT NULL,
    run_id UUID NOT NULL,
    account_id UUID NOT NULL,
    currency CHAR(3) NOT NULL,
    signed_amount_sen BIGINT NOT NULL CHECK (signed_amount_sen <> 0),
    FOREIGN KEY (entry_id, run_id, currency)
        REFERENCES journal_entries (id, run_id, currency),
    FOREIGN KEY (account_id, run_id, currency)
        REFERENCES accounts (id, run_id, currency)
);

CREATE TABLE IF NOT EXISTS obligations (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    merchant_account_id UUID NOT NULL,
    currency CHAR(3) NOT NULL DEFAULT 'MYR' CHECK (currency = 'MYR'),
    amount_sen BIGINT NOT NULL CHECK (amount_sen > 0),
    paid BOOLEAN NOT NULL DEFAULT FALSE,
    paid_entry_id UUID,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (id, run_id),
    FOREIGN KEY (merchant_account_id, run_id, currency)
        REFERENCES accounts (id, run_id, currency),
    FOREIGN KEY (paid_entry_id, run_id, currency)
        REFERENCES journal_entries (id, run_id, currency),
    CHECK ((paid AND paid_entry_id IS NOT NULL) OR (NOT paid AND paid_entry_id IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS obligations_paid_entry_idx
    ON obligations (paid_entry_id)
    WHERE paid_entry_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS journal_entries_run_created_idx
    ON journal_entries (run_id, created_at, id);
CREATE INDEX IF NOT EXISTS journal_postings_account_idx
    ON journal_postings (account_id, entry_id);

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
        ELSE FALSE
    END;

    IF NOT valid_shape THEN
        RAISE EXCEPTION 'journal % has account kinds not allowed for %', target_entry_id, journal_kind
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS journal_entries_balance_check ON journal_entries;
CREATE CONSTRAINT TRIGGER journal_entries_balance_check
    AFTER INSERT ON journal_entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finmind_assert_journal_balanced();

DROP TRIGGER IF EXISTS journal_postings_balance_check ON journal_postings;
CREATE CONSTRAINT TRIGGER journal_postings_balance_check
    AFTER INSERT ON journal_postings
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION finmind_assert_journal_balanced();

CREATE OR REPLACE FUNCTION finmind_reject_ledger_mutation()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'ledger rows are append-only; add a compensating journal instead'
        USING ERRCODE = '55000';
END;
$$;

DROP TRIGGER IF EXISTS journal_entries_append_only ON journal_entries;
CREATE TRIGGER journal_entries_append_only
    BEFORE UPDATE OR DELETE ON journal_entries
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_ledger_mutation();

DROP TRIGGER IF EXISTS journal_postings_append_only ON journal_postings;
CREATE TRIGGER journal_postings_append_only
    BEFORE UPDATE OR DELETE ON journal_postings
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_ledger_mutation();

COMMIT;
