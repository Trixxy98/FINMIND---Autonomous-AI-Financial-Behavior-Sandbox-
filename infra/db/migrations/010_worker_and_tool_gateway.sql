-- ADR-0009: run-scoped worker sessions and recorded agent tool calls.
BEGIN;

CREATE TABLE IF NOT EXISTS run_sessions (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    token_hash BYTEA NOT NULL UNIQUE CHECK (length(token_hash) = 32),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ
);

-- At most one active session per run.
CREATE UNIQUE INDEX IF NOT EXISTS run_sessions_one_active_per_run
    ON run_sessions (run_id)
    WHERE revoked_at IS NULL;

CREATE TABLE IF NOT EXISTS tool_calls (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    tool_call_id TEXT NOT NULL CHECK (length(tool_call_id) BETWEEN 1 AND 100),
    tool_name TEXT NOT NULL CHECK (length(tool_name) BETWEEN 1 AND 100),
    arguments_redacted JSONB NOT NULL,
    request_hash TEXT NOT NULL,
    outcome TEXT,
    reason_code TEXT,
    journal_id UUID,
    response JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TIMESTAMPTZ,
    UNIQUE (run_id, tool_call_id),
    FOREIGN KEY (journal_id, run_id) REFERENCES journal_entries (id, run_id),
    CHECK ((completed_at IS NULL) = (response IS NULL AND outcome IS NULL))
);

-- A tool call is inserted once and completed once; nothing else changes and nothing is deleted.
CREATE OR REPLACE FUNCTION finmind_guard_tool_call()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'tool calls are append-only' USING ERRCODE = '55000';
    END IF;
    IF OLD.completed_at IS NOT NULL
       OR NEW.id IS DISTINCT FROM OLD.id OR NEW.run_id IS DISTINCT FROM OLD.run_id
       OR NEW.tool_call_id IS DISTINCT FROM OLD.tool_call_id OR NEW.tool_name IS DISTINCT FROM OLD.tool_name
       OR NEW.arguments_redacted IS DISTINCT FROM OLD.arguments_redacted OR NEW.request_hash IS DISTINCT FROM OLD.request_hash
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'tool call % can only be completed once', OLD.tool_call_id USING ERRCODE = '55000';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS tool_calls_guard ON tool_calls;
CREATE TRIGGER tool_calls_guard
    BEFORE UPDATE OR DELETE ON tool_calls
    FOR EACH ROW EXECUTE FUNCTION finmind_guard_tool_call();

ALTER TABLE run_events DROP CONSTRAINT IF EXISTS run_events_event_type_check;
ALTER TABLE run_events
    ADD CONSTRAINT run_events_event_type_check
    CHECK (event_type IN (
        'RUN_CREATED', 'ACCOUNT_CREATED', 'OBLIGATION_CREATED', 'LEDGER_COMMITTED',
        'OBLIGATION_PAID', 'JOURNAL_REVERSED', 'OBLIGATION_PAYMENT_REVERSED',
        'LEDGER_REPLAYED', 'POLICY_DENIED', 'FINANCE_ERROR', 'RUN_STATUS_CHANGED',
        'SCENARIO_MATERIALIZED', 'SCENARIO_EVENT_DELIVERED', 'CLOCK_ADVANCED',
        'TOOL_CALL_PROPOSED', 'TOOL_RETURNED', 'UNSUPPORTED_CAPABILITY_ATTEMPT',
        'FUNDING_REQUESTED', 'AGENT_PLAN_SUBMITTED', 'AGENT_FINAL_REPORT'));

ALTER TABLE run_events DROP CONSTRAINT IF EXISTS run_events_source_check;
ALTER TABLE run_events
    ADD CONSTRAINT run_events_source_check
    CHECK (source IN ('internal_api', 'scenario_engine', 'agent', 'worker', 'gateway'));

ALTER TABLE run_events DROP CONSTRAINT IF EXISTS run_events_outcome_check;
ALTER TABLE run_events
    ADD CONSTRAINT run_events_outcome_check
    CHECK (outcome IS NULL OR outcome IN (
        'ok', 'committed', 'replayed', 'malformed', 'denied', 'conflict', 'internal_error', 'unsupported_capability'));

REVOKE ALL ON TABLE public.run_sessions, public.tool_calls FROM finmind_api;
GRANT SELECT, INSERT ON TABLE public.run_sessions TO finmind_api;
GRANT UPDATE (expires_at, revoked_at) ON TABLE public.run_sessions TO finmind_api;
GRANT SELECT, INSERT ON TABLE public.tool_calls TO finmind_api;
GRANT UPDATE (outcome, reason_code, journal_id, response, completed_at) ON TABLE public.tool_calls TO finmind_api;

COMMIT;
