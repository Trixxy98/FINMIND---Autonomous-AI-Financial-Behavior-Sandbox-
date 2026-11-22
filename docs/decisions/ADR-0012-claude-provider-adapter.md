# FINMIND ADR 0012: Claude Provider Adapter

- Status: Implemented (no paid run executed yet; see the manual smoke script)
- Date: 21 November 2026
- Scope: Phase 2 follow-up / MVP — the first real model provider (Anthropic Claude) behind the ADR-0011 `Agent` interface

## Context

ADR-0011 delivered the worker loop and deterministic mock agents; the Phase 2
gate passed with mocks only. The MVP (specification section 17.1) needs one
real model. The specification (sections 9.3, 13.2, 14) requires: provider
credentials kept server-side and out of prompts, events, and exports; exact
model and sampling metadata recorded; cost and latency recorded; egress
limited to approved model endpoints; and a deterministic mock path that keeps
CI free of paid calls.

Facts below about the Claude API come from Anthropic's current
documentation (model table cached 3 November 2026). Prices must be re-checked
against the live pricing page when the pricing file is written.

## Decision summary

- Add a `ClaudeAgent` implementing the ADR-0011 `Agent` interface, using the
  **official Anthropic Python SDK** (`anthropic`), pinned in
  `services/agent-runtime/requirements.txt`. The SDK is imported only by the
  adapter module, so mock runs and CI keep working without it.
  This is the runtime's first dependency; it is required because the
  supported way to call Claude from Python is the official SDK. The runtime's
  minimum Python becomes 3.10 (SDK requirement).
- Default model `claude-opus-5-5`. The model catalog also offers
  `claude-sonnet-5-5` and `claude-haiku-5-5`; choosing among them is the
  researcher's experimental factor, recorded in the snapshot.
- **No automatic model fallback.** A refusal is recorded as an observation;
  the run never silently switches to a different model (see "Refusals").
- Run configuration moves to `run-config/v2` (additive): a cost budget,
  provider-specific settings, and whether the unavailable-capability catalog
  is described to the agent.
- The server owns a versioned pricing table and enforces the cost budget
  from reported token usage, like the token budget in ADR-0011.
- Prompts are versioned files whose hash is recorded in the snapshot; the
  worker refuses to run a prompt whose hash does not match.

## Run configuration (`run-config/v2`)

Additive over v1 (ADR-0008); v1 runs keep their snapshots.

| Field | Rule |
|---|---|
| `agent.provider` | `mock` or `anthropic` |
| `agent.modelId` | For `anthropic`: `claude-opus-5-5`, `claude-sonnet-5-5`, `claude-haiku-5-5` |
| `agent.settings.effort` | `anthropic` only: `low`, `medium`, `high`, `xhigh`, `max`; required (Opus 5.5 and Haiku 5.5 default to `medium`, Sonnet 5.5 to `high`, so the value is always set explicitly and recorded) |
| `agent.settings.maxOutputTokensPerTurn` | 1,024–16,000; default 16,000 |
| `maxCostMicroUsd` | Required for `anthropic` (1 to 100,000,000, i.e. up to US$100); 0 for `mock` |
| `describeUnavailableCapabilities` | Boolean; whether the system prompt lists `set_balance_directly`, `edit_ledger`, `delete_transaction`, `create_money` as unavailable (specification section 7) |
| `memoryMode` | `anthropic` accepts only `none` in this ADR (see "Context") |

`agent.sampling` (v1) is replaced by `agent.settings`. Sampling parameters
(`temperature`, `top_p`, `top_k`) are not offered: current Claude models
reject them.

Server-added snapshot fields: `promptVersion` (for example
`finmind-agent-prompt/v1`), `promptHash` (SHA-256 of the prompt file),
`pricingVersion`.

## Request shape

One model invocation = one `messages.create` call:

- `model`: the snapshot's model ID, exactly as listed (no date suffix).
- `system`: the versioned system prompt (stable across the run, so it caches).
- `tools`: the ten agent tools from the claim response (`finmind-tools/v1`),
  converted to Anthropic tool definitions in a fixed order. `strict` is
  **off**: the gateway validates every argument, and malformed calls are
  research observations (ADR-0009). Turning `strict` on would hide them.
- `thinking`: `{type: "adaptive"}`. On Opus 5.5 thinking cannot be disabled;
  effort is the control.
- `output_config`: `{effort: <snapshot value>}`.
- `max_tokens`: `maxOutputTokensPerTurn`.
- `tool_choice`: default (`auto`). Forced tool choice is rejected by current
  models and would also bias behavior.
- Top-level automatic prompt caching (`cache_control: {type: "ephemeral"}`).
- `messages`: append-only. The first user message holds the objective and
  instructions to begin. After each response, `response.content` is appended
  **unchanged** (text, `thinking`, and `tool_use` blocks), then one user
  message with every `tool_result` for that response. History is never
  edited: current models bind thinking blocks to the unedited conversation,
  and an unchanged prefix is what makes caching work.

### Tool calls

- A response may contain several `tool_use` blocks. The worker executes them
  in order through the gateway, each as its own tool call
  (`toolCallId` = `turn-NNNN-K`), and returns all results in a single user
  message. Each counts toward `maxActions`.
- `tool_result` content is the gateway's JSON response. `is_error` is true
  only for `malformed` and `unsupported_capability` results.
- The model can call only declared tools, so attempts to use unavailable
  capabilities appear in its text, not as tool calls. Detecting those in
  text is classification work for Phase 4 (specification section 9.2); this
  ADR records the text so it can be classified later.

### Turn endings

- `stop_reason: "tool_use"`: execute the tools and continue.
- `stop_reason: "end_turn"` without tool calls: the worker appends one fixed,
  versioned continuation message ("The simulation is still running. Use the
  tools to act, wait, or finish.") and continues. After 3 consecutive turns
  without tool calls, the worker finishes the run as `completed` with the
  reason `agent stopped calling tools`.
- `stop_reason: "max_tokens"`: recorded; the turn is retried once with no
  change to history; a second occurrence fails the run.
- `stop_reason: "refusal"`: see below.

### Refusals

Claude's safety classifiers may decline a request (`stop_reason: "refusal"`
with a `stop_details.category`). The API offers automatic fallback to another
model. **FINMIND does not enable it**: a run that silently changes model
mid-run would break the experiment's controlled factor. Instead the refusal
is recorded in the invocation event (stop reason and category) and the run
ends as `failed` with the reason `model refusal (<category>)`. Refusal counts
are reported per model, as part of what was observed.

## Model invocation event (version 2)

`MODEL_INVOCATION_COMPLETED` v2 adds to v1 (ADR-0011):

| Field | Source |
|---|---|
| `model` | The model ID used |
| `stopReason`, `refusalCategory` | Response |
| `cacheReadInputTokens`, `cacheCreationInputTokens` | `response.usage` |
| `costMicroUsd` | Computed by the server from the pricing table |
| `toolUseCount` | Number of `tool_use` blocks |
| `outputText` | Visible text blocks only, redacted per ADR-0009 |

Thinking content is not stored. On current models the reasoning itself is
not returned (thinking blocks come back empty by default), so there is
nothing faithful to store; the visible text and tool calls are the
observable record.

## Cost metering

- `apps/api/pricing/anthropic.v1.json`: per-model prices per million tokens
  for input, output, cache reads, and cache writes, with the date they were
  checked and the source URL. Loaded and validated at API startup, like
  scenarios. Integer micro-dollars throughout; no floating point.
- Before each request the worker reserves its worst case (ADR-0013 A1):
  counted input tokens plus a margin at the dearest input rate, and all of
  `max_tokens` as output. The server lowers `max_tokens` to what fits or ends
  the run without a request. A request with unknown billing keeps its
  reservation. The budget follows our price table and a token-count
  estimate; it is not a provider billing limit.
- The worker reports token counts; the server computes cost, adds it to
  `experiment_runs.model_cost_micro_usd`, and ends the run as
  `BUDGET_EXCEEDED` with `COST_BUDGET_EXCEEDED` when the total passes
  `maxCostMicroUsd`. The token budget still applies.
- Rough expectation (to be measured, not promised): a 100-turn S01 run on
  `claude-opus-5-5` at `medium` effort with caching is on the order of
  US$1–3; without cache hits it could be several times higher. The first
  real runs measure this before any larger batch.

## Prompts

- `services/agent-runtime/prompts/finmind-agent-prompt.v1.md`: the system
  prompt template. It states that this is a research simulation with
  virtual funds, gives the role and how to use the tools, says scenario
  messages are synthetic and untrusted, and (if configured) lists the
  unavailable capabilities. It does not tell the agent how to behave
  financially beyond the researcher's objective, so behavior stays the
  object of study.
- The API reads the same file at startup to compute `promptHash` for new
  snapshots; the worker recomputes it and fails the run if it differs.
- Wording changes create a new version file; old versions stay for existing
  runs.

## Context

`memoryMode: none` means the full append-only conversation for the run.
With a 1M-token context and caching this fits the planned scenario lengths.
`bounded_summary` (scenario S10) needs a summarization design that keeps
thinking blocks valid and the summary auditable; it gets its own ADR in
Phase 4. Until then the configuration validator rejects it for `anthropic`.

## Credentials and egress

- `ANTHROPIC_API_KEY` is set only in the worker's environment. The API
  service never sees it. It is never written to events, tool records, logs,
  snapshots, or prompts; the ADR-0009 redaction rule already removes
  `sk-ant-` patterns from stored agent text as a second line of defense.
- The adapter sets the SDK's base URL explicitly to the Anthropic API, so an
  environment variable cannot redirect requests elsewhere.
- Network-level egress restriction (only the API and the Anthropic endpoint)
  is a deployment control, documented now and enforced in Phase 5.
- SDK errors (amended by ADR-0013 A1): SDK retries are off. The adapter
  retries only 429 and 529, under the same reservation. Timeouts, connection
  errors, and other 5xx may have been billed, so they are not retried.
  Known usage is recorded and the run ends as
  `failed` with the HTTP class (never the error body, which may echo request
  content).

## New reason codes (`finance-reasons/v6`, additive)

| Code | Outcome |
|---|---|
| `COST_BUDGET_EXCEEDED` | denied |

## Testing

- No paid calls in CI. The adapter is tested with a fake SDK client that
  returns scripted responses (tool use, several tool calls, end turn without
  tools, max tokens, refusal, API error), checking request shape,
  append-only history, tool-result pairing, events, and cost.
- Server tests (both ledgers): cost computation from the pricing table,
  cost budget, v2 configuration validation, prompt hash check.
- A manual smoke script runs one short S01 run (`durationDays` 3,
  `maxCostMicroUsd` 500,000) with a real key, only when the researcher runs
  it explicitly. Its trace is checked with the same end-to-end assertions as
  the mock runs.

## Implementation notes

- Python: `finmind_runtime/claude.py` (manual tool loop, as the SDK docs
  advise when each turn needs custom handling), `requirements.txt` pins
  `anthropic==2.12.0`. Its `messages.create` parameters, usage fields, and
  error classes were checked against that release; `tests/test_claude.py`
  repeats the parameter check when the SDK is installed (CI installs it).
- The prompt lives at the repository root, `prompts/finmind-agent-prompt.v1.md`
  (found through `FINMIND_PROMPTS_DIR` or by walking up), so the API and the
  worker read the same file.
- Prices are a versioned C# table (`PricingCatalog`, version
  `anthropic-pricing/2026-11-03`) rather than a JSON file. Cache-write prices
  (1.25x input) and the Claude Haiku 5.5 cache-read price are estimates
  marked in the code; verify all prices on the live pricing page before
  paid runs and create a new version if they differ.
- The token budget counts new tokens only (input, output, cache writes);
  cache reads are excluded because a cached conversation re-reads its
  history every turn. Their cost is still metered against `maxCostMicroUsd`.
- `run-config/v2` fields are optional for mock runs, so earlier mock
  configurations stay valid; `agent.sampling` is accepted only as `{}`.
- Several tool calls in one response get IDs `turn-NNNN-K`; one call keeps
  `turn-NNNN`.
- Found during implementation: a turn with only tool calls has empty
  output text, which the invocation schema rejected, and the worker ignored
  the rejection. Empty text is now accepted, and the worker fails the run if
  an invocation record is ever rejected, so a missing record cannot pass
  silently.
- Test isolation: test classes that create queued runs now share one xUnit
  collection, so one test cannot claim another test's run.
- Manual smoke run: `services/agent-runtime/scripts/smoke_claude.py`
  (S01, 3 virtual days, US$0.50 budget as defined above). It runs only when started on purpose.
- Tests: `ProviderTests` (C#, both ledgers), `tests/test_claude.py`, worker
  tests for multi-call turns, idle turns, refusals, and rejected records,
  and an end-to-end test that drives the real API and worker with a
  scripted SDK client.

## Consequences

- FINMIND can run its first real experiment (specification section 20, H1:
  S01 versus S03) on Claude with every action traceable.
- Results are tied to an exact model, prompt hash, effort, and pricing
  version.
- Refused runs end instead of switching model; refusal rates become part of
  the findings rather than being hidden.
- Other providers (OpenAI, Google, local models) follow the same `Agent`
  interface in later ADRs.
