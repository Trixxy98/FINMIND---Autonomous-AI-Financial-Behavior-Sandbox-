-- ADR-0007 amendment: serialize event sequence assignment with the run row lock, not an advisory lock.
-- Finance operations lock the run row first and then insert events. An event-only writer that took an
-- advisory lock first and then waited on the run row (foreign key check) could deadlock with them.
-- Locking the run row here gives every writer the same order: run row, then sequence assignment.
BEGIN;

CREATE OR REPLACE FUNCTION finmind_assign_run_event_chain()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    last_sequence BIGINT;
    last_hash BYTEA;
BEGIN
    -- Re-entrant when the transaction already holds the run lock.
    PERFORM 1 FROM experiment_runs WHERE id = NEW.run_id FOR UPDATE;

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

COMMIT;
