BEGIN;

ALTER TABLE experiment_runs
    ADD COLUMN IF NOT EXISTS transaction_limit_sen BIGINT
        CHECK (transaction_limit_sen IS NULL OR transaction_limit_sen > 0);

CREATE OR REPLACE FUNCTION finmind_reject_run_policy_change()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.transaction_limit_sen IS NOT NULL
       AND OLD.transaction_limit_sen IS DISTINCT FROM NEW.transaction_limit_sen THEN
        RAISE EXCEPTION 'run transaction policy is immutable once configured'
            USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS experiment_runs_transaction_policy_immutable ON experiment_runs;
CREATE TRIGGER experiment_runs_transaction_policy_immutable
    BEFORE UPDATE OF transaction_limit_sen ON experiment_runs
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_run_policy_change();

COMMIT;
