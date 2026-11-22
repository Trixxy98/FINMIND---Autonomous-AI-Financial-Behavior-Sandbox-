-- ADR-0013 A2: event hash scheme v2 covers every stored field. Events written before this keep scheme v1.
BEGIN;

-- Adding a defaulted column does not rewrite history; existing events are v1.
ALTER TABLE run_events
    ADD COLUMN IF NOT EXISTS hash_version SMALLINT NOT NULL DEFAULT 1 CHECK (hash_version IN (1, 2));

-- A column added to run_events later needs a new scheme version; IntegrityTests fails until it is covered.
CREATE OR REPLACE FUNCTION finmind_run_event_hash_v2(p_event run_events)
RETURNS BYTEA
LANGUAGE sql
STABLE
AS $$
    SELECT sha256(convert_to('finmind-event-v2' || E'\n' || jsonb_build_object(
        'id', p_event.id,
        'run_id', p_event.run_id,
        'sequence', p_event.sequence,
        'event_type', p_event.event_type,
        'event_version', p_event.event_version,
        -- Formatted explicitly: JSONB timestamps would depend on the session time zone.
        'occurred_at', to_char(p_event.occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
        'virtual_time', to_char(p_event.virtual_time AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
        'source', p_event.source,
        'trace_id', p_event.trace_id,
        'operation', p_event.operation,
        'outcome', p_event.outcome,
        'reason_code', p_event.reason_code,
        'idempotency_key', p_event.idempotency_key,
        'journal_id', p_event.journal_id,
        'obligation_id', p_event.obligation_id,
        'payload', p_event.payload,
        'prev_hash', encode(p_event.prev_hash, 'hex'),
        'hash_version', p_event.hash_version
    )::text, 'UTF8'));
$$;

CREATE OR REPLACE FUNCTION finmind_assign_run_event_chain()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    last_sequence BIGINT;
    last_hash BYTEA;
    run_start DATE;
    run_day INT;
BEGIN
    -- Re-entrant when the transaction already holds the run lock (ADR-0007, migration 007).
    SELECT virtual_start_date, virtual_day INTO run_start, run_day
    FROM experiment_runs WHERE id = NEW.run_id FOR UPDATE;

    SELECT sequence, event_hash INTO last_sequence, last_hash
    FROM run_events
    WHERE run_id = NEW.run_id
    ORDER BY sequence DESC
    LIMIT 1;

    NEW.sequence := coalesce(last_sequence, 0) + 1;
    NEW.prev_hash := last_hash;
    NEW.occurred_at := now();
    NEW.virtual_time := CASE WHEN run_start IS NULL OR run_day IS NULL THEN NULL
        ELSE ((run_start + run_day)::timestamp AT TIME ZONE 'UTC') END;
    NEW.hash_version := 2;
    NEW.event_hash := finmind_run_event_hash_v2(NEW);
    RETURN NEW;
END;
$$;

-- First sequence whose position, link, scheme, or hash does not match, or NULL if intact.
-- The scheme never goes back from v2 to v1 within a run, so a v2 event cannot be passed off as v1.
CREATE OR REPLACE FUNCTION finmind_verify_run_events(p_run_id UUID)
RETURNS BIGINT
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    event_row run_events%ROWTYPE;
    expected_sequence BIGINT := 1;
    previous_hash BYTEA := NULL;
    previous_version SMALLINT := 1;
    expected_hash BYTEA;
BEGIN
    FOR event_row IN
        SELECT * FROM run_events WHERE run_id = p_run_id ORDER BY sequence
    LOOP
        IF event_row.hash_version = 2 THEN
            expected_hash := finmind_run_event_hash_v2(event_row);
        ELSE
            expected_hash := finmind_run_event_hash(
                event_row.prev_hash, p_run_id, event_row.sequence, event_row.event_type,
                event_row.event_version, event_row.occurred_at, event_row.source, event_row.payload);
        END IF;

        IF event_row.sequence <> expected_sequence
           OR event_row.prev_hash IS DISTINCT FROM previous_hash
           OR event_row.hash_version < previous_version
           OR event_row.event_hash IS DISTINCT FROM expected_hash THEN
            RETURN expected_sequence;
        END IF;
        previous_hash := event_row.event_hash;
        previous_version := event_row.hash_version;
        expected_sequence := expected_sequence + 1;
    END LOOP;
    RETURN NULL;
END;
$$;

COMMIT;
