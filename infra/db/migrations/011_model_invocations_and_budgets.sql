-- ADR-0011: model invocation events and the server-enforced token budget.
BEGIN;

ALTER TABLE experiment_runs
    ADD COLUMN IF NOT EXISTS model_tokens_used BIGINT NOT NULL DEFAULT 0 CHECK (model_tokens_used >= 0);

CREATE OR REPLACE FUNCTION finmind_guard_model_tokens()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.model_tokens_used < OLD.model_tokens_used THEN
        RAISE EXCEPTION 'run % token usage cannot decrease', OLD.id USING ERRCODE = '55000';
    END IF;
    IF NEW.model_tokens_used <> OLD.model_tokens_used AND NEW.status IS NULL THEN
        RAISE EXCEPTION 'run % is unmanaged and has no token budget', OLD.id USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS experiment_runs_model_tokens_guard ON experiment_runs;
CREATE TRIGGER experiment_runs_model_tokens_guard
    BEFORE UPDATE OF model_tokens_used ON experiment_runs
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_model_tokens();

ALTER TABLE run_events DROP CONSTRAINT IF EXISTS run_events_event_type_check;
ALTER TABLE run_events
    ADD CONSTRAINT run_events_event_type_check
    CHECK (event_type IN (
        'RUN_CREATED', 'ACCOUNT_CREATED', 'OBLIGATION_CREATED', 'LEDGER_COMMITTED',
        'OBLIGATION_PAID', 'JOURNAL_REVERSED', 'OBLIGATION_PAYMENT_REVERSED',
        'LEDGER_REPLAYED', 'POLICY_DENIED', 'FINANCE_ERROR', 'RUN_STATUS_CHANGED',
        'SCENARIO_MATERIALIZED', 'SCENARIO_EVENT_DELIVERED', 'CLOCK_ADVANCED',
        'TOOL_CALL_PROPOSED', 'TOOL_RETURNED', 'UNSUPPORTED_CAPABILITY_ATTEMPT',
        'FUNDING_REQUESTED', 'AGENT_PLAN_SUBMITTED', 'AGENT_FINAL_REPORT',
        'MODEL_INVOCATION_COMPLETED'));

-- Lookup for idempotent model invocation retries (one event per run and turn).
CREATE INDEX IF NOT EXISTS run_events_trace_idx ON run_events (run_id, event_type, trace_id) WHERE trace_id IS NOT NULL;

GRANT UPDATE (model_tokens_used) ON TABLE public.experiment_runs TO finmind_api;

COMMIT;
