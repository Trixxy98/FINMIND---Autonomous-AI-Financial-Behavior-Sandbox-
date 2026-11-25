# Phase 5 exit gate: report

- Date: 25 November 2026
- Code revision: `f106862f36bc55548b0ee5cd9e3bd981d77b9b87` (a fresh clone from GitHub)
- Decision record: ADR-0017 step 7

## What the gate requires

Plan section 3: an independent developer can reproduce the mock results
locally; no real financial integration exists; all critical integrity tests
pass.

## Method

The repository was cloned from GitHub into a new, empty directory and set up
by following `README.md` only, with newly generated secrets. It ran in its own
Compose project (`finmind-gate`, PostgreSQL on port 55432), so no existing
database on the machine was used or touched. No model provider was called.
Afterwards the clone, its secrets, and its database volume were removed.

Two deviations from a literal reading of the README, both forced by the
setting, not by the steps:

- The `finmind_api` password was set through `psql` standard input rather
  than the interactive `\password` prompt.
- `COMPOSE_PROJECT_NAME` and `POSTGRES_PORT` were set in `.env`, because
  another copy of FINMIND was running on the same machine (finding 1).

## Results

| README section | What ran | Result |
|---|---|---|
| Quickstart 1 | Environment files from the examples | No placeholder left; all four ignored by git |
| Quickstart 2 | Compose up, 17 migrations, API role password | Healthy; 17 of 17 applied |
| Checks | `finmind_test` created and migrated | 17 of 17 applied |
| Checks | Ledger and API tests | 472 passed, 0 failed |
| Checks | Runtime tests without an API | 157 run, 77 skipped (need an API), 0 failed |
| Checks | Web: `npm ci`, tests, build | 61 passed; build succeeded |
| Checks | Crash drill | Passed: worker killed, API restarted (5 actions at restart, 38 at the end), two workers; no token in logs |
| Demo | `demo.py --no-ui --samples` | Three runs; metrics equal to the committed samples (machine-time fields aside), with different run IDs |
| Quickstart 3 | API with `dotnet run` | Healthy |
| Checks | Phase 4 gate | Passed; figures below |
| Checks | Runtime tests with the API | 157 run, 2 skipped (Anthropic SDK not installed), 0 failed |
| Quickstart 4 to 6 | Worker, web app, first S01 mock run through the web proxy | COMPLETED; wallets reconciled |

Phase 4 gate figures, against `docs/reports/phase4-exit-gate.md`:

| Metric | Phase 4 report | This clone |
|---|---|---|
| Factor between groups | `objective` | `objective` |
| Runs included per group | 3 and 3 | 3 and 3 |
| Final net worth | MYR 7,870.00 | MYR 7,870.00 |
| Net worth change | −MYR 2,130.00 | −MYR 2,130.00 |
| Actions per run | 38 | 38 |
| Integrity violations | 0 | 0 |
| Reproducible across two executions | yes | yes |
| Exports verified with `hashlib` | yes | yes |

No real financial integration: the dependency allowlist and integration scan
(`services/agent-runtime/tests/test_no_financial_integration.py`) passed in
the runtime tests above, along with the threat model and documentation checks.

Critical integrity tests: all ledger invariant, isolation, idempotency,
append-only, hash-chain, and recovery tests named in
`docs/security/threat-model.md` are in the suites above, which passed.

## Findings, and what changed

1. **Compose project name.** Compose names the project after the directory
   of the compose file, `infra`. A second checkout on the same machine would
   share the first one's container and database volume. `.env.example` now
   sets `COMPOSE_PROJECT_NAME=finmind`, and the README explains how to run a
   second copy. Existing `.env` files are unchanged, so an existing setup
   keeps its project name and its data. Adding the line to an existing `.env`
   would start a new, empty database: do not, unless that is intended.
2. **Migration notices.** A new database prints `NOTICE ... does not exist,
   skipping` for each trigger a migration replaces. They are harmless; the
   README now says so.
3. **Earlier, in step 5:** the old README started Compose without
   `--env-file .env`; fixed before this gate.

## Limitations

- One machine (Apple M4, macOS) and one person following the steps. Other
  operating systems were not tried; CI covers Linux for the tests and the
  drill, not the README steps.
- The web app was checked through its proxy with HTTP requests in this gate;
  its screens were checked in a browser in earlier steps (ADR-0014 to
  ADR-0017).
- Mock agents only. The Claude adapter is tested against the pinned SDK and
  a fake transport, not the live API.
