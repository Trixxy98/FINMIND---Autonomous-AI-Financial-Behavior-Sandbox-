-- ADR-0015 step 3: human review decisions on classification items. Labels are derived; only decisions are stored.
BEGIN;

CREATE TABLE IF NOT EXISTS classification_reviews (
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES experiment_runs(id),
    item_key TEXT NOT NULL CHECK (length(item_key) BETWEEN 1 AND 100),
    classification_version TEXT NOT NULL,
    label TEXT NOT NULL CHECK (label IN (
        'NORMAL_ACTION', 'REQUEST_FOR_CLARIFICATION', 'POLICY_DENIAL', 'UNSUPPORTED_CAPABILITY_ATTEMPT',
        'UNAUTHORIZED_FUNDING_REQUEST', 'BALANCE_MISREPORT', 'DUPLICATE_PAYMENT_ATTEMPT',
        'POTENTIAL_PROMPT_INJECTION_COMPLIANCE', 'RECONCILIATION_ATTEMPT', 'MODEL_ERROR', 'UNKNOWN_REVIEW')),
    note TEXT CHECK (note IS NULL OR length(note) <= 2000),
    reviewer TEXT NOT NULL CHECK (length(reviewer) BETWEEN 1 AND 100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS classification_reviews_run_idx ON classification_reviews (run_id, item_key, created_at);

-- A changed decision is a new row, so the history stays visible.
DROP TRIGGER IF EXISTS classification_reviews_append_only ON classification_reviews;
CREATE TRIGGER classification_reviews_append_only
    BEFORE UPDATE OR DELETE ON classification_reviews
    FOR EACH ROW EXECUTE FUNCTION finmind_reject_ledger_mutation();

REVOKE ALL ON TABLE public.classification_reviews FROM finmind_api;
GRANT SELECT, INSERT ON TABLE public.classification_reviews TO finmind_api;

COMMIT;
