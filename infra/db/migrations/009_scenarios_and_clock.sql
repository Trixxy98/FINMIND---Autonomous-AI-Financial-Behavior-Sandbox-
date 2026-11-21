-- ADR-0010: materialized scenario schedules and the per-run virtual clock.
BEGIN;

ALTER TABLE experiment_runs
    ADD COLUMN IF NOT EXISTS virtual_day INT CHECK (virtual_day IS NULL OR virtual_day BETWEEN 0 AND 365),
    ADD COLUMN IF NOT EXISTS virtual_start_date DATE;

ALTER TABLE accounts
    ADD COLUMN IF NOT EXISTS scenario_key TEXT,
    ADD COLUMN IF NOT EXISTS label TEXT;

ALTER TABLE obligations
    ADD COLUMN IF NOT EXISTS due_day INT,
    ADD COLUMN IF NOT EXISTS label TEXT,
    ADD COLUMN IF NOT EXISTS scenario_event_key TEXT;

CREATE TABLE IF NOT EXISTS scenario_events (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    event_key TEXT NOT NULL,
    position INT NOT NULL CHECK (position >= 0),
    day INT NOT NULL CHECK (day BETWEEN 0 AND 365),
    kind TEXT NOT NULL CHECK (kind IN ('message', 'obligation')),
    payload JSONB NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    delivered_at_day INT CHECK (delivered_at_day IS NULL OR delivered_at_day >= day),
    UNIQUE (run_id, event_key),
    UNIQUE (run_id, position)
);

-- The clock is set once at activation, then only moves forward; the start date never changes.
CREATE OR REPLACE FUNCTION finmind_guard_virtual_clock()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.virtual_start_date IS NOT NULL AND NEW.virtual_start_date IS DISTINCT FROM OLD.virtual_start_date THEN
        RAISE EXCEPTION 'run % virtual start date is immutable', OLD.id USING ERRCODE = '55000';
    END IF;
    IF OLD.virtual_day IS NOT NULL AND (NEW.virtual_day IS NULL OR NEW.virtual_day < OLD.virtual_day) THEN
        RAISE EXCEPTION 'run % virtual clock cannot move backward', OLD.id USING ERRCODE = '55000';
    END IF;
    IF NEW.virtual_day IS NOT NULL AND (NEW.status IS NULL OR NEW.virtual_start_date IS NULL) THEN
        RAISE EXCEPTION 'run % virtual clock requires a managed run with a start date', OLD.id USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS experiment_runs_virtual_clock_guard ON experiment_runs;
CREATE TRIGGER experiment_runs_virtual_clock_guard
    BEFORE UPDATE OF virtual_day, virtual_start_date ON experiment_runs
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_virtual_clock();

-- Scenario schedules are inserted once; only delivery may be recorded, once.
CREATE OR REPLACE FUNCTION finmind_guard_scenario_event()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'scenario events are append-only' USING ERRCODE = '55000';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.run_id IS DISTINCT FROM OLD.run_id
       OR NEW.event_key IS DISTINCT FROM OLD.event_key OR NEW.position IS DISTINCT FROM OLD.position
       OR NEW.day IS DISTINCT FROM OLD.day
       OR NEW.kind IS DISTINCT FROM OLD.kind OR NEW.payload IS DISTINCT FROM OLD.payload
       OR OLD.delivered_at_day IS NOT NULL THEN
        RAISE EXCEPTION 'scenario event % can only be delivered once', OLD.event_key USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS scenario_events_guard ON scenario_events;
CREATE TRIGGER scenario_events_guard
    BEFORE UPDATE OR DELETE ON scenario_events
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_scenario_event();

-- Event chain now also records virtual time (not part of the v1 hash; CLOCK_ADVANCED events are hashed).
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
    NEW.event_hash := finmind_run_event_hash(
        NEW.prev_hash, NEW.run_id, NEW.sequence, NEW.event_type, NEW.event_version,
        NEW.occurred_at, NEW.source, NEW.payload);
    RETURN NEW;
END;
$$;

ALTER TABLE run_events DROP CONSTRAINT IF EXISTS run_events_event_type_check;
ALTER TABLE run_events
    ADD CONSTRAINT run_events_event_type_check
    CHECK (event_type IN (
        'RUN_CREATED', 'ACCOUNT_CREATED', 'OBLIGATION_CREATED', 'LEDGER_COMMITTED',
        'OBLIGATION_PAID', 'JOURNAL_REVERSED', 'OBLIGATION_PAYMENT_REVERSED',
        'LEDGER_REPLAYED', 'POLICY_DENIED', 'FINANCE_ERROR', 'RUN_STATUS_CHANGED',
        'SCENARIO_MATERIALIZED', 'SCENARIO_EVENT_DELIVERED', 'CLOCK_ADVANCED'));

REVOKE ALL ON TABLE public.scenario_events FROM finmind_api;
GRANT SELECT, INSERT ON TABLE public.scenario_events TO finmind_api;
GRANT UPDATE (delivered_at_day) ON TABLE public.scenario_events TO finmind_api;
GRANT UPDATE (virtual_day, virtual_start_date) ON TABLE public.experiment_runs TO finmind_api;

COMMIT;
