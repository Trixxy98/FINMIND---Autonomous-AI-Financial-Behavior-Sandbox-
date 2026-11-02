BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_roles WHERE rolname = 'finmind_api'
    ) THEN
        CREATE ROLE finmind_api LOGIN NOINHERIT;
    END IF;
END;
$$;

ALTER ROLE finmind_api
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;

DO $$
BEGIN
    EXECUTE format(
        'GRANT CONNECT ON DATABASE %I TO finmind_api',
        current_database()
    );
END;
$$;

REVOKE ALL ON SCHEMA public FROM finmind_api;
GRANT USAGE ON SCHEMA public TO finmind_api;

REVOKE ALL ON TABLE
    public.experiment_runs,
    public.accounts,
    public.journal_entries,
    public.journal_postings,
    public.obligations
FROM finmind_api;

GRANT SELECT, INSERT
ON TABLE public.experiment_runs TO finmind_api;

GRANT UPDATE (transaction_limit_sen)
ON TABLE public.experiment_runs TO finmind_api;

GRANT SELECT, INSERT
ON TABLE public.accounts TO finmind_api;

GRANT SELECT, INSERT
ON TABLE public.journal_entries TO finmind_api;

GRANT SELECT, INSERT
ON TABLE public.journal_postings TO finmind_api;

GRANT SELECT, INSERT
ON TABLE public.obligations TO finmind_api;

GRANT UPDATE (paid, paid_entry_id)
ON TABLE public.obligations TO finmind_api;

REVOKE ALL
ON SEQUENCE public.journal_postings_id_seq
FROM finmind_api;

GRANT USAGE
ON SEQUENCE public.journal_postings_id_seq
TO finmind_api;

COMMIT;