-- ADR-0012: server-computed model cost and the cost budget.
BEGIN;

ALTER TABLE experiment_runs
    ADD COLUMN IF NOT EXISTS model_cost_micro_usd BIGINT NOT NULL DEFAULT 0 CHECK (model_cost_micro_usd >= 0);

CREATE OR REPLACE FUNCTION finmind_guard_model_cost()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.model_cost_micro_usd < OLD.model_cost_micro_usd THEN
        RAISE EXCEPTION 'run % model cost cannot decrease', OLD.id USING ERRCODE = '55000';
    END IF;
    IF NEW.model_cost_micro_usd <> OLD.model_cost_micro_usd AND NEW.status IS NULL THEN
        RAISE EXCEPTION 'run % is unmanaged and has no cost budget', OLD.id USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS experiment_runs_model_cost_guard ON experiment_runs;
CREATE TRIGGER experiment_runs_model_cost_guard
    BEFORE UPDATE OF model_cost_micro_usd ON experiment_runs
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_model_cost();

GRANT UPDATE (model_cost_micro_usd) ON TABLE public.experiment_runs TO finmind_api;

COMMIT;
