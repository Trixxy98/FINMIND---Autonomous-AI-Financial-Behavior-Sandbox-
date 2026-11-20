-- ADR-0007: append-only, hash-linked, per-run finance audit event store.
BEGIN;

-- Allows run_events to reference a journal within the same run.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'journal_entries_id_run_id_key'
          AND conrelid = 'journal_entries'::regclass
    ) THEN
        ALTER TABLE journal_entries ADD CONSTRAINT journal_entries_id_run_id_key UNIQUE (id, run_id);
    END IF;
END;
$$;

CREATE TABLE IF NOT EXISTS run_events (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    sequence BIGINT NOT NULL CHECK (sequence > 0),
    event_type TEXT NOT NULL CHECK (event_type IN (
        'RUN_CREATED', 'ACCOUNT_CREATED', 'OBLIGATION_CREATED', 'LEDGER_COMMITTED',
        'OBLIGATION_PAID', 'JOURNAL_REVERSED', 'OBLIGATION_PAYMENT_REVERSED',
        'LEDGER_REPLAYED', 'POLICY_DENIED', 'FINANCE_ERROR')),
    event_version INT NOT NULL CHECK (event_version > 0),
    occurred_at TIMESTAMPTZ NOT NULL,
    virtual_time TIMESTAMPTZ,
    source TEXT NOT NULL CHECK (source IN ('internal_api', 'scenario_engine', 'agent')),
    trace_id TEXT,
    operation TEXT,
    outcome TEXT CHECK (outcome IS NULL OR outcome IN (
        'committed', 'replayed', 'malformed', 'denied', 'conflict', 'internal_error')),
    reason_code TEXT,
    idempotency_key TEXT,
    journal_id UUID,
    obligation_id UUID,
    payload JSONB NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    prev_hash BYTEA,
    event_hash BYTEA NOT NULL,
    UNIQUE (run_id, sequence),
    FOREIGN KEY (journal_id, run_id) REFERENCES journal_entries (id, run_id),
    FOREIGN KEY (obligation_id, run_id) REFERENCES obligations (id, run_id),
    CHECK ((sequence = 1) = (prev_hash IS NULL))
);

-- Canonical hash, scheme finmind-event-v1 (ADR-0007).
CREATE OR REPLACE FUNCTION finmind_run_event_hash(
    p_prev_hash BYTEA, p_run_id UUID, p_sequence BIGINT, p_event_type TEXT, p_event_version INT,
    p_occurred_at TIMESTAMPTZ, p_source TEXT, p_payload JSONB)
RETURNS BYTEA
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT sha256(convert_to(
        'finmind-event-v1' || E'\n' ||
        coalesce(encode(p_prev_hash, 'hex'), '') || E'\n' ||
        p_run_id::text || E'\n' ||
        p_sequence::text || E'\n' ||
        p_event_type || E'\n' ||
        p_event_version::text || E'\n' ||
        to_char(p_occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || E'\n' ||
        p_source || E'\n' ||
        p_payload::text,
        'UTF8'));
$$;

-- Sequence, link, time, and hash are assigned by the database; caller values are ignored.
CREATE OR REPLACE FUNCTION finmind_assign_run_event_chain()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    last_sequence BIGINT;
    last_hash BYTEA;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended('finmind_run_events:' || NEW.run_id::text, 0));

    SELECT sequence, event_hash INTO last_sequence, last_hash
    FROM run_events
    WHERE run_id = NEW.run_id
    ORDER BY sequence DESC
    LIMIT 1;

    NEW.sequence := coalesce(last_sequence, 0) + 1;
    NEW.prev_hash := last_hash;
    NEW.occurred_at := now();
    NEW.event_hash := finmind_run_event_hash(
        NEW.prev_hash, NEW.run_id, NEW.sequence, NEW.event_type, NEW.event_version,
        NEW.occurred_at, NEW.source, NEW.payload);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS run_events_assign_chain ON run_events;
CREATE TRIGGER run_events_assign_chain
    BEFORE INSERT ON run_events
    FOR EACH ROW EXECUTE FUNCTION finmind_assign_run_event_chain();

DROP TRIGGER IF EXISTS run_events_append_only ON run_events;
CREATE TRIGGER run_events_append_only
    BEFORE UPDATE OR DELETE ON run_events
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_ledger_mutation();

-- Returns the first sequence whose position, link, or hash does not match, or NULL if intact.
CREATE OR REPLACE FUNCTION finmind_verify_run_events(p_run_id UUID)
RETURNS BIGINT
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    event_row RECORD;
    expected_sequence BIGINT := 1;
    previous_hash BYTEA := NULL;
BEGIN
    FOR event_row IN
        SELECT sequence, event_type, event_version, occurred_at, source, payload, prev_hash, event_hash
        FROM run_events
        WHERE run_id = p_run_id
        ORDER BY sequence
    LOOP
        IF event_row.sequence <> expected_sequence
           OR event_row.prev_hash IS DISTINCT FROM previous_hash
           OR event_row.event_hash <> finmind_run_event_hash(
                event_row.prev_hash, p_run_id, event_row.sequence, event_row.event_type,
                event_row.event_version, event_row.occurred_at, event_row.source, event_row.payload) THEN
            RETURN expected_sequence;
        END IF;
        previous_hash := event_row.event_hash;
        expected_sequence := expected_sequence + 1;
    END LOOP;
    RETURN NULL;
END;
$$;

REVOKE ALL ON TABLE public.run_events FROM finmind_api;
GRANT SELECT, INSERT ON TABLE public.run_events TO finmind_api;

COMMIT;
