-- ADR-0015 step 2: batches of runs created from one configuration, started in a recorded order.
BEGIN;

CREATE TABLE IF NOT EXISTS run_batches (
    id UUID PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    label TEXT CHECK (label IS NULL OR length(label) BETWEEN 1 AND 200),
    request_config JSONB NOT NULL CHECK (jsonb_typeof(request_config) = 'object'),
    run_count INT NOT NULL CHECK (run_count BETWEEN 1 AND 20),
    seed_mode TEXT NOT NULL CHECK (seed_mode IN ('same', 'list')),
    seeds BIGINT[],
    order_seed BIGINT NOT NULL CHECK (order_seed >= 0),
    start_order INT[] NOT NULL,
    CHECK ((seed_mode = 'list') = (seeds IS NOT NULL)),
    CHECK (cardinality(start_order) = run_count)
);

-- A batch is a record of what was asked: never edited or deleted.
DROP TRIGGER IF EXISTS run_batches_append_only ON run_batches;
CREATE TRIGGER run_batches_append_only
    BEFORE UPDATE OR DELETE ON run_batches
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_ledger_mutation();

ALTER TABLE experiment_runs
    ADD COLUMN IF NOT EXISTS batch_id UUID REFERENCES run_batches(id),
    ADD COLUMN IF NOT EXISTS batch_position INT CHECK (batch_position BETWEEN 0 AND 19);

ALTER TABLE experiment_runs DROP CONSTRAINT IF EXISTS experiment_runs_batch_pair;
ALTER TABLE experiment_runs
    ADD CONSTRAINT experiment_runs_batch_pair CHECK ((batch_id IS NULL) = (batch_position IS NULL));

CREATE UNIQUE INDEX IF NOT EXISTS experiment_runs_batch_position_idx
    ON experiment_runs (batch_id, batch_position) WHERE batch_id IS NOT NULL;

-- Batch membership is set when a run is created and never changes.
CREATE OR REPLACE FUNCTION finmind_guard_run_batch()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.batch_id IS DISTINCT FROM OLD.batch_id OR NEW.batch_position IS DISTINCT FROM OLD.batch_position THEN
        RAISE EXCEPTION 'run % batch membership is immutable', OLD.id USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS experiment_runs_batch_guard ON experiment_runs;
CREATE TRIGGER experiment_runs_batch_guard
    BEFORE UPDATE ON experiment_runs
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_run_batch();

REVOKE ALL ON TABLE public.run_batches FROM finmind_api;
GRANT SELECT, INSERT ON TABLE public.run_batches TO finmind_api;

COMMIT;
