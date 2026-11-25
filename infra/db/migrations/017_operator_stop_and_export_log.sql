-- ADR-0017 step 2: an operator switch that stops every run, and a record of every export request.
-- Both tables are append-only: the current state is the latest row; history is never edited.
BEGIN;

CREATE TABLE IF NOT EXISTS operator_switch_log (
    id UUID PRIMARY KEY,
    seq BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    action TEXT NOT NULL CHECK (action IN ('stop', 'resume')),
    reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
    source TEXT NOT NULL CHECK (length(source) BETWEEN 1 AND 100),
    cancelled_runs INT NOT NULL DEFAULT 0 CHECK (cancelled_runs >= 0),
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS export_log (
    id UUID PRIMARY KEY,
    seq BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    run_id UUID NOT NULL,  -- not a foreign key: a request for a run that does not exist is recorded too
    format TEXT NOT NULL CHECK (length(format) BETWEEN 1 AND 20),
    outcome TEXT NOT NULL CHECK (outcome IN ('served', 'not_found', 'invalid_format')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
);

DROP TRIGGER IF EXISTS operator_switch_log_append_only ON operator_switch_log;
CREATE TRIGGER operator_switch_log_append_only
    BEFORE UPDATE OR DELETE ON operator_switch_log
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_ledger_mutation();

DROP TRIGGER IF EXISTS export_log_append_only ON export_log;
CREATE TRIGGER export_log_append_only
    BEFORE UPDATE OR DELETE ON export_log
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_ledger_mutation();

REVOKE ALL ON TABLE public.operator_switch_log FROM finmind_api;
GRANT SELECT, INSERT ON TABLE public.operator_switch_log TO finmind_api;
REVOKE ALL ON TABLE public.export_log FROM finmind_api;
GRANT SELECT, INSERT ON TABLE public.export_log TO finmind_api;

COMMIT;
