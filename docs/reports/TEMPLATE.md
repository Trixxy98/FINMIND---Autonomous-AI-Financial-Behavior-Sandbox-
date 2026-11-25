# [Title: what was compared, in plain words]

- Date:
- Author:
- Code revision: (from the run snapshots)
- Batches: (IDs)
- Status: draft / reviewed

Replace every bracketed note. Delete a section only if it truly does not
apply, and say why. See `docs/RESEARCHER_GUIDE.md` for how to read each
figure.

## Question

[One sentence. What was varied, and what was observed.]

## Setup

| | Group 1 | Group 2 |
|---|---|---|
| Factor varied | | |
| Scenario and version (hash) | | |
| Objective | | |
| Agent: provider, model ID, settings | | |
| Prompt version | | |
| Seed(s) | | |
| Starting balance, transaction limit | | |
| Budgets: actions, tokens, cost, wall time, days | | |
| Runs requested | | |

Versions in every snapshot: `run-config/`…, `finmind-tools/`…,
`finance-policy/`…, `finance-reasons/`…, pricing …. Results use
`finmind-metrics/`… and `finmind-compare/`….

Differences the comparison reported: between groups [list; should be the
factor only], within groups [list; should be none], metadata [list].

## Runs included

| | Group 1 | Group 2 |
|---|---|---|
| Included (n) | | |
| Excluded, with reasons | | |
| Ended by budget | | |

## Results

[Per metric: mean, the raw values per run, and the denominator for rates.
No interval below n = 5. State "not applicable" where a denominator is zero.]

| Metric (version) | Group 1 | Group 2 |
|---|---|---|
| | | |

Provider spend: [total cost recorded, and per run].

## Human review

[Candidates reviewed (Q1 to Q4), by whom, the labels chosen, and any
disagreement. Candidates not reviewed are not findings.]

## Observations

[What the record shows, as counts in this configuration: "in 3 of 5 runs, …".
No claims about intent, understanding, or general model traits.]

## Limitations

- Runs per group and what that allows.
- Provider nondeterminism; what was held fixed and what could not be.
- Anything the metrics do not capture.
- Any platform failure, and its effect.

## Reproduce

[Configuration (copy from a run), batch IDs, code revision, and the export
files with their chain verification.]
