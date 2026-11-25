# Sample data

Synthetic exports of three mock-agent runs, for trying the export format,
the verification, and analysis code without running anything. No model or
provider was called; every name in them is fictitious; there is no real
money anywhere.

| File | Configuration (`configs.json`) | Ended |
|---|---|---|
| `s01-prudent.jsonl`, `.csv` | S01, `finmind-mock-v1`, seed 42 | COMPLETED, 38 actions |
| `s04-adversarial.jsonl`, `.csv` | S04, `finmind-mock-adversarial-v1`, seed 42 | COMPLETED, 48 actions |
| `s07-chaotic.jsonl`, `.csv` | S07, `finmind-mock-chaotic-v1`, seed 7, at most 25 actions | BUDGET_EXCEEDED, 26 actions (the last is the refused attempt) |

Mock agents ignore scenario messages, so these show the record's shape, not
how a model responds to S04's offer or S07's "administrator".

## Format

`finmind-export/v2` (ADR-0015; ADR-0018 added the outcomes record). The JSONL holds, in order: a
header (versions, redaction), the run with its configuration snapshot, every
event with the exact text it was hashed from (`hashInput`), every action with
its evidence label and response, every journal with its postings, the
wallets, the metrics, the outcomes, the classifications, and an end record with
`chainIntact`. The CSV lists every posting; its amounts sum to zero.

## Verify a file yourself

```python
import hashlib, json
records = [json.loads(line) for line in open("samples/s01-prudent.jsonl")]
previous = None
for event in (r for r in records if r["type"] == "event"):
    assert hashlib.sha256(event["hashInput"].encode()).hexdigest() == event["eventHash"]
    assert event["prevHash"] == previous
    previous = event["eventHash"]
print("chain intact")
```

## Regenerate

Run IDs, timestamps, and hashes differ every time; the figures do not. With
`.env` and `.env.api` set up as in the main README:

```sh
python3 services/agent-runtime/scripts/demo.py --no-ui --samples samples
```

`services/agent-runtime/tests/test_samples.py` checks these files, and, when
an API is available, that the same configurations give the same metrics.
