-- ADR-0008: managed runs with an immutable configuration snapshot and a server-enforced lifecycle.
-- Runs with NULL status are unmanaged legacy runs created by the Phase 1 OpenRun path.
BEGIN;

ALTER TABLE experiment_runs
    ADD COLUMN IF NOT EXISTS status TEXT,
    ADD COLUMN IF NOT EXISTS status_reason TEXT,
    ADD COLUMN IF NOT EXISTS config_snapshot JSONB,
    ADD COLUMN IF NOT EXISTS config_hash BYTEA,
    ADD COLUMN IF NOT EXISTS template_id UUID,
    ADD COLUMN IF NOT EXISTS queued_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS started_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS finished_at TIMESTAMPTZ;

ALTER TABLE experiment_runs DROP CONSTRAINT IF EXISTS experiment_runs_status_check;
ALTER TABLE experiment_runs
    ADD CONSTRAINT experiment_runs_status_check
    CHECK (status IS NULL OR status IN (
        'DRAFT', 'QUEUED', 'INITIALIZING', 'RUNNING',
        'COMPLETED', 'FAILED', 'CANCELLED', 'BUDGET_EXCEEDED'));

ALTER TABLE experiment_runs DROP CONSTRAINT IF EXISTS experiment_runs_managed_check;
ALTER TABLE experiment_runs
    ADD CONSTRAINT experiment_runs_managed_check
    CHECK (
        (status IS NULL AND config_snapshot IS NULL AND config_hash IS NULL)
        OR (status IS NOT NULL AND config_snapshot IS NOT NULL AND config_hash IS NOT NULL
            AND jsonb_typeof(config_snapshot) = 'object'
            AND transaction_limit_sen IS NOT NULL));

CREATE OR REPLACE FUNCTION finmind_run_config_hash(p_snapshot JSONB)
RETURNS BYTEA
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT sha256(convert_to('finmind-run-config-v1' || E'\n' || p_snapshot::text, 'UTF8'));
$$;

CREATE OR REPLACE FUNCTION finmind_run_transition_allowed(p_from TEXT, p_to TEXT)
RETURNS BOOLEAN
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT (p_from, p_to) IN (
        ('DRAFT', 'QUEUED'),
        ('QUEUED', 'INITIALIZING'),
        ('INITIALIZING', 'RUNNING'),
        ('RUNNING', 'COMPLETED'),
        ('RUNNING', 'BUDGET_EXCEEDED'),
        ('INITIALIZING', 'FAILED'),
        ('RUNNING', 'FAILED'),
        ('DRAFT', 'CANCELLED'),
        ('QUEUED', 'CANCELLED'),
        ('INITIALIZING', 'CANCELLED'),
        ('RUNNING', 'CANCELLED'));
$$;

CREATE OR REPLACE FUNCTION finmind_guard_run_lifecycle()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.queued_at := NULL;
        NEW.started_at := NULL;
        NEW.finished_at := NULL;
        IF NEW.config_snapshot IS NOT NULL THEN
            IF NEW.status IS DISTINCT FROM 'DRAFT' THEN
                RAISE EXCEPTION 'managed runs must be created as DRAFT' USING ERRCODE = '23514';
            END IF;
            NEW.config_hash := finmind_run_config_hash(NEW.config_snapshot);
        ELSE
            NEW.config_hash := NULL;
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.config_snapshot IS DISTINCT FROM OLD.config_snapshot
       OR NEW.config_hash IS DISTINCT FROM OLD.config_hash
       OR NEW.template_id IS DISTINCT FROM OLD.template_id THEN
        RAISE EXCEPTION 'run % configuration is immutable', OLD.id USING ERRCODE = '55000';
    END IF;

    IF (OLD.status IS NULL) <> (NEW.status IS NULL) THEN
        RAISE EXCEPTION 'run % cannot change between managed and unmanaged', OLD.id USING ERRCODE = '55000';
    END IF;

    -- Timestamps are owned by this trigger.
    NEW.queued_at := OLD.queued_at;
    NEW.started_at := OLD.started_at;
    NEW.finished_at := OLD.finished_at;

    IF NEW.status IS DISTINCT FROM OLD.status THEN
        IF NOT finmind_run_transition_allowed(OLD.status, NEW.status) THEN
            RAISE EXCEPTION 'run % cannot move from % to %', OLD.id, OLD.status, NEW.status USING ERRCODE = '23514';
        END IF;
        IF NEW.status = 'QUEUED' THEN NEW.queued_at := now(); END IF;
        IF NEW.status = 'RUNNING' THEN NEW.started_at := now(); END IF;
        IF NEW.status IN ('COMPLETED', 'FAILED', 'CANCELLED', 'BUDGET_EXCEEDED') THEN NEW.finished_at := now(); END IF;
    ELSIF NEW.status_reason IS DISTINCT FROM OLD.status_reason THEN
        RAISE EXCEPTION 'run % status reason changes only with a status transition', OLD.id USING ERRCODE = '55000';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS experiment_runs_lifecycle_guard ON experiment_runs;
CREATE TRIGGER experiment_runs_lifecycle_guard
    BEFORE INSERT OR UPDATE ON experiment_runs
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_run_lifecycle();

-- Lifecycle event type.
ALTER TABLE run_events DROP CONSTRAINT IF EXISTS run_events_event_type_check;
ALTER TABLE run_events
    ADD CONSTRAINT run_events_event_type_check
    CHECK (event_type IN (
        'RUN_CREATED', 'ACCOUNT_CREATED', 'OBLIGATION_CREATED', 'LEDGER_COMMITTED',
        'OBLIGATION_PAID', 'JOURNAL_REVERSED', 'OBLIGATION_PAYMENT_REVERSED',
        'LEDGER_REPLAYED', 'POLICY_DENIED', 'FINANCE_ERROR', 'RUN_STATUS_CHANGED'));

-- The API may change status and reason only; timestamps are set by the trigger.
GRANT UPDATE (status, status_reason) ON TABLE public.experiment_runs TO finmind_api;

COMMIT;
