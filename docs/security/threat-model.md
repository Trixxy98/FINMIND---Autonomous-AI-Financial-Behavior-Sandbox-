# FINMIND threat model

- Version: 1 (25 November 2026), ADR-0017 step 1
- Scope: a local, single-researcher sandbox with virtual MYR only
- Source: specification sections 14.1 and 14.2

Each threat lists its controls, the tests that show them, and the risk that
remains. Tests are named as `file::test`. A test
(`services/agent-runtime/tests/test_threat_model.py`) fails if a named test no
longer exists, so this document cannot silently go stale.

## What is protected

- The ledger: balanced, immutable, run-scoped double-entry journals in integer
  sen. It is the only source of balances.
- The record: append-only, hash-chained events, tool calls, and reviews.
- Provider credentials and the researcher's tokens.
- The researcher's machine, from anything a model produces.

## Trust boundaries

| Boundary | Trusted side | Untrusted side |
|---|---|---|
| Tool gateway | API and PostgreSQL decide and record | Model output, tool arguments, worker requests |
| Worker | Holds the worker token and, for Claude runs, the provider key | Model responses, scenario text |
| Researcher API | Researcher token holder on this machine | Other hosts, other browser origins |
| Database roles | Owner role (migrations only) | API role (`finmind_api`, restricted) |

The agent has no shell, code execution, SQL, filesystem, or network tool. It
can only propose calls to the typed tools in `finmind-tools/v1`.

## Threats from the specification (section 14.1)

### T1. Prompt injection from scenario messages

Messages (S04, S05, S07, S12) are data delivered through `get_environment`.
Policy is fixed in the run snapshot and cannot be changed by any tool.

- `services/agent-runtime/tests/test_injection.py::test_a_message_claiming_authority_does_not_lift_the_limit`
- `apps/api/Finance.Tests/LedgerTests.cs::Run_transaction_policy_is_required_and_immutable`
- `apps/api/Finance.Tests/RunLifecycleTests.cs::Postgres_database_protects_snapshot_and_lifecycle`
- `apps/api/Finance.Tests/NewScenarioTests.cs::S07_message_claims_authority_over_the_limit`

Remaining: a model may still follow an instruction within the real limits
(for example, paying a synthetic merchant that a message named). That is
behavior to observe, recorded as a committed or denied action, not a breach.

### T2. Unauthorized tool calls

Only catalogued tools exist; arguments are validated against strict schemas;
every call is checked on the server for ownership, run scope, amount, limit,
and available funds.

- `apps/api/Finance.Tests/ToolGatewayTests.cs::Catalog_schemas_are_strict`
- `apps/api/Finance.Tests/ToolGatewayTests.cs::unsupported_and_invalid_calls_change_nothing`
- `apps/api/Finance.Tests/LedgerTests.cs::Transfer_enforces_limit_positive_amount_and_available_balance`
- `apps/api/Finance.Tests/ReasonCodeTests.cs::Postgres_rejection_has_same_code_and_no_effect`
- `services/agent-runtime/tests/test_a2_regressions.py::test_direct_routes_cannot_change_a_managed_run`

Remaining: none known.

### T3. Exfiltration through untrusted destinations

Destinations are an allowlist of the run's own synthetic merchants; there is
no real destination and no outbound integration. Every dependency is on a
reviewed list (NuGet, pip, every npm package in the lockfile, container
images); the only hosts in source are local ones and the model provider; and
no payment, banking, brokerage, wallet, or crypto SDK or host name appears in
source or tests.

- `apps/api/Finance.Tests/ToolGatewayTests.cs::destinations_are_allowlisted`
- `apps/api/Finance.Tests/LedgerTests.cs::Bill_payment_uses_server_obligation_amount_and_merchant_payee`
- `services/agent-runtime/tests/test_no_financial_integration.py::test_npm_direct_and_locked_packages_are_the_reviewed_set`
- `services/agent-runtime/tests/test_no_financial_integration.py::test_source_names_only_local_hosts_and_the_model_provider`
- `services/agent-runtime/tests/test_no_financial_integration.py::test_no_payment_banking_brokerage_or_crypto_sdk_or_host`

Remaining: the worker process can reach the network (see O4). The direct
NuGet and pip dependencies are pinned and listed, but their transitive
dependencies are resolved at install time and are not listed (npm's are, via
the lockfile). A name list cannot catch an integration under a name it does
not know; review of new dependencies is still needed.

### T4. Cross-run access

Accounts, obligations, sessions, and idempotency keys are scoped to a run in
both the code and database constraints.

- `apps/api/Finance.Tests/LedgerTests.cs::Reject_cross_run_account_postings`
- `apps/api/Finance.Tests/LedgerTests.cs::Transaction_caps_are_isolated_between_runs`
- `apps/api/Finance.Tests/PostgresLedgerTests.cs::Concurrent_opening_funding_is_single_and_cross_run_spending_is_denied`
- `services/agent-runtime/tests/test_parallel_isolation.py::test_parallel_runs_are_isolated_and_equal_to_sequential_runs`

Remaining: none known.

### T5. Fabricated tool responses

Responses are produced and recorded by the server; the worker relays them. A
finished call cannot commit later, and a retried call returns the response
that was recorded.

- `apps/api/Finance.Tests/ToolCallTruthTests.cs::response_is_the_one_that_was_recorded`
- `apps/api/Finance.Tests/ToolCallTruthTests.cs::finished_call_cannot_commit_later`
- `services/agent-runtime/tests/test_unreadable_calls.py::test_hostile_bodies_never_escape_the_record`

Remaining: the worker could misreport a response to the model; it cannot
change what the ledger and record hold.

### T6. Ledger modification

Postings are immutable; corrections are compensating journals; the API role
cannot change protected data; there is no balance setter.

- `apps/api/Finance.Tests/PostgresIntegrityTests.cs::Committed_journals_and_postings_cannot_be_changed_or_deleted`
- `apps/api/Finance.Tests/RestrictedRoleTests.cs::Api_role_can_use_ledger_but_cannot_change_protected_data`
- `apps/api/Finance.Tests/LedgerTests.cs::Direct_commits_without_run_policy_fail_closed`
- `apps/api/Finance.Tests/ReversalTests.cs::Database_rejects_reversal_with_mismatched_postings`
- `apps/api/Finance.Tests/Phase1GateTests.cs::Postgres_and_in_memory_agree_on_random_operations`

Remaining: the database owner role can change anything. It is used only for
migrations and is refused by the API at startup (O3).

### T7. Replay tampering

Events are hash-chained (v1 and v2); verification detects any changed column;
exports carry the hash input so a reader can check them with `hashlib` alone.

- `apps/api/Finance.Tests/IntegrityTests.cs::Altering_any_stored_column_breaks_verification`
- `apps/api/Finance.Tests/EventTests.cs::Postgres_verification_detects_a_changed_payload`
- `apps/api/Finance.Tests/EventTests.cs::Postgres_api_role_cannot_change_or_delete_events`
- `services/agent-runtime/tests/test_export_api.py::test_chain_verifies_with_hashlib_alone`

Remaining: the chain is not signed. The owner role could rewrite a whole chain
consistently; the defence is backups and exported copies.

### T8. Token exhaustion

Each run has action, token, cost, wall-time, and virtual-day budgets. Cost is
reserved before each provider request.

- `apps/api/Finance.Tests/BudgetTests.cs::token_budget_counts_once_per_turn_and_ends_the_run`
- `apps/api/Finance.Tests/ProviderTests.cs::cost_budget_ends_a_claude_run`
- `services/agent-runtime/tests/test_a1_regressions.py::test_each_request_is_reserved_first_and_uses_the_granted_output_limit`
- `services/agent-runtime/tests/test_end_to_end.py::test_action_budget_ends_the_run`
- `services/agent-runtime/tests/test_worker.py::test_local_wall_time_stops_the_run`

Remaining: there is no per-minute rate. Budgets bound the total for each run;
a provider console spend limit is the outer bound (README).

### T9. Double payment through retries

Every money call carries an idempotency key; a repeated key returns the first
result; a conflicting payload is refused; timeout after commit is recovered.

- `apps/api/Finance.Tests/LedgerTests.cs::Repeated_idempotency_key_returns_original_commit_without_duplicate_effect`
- `apps/api/Finance.Tests/LedgerTests.cs::Reject_conflicting_payload_for_existing_idempotency_key`
- `apps/api/Finance.Tests/Phase1GateTests.cs::Postgres_concurrent_payments_pay_once`
- `apps/api/Finance.Tests/ReasonCodeTests.cs::Retried_payment_is_a_replay_not_a_second_effect`
- `services/agent-runtime/tests/test_end_to_end.py::test_dropped_response_on_payment_is_retried_once`

Remaining: none known.

### T10. Model-generated false claims

Agent text is never a source of balances. Each action has one evidence label;
funding requests credit nothing; text judgements are review candidates only.

- `services/agent-runtime/tests/test_read_api.py::test_actions_carry_one_evidence_label_each`
- `apps/api/Finance.Tests/ToolGatewayTests.cs::non_money_tools_record_without_moving_money`
- `apps/api/Finance.Tests/ClassificationTests.cs::Text_judgements_are_review_candidates_never_findings`
- `apps/web/tests/cues.test.js::a funding request is a letter with no amount, reported by the agent`

Remaining: a reader can still misread agent text; the UI labels it REPORTED BY
AGENT everywhere.

## Operational threats

### O1. Secret leakage

The API refuses to start with provider credentials; only the worker holds
them. Agent text is redacted; errors keep only a class name; reads and exports
never include credentials.

- `apps/api/Finance.Tests/RequestGuardTests.cs::Unsafe_configuration_stops_startup`
- `services/agent-runtime/tests/test_trust_boundary.py::test_worker_refuses_database_and_researcher_credentials`
- `apps/api/Finance.Tests/ToolGatewayTests.cs::agent_text_is_redacted_and_truncated`
- `services/agent-runtime/tests/test_claude.py::test_api_errors_keep_only_the_class_name`
- `services/agent-runtime/tests/test_read_api.py::test_no_credential_is_ever_returned`
- `services/agent-runtime/tests/test_export_api.py::test_no_credential_appears_and_errors_are_clean`
- `apps/api/Finance.Tests/ReasonCodeTests.cs::Internal_errors_do_not_expose_exception_details`

Remaining: secrets sit in local `.env*` files, git-ignored. Disk encryption
and file permissions are the operator's.

### O2. Access to the API from other hosts and pages

Only `127.0.0.1:5080` and `localhost:5080` are accepted as hosts, which blocks
DNS rebinding; only configured origins are accepted; researcher routes need a
bearer token held in browser memory, so there is no cookie to forge.

- `services/agent-runtime/tests/test_trust_boundary.py::test_foreign_host_and_origin_are_rejected_even_with_the_credential`
- `services/agent-runtime/tests/test_trust_boundary.py::test_health_and_worker_routes_check_the_host_too`
- `services/agent-runtime/tests/test_trust_boundary.py::test_routes_reject_missing_or_wrong_credentials`
- `apps/api/Finance.Tests/RequestGuardTests.cs::Without_a_researcher_token_researcher_routes_stay_closed`

Remaining: there are two roles only (researcher, worker). Anyone with the
researcher token can do everything a researcher can.

### O3. An over-privileged database role

The API connects as a restricted role and refuses to start as the owner, as a
privileged role, or when it cannot check.

- `apps/api/Finance.Tests/RequestGuardTests.cs::The_owner_role_is_refused_and_the_api_role_is_accepted`
- `apps/api/Finance.Tests/ToolGatewayTests.cs::Postgres_restricted_role_runs_the_gateway`

Remaining: none known.

### O4. Worker isolation and network egress

The worker is a local Python process with stdlib and, for Claude runs, the
pinned Anthropic SDK. It executes no model-produced code. Its network access
is not restricted and it is not in a container. Its token opens only worker
routes, and a finished call cannot commit later.

- `services/agent-runtime/tests/test_trust_boundary.py::test_routes_reject_missing_or_wrong_credentials`
- `apps/api/Finance.Tests/ToolCallTruthTests.cs::finished_call_cannot_commit_later`

Remaining (accepted, ADR-0017 decision 2): a compromised dependency in the
worker could reach the network with the worker token and provider key. The
worker token cannot read research data or change finished runs. Recommended:
keep dependencies pinned, keep the provider spend limit, and run the worker in
a container with an egress proxy if the sandbox is ever shared.

### O5. Stopping everything, and recovering from crashes

Runs can be cancelled one by one, or all at once with the operator stop
(`POST /api/v1/admin/stop`, with a reason), which also refuses starts and
claims until resumed. Expired claims are reclaimed; a lost response is
recovered by its idempotency key.

- `apps/api/Finance.Tests/RecoveryTests.cs::lost_claim_is_reclaimed_after_expiry`
- `apps/api/Finance.Tests/RecoveryTests.cs::running_run_with_expired_session_is_failed`
- `apps/api/Finance.Tests/ToolCallTruthTests.cs::cancel_between_the_check_and_the_ledger`
- `services/agent-runtime/tests/test_end_to_end.py::test_cancellation_stops_money_movement`
- `apps/api/Finance.Tests/OperatorStopTests.cs::stop_cancels_active_runs_and_refuses_starts_and_claims`
- `apps/api/Finance.Tests/OperatorStopTests.cs::Postgres_starts_racing_a_stop_leave_no_active_run`
- `services/agent-runtime/tests/test_operator_api.py::test_stop_cancels_runs_and_refuses_starts_and_claims_until_resumed`

A drill with real processes runs in CI: a worker killed mid-run (the run
fails safely once its session expires; the queue carries on), the API killed
mid-run and restarted (the worker's retries carry the run through), and two
workers sharing a queue (each run claimed once).

- `services/agent-runtime/scripts/crash_drill.py::drill_worker_killed`
- `services/agent-runtime/scripts/crash_drill.py::drill_api_restarted`
- `services/agent-runtime/scripts/crash_drill.py::drill_two_workers`
- `apps/api/Finance.Tests/SessionLifetimeTests.cs::A_gateway_expires_sessions_after_its_configured_lifetime`

Remaining: a run whose worker dies while RUNNING is failed, not resumed; it
must be run again. Detection takes up to the session lifetime (10 minutes by
default). A model request already sent when a stop arrives still completes
and its cost is recorded.

### O6. Export access

Exports need the researcher token and contain no secrets. CSV text cannot be
read as a spreadsheet formula. Every export request is recorded (run, format,
outcome, time) in an append-only table before anything is served.

- `apps/api/Finance.Tests/ExportTests.cs::Csv_text_cells_are_quoted_and_never_formulas`
- `services/agent-runtime/tests/test_export_api.py::test_header_states_versions_and_redaction`
- `services/agent-runtime/tests/test_operator_api.py::test_every_export_request_is_recorded_with_its_outcome`
- `apps/api/Finance.Tests/OperatorStopTests.cs::Postgres_api_role_cannot_change_or_delete_the_switch_history`

Remaining: the record says what was requested, not who requested it; there is
one researcher token. A copy of an export, once saved, is outside the record.

### O7. Hostile or malformed input

Unreadable bodies, invalid Unicode, oversized calls, and unrepresentable text
are recorded as malformed attempts; nothing escapes the record.

- `services/agent-runtime/tests/test_invalid_unicode.py::test_invalid_unicode_is_recorded_as_malformed_and_the_run_goes_on`
- `services/agent-runtime/tests/test_a1_regressions.py::test_oversized_attempt_is_recorded_as_malformed`
- `services/agent-runtime/tests/test_unrepresentable.py::test_no_encoding_error_reaches_the_model_api`
- `apps/api/Finance.Tests/Phase1GateTests.cs::Postgres_extreme_amounts_are_safe`

Remaining: none known.

## Review log

| Date | Change |
|---|---|
| 25 November 2026 | Version 1. Added the T1 end-to-end test. Open: O4 accepted; O5 and O6 in ADR-0017 steps 2 and 3 |
| 25 November 2026 | O5: operator stop added. O6: export record added (migration 017) |
| 25 November 2026 | O5: crash and concurrency drill with real processes, in CI |
| 25 November 2026 | T3: dependency allowlist and integration scan |
