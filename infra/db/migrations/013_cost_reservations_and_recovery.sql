-- ADR-0013 A1: cost reservations before paid model calls, and recovery of runs whose worker was lost.
BEGIN;

-- Worst-case cost of model calls that have no usage record yet. It counts toward the cost cap.
-- An attempt whose billing is unknown (timeout, crash) stays reserved.
ALTER TABLE experiment_runs
    ADD COLUMN IF NOT EXISTS model_cost_reserved_micro_usd BIGINT NOT NULL DEFAULT 0
        CHECK (model_cost_reserved_micro_usd >= 0);

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
        'MODEL_INVOCATION_COMPLETED', 'MODEL_COST_RESERVED', 'RUN_RECLAIMED'));

CREATE INDEX IF NOT EXISTS run_sessions_unrevoked_expiry_idx
    ON run_sessions (expires_at) WHERE revoked_at IS NULL;

GRANT UPDATE (model_cost_reserved_micro_usd) ON TABLE public.experiment_runs TO finmind_api;

COMMIT;
