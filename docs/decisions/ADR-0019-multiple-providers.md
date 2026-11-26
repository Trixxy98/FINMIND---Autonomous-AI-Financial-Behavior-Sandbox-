# FINMIND ADR 0019: Multiple Model Providers

- Status: Accepted 25 November 2026 (steps 1 and 2 implemented; amended on 26 November 2026, see step 2); decisions 1 to 4 as proposed
- Date: 25 November 2026
- Scope: OpenAI (GPT), Groq, and Google (Gemini) as providers beside Anthropic
  (Claude), so users can compare them under the same scenario, prompt, tools,
  policy, and budgets

## Context

FINMIND runs locally: each user brings their own provider keys and pays for
their own runs (decided 25 November 2026). Its purpose is to compare how
different models handle the same ledger responsibilities. Only Anthropic is
supported (ADR-0012).

The developer cannot make paid calls. Each adapter is therefore built from
the provider's published API documentation and tested against fake
transports that reproduce the documented requests and responses. No adapter
is verified against a live API until a user with a key runs the smoke script.
The product must say so wherever a user picks a model.

## Decision

### One agent contract, one adapter per API family

The worker's provider-neutral contract is unchanged: an agent turns an
`Observation` into a `Decision` (tool calls, text, token counts, model,
stop reason, refusal, terminal condition). Two new adapters implement it:

| Adapter | Providers | Notes |
|---|---|---|
| OpenAI-compatible chat with tools | OpenAI, Groq | Groq documents an OpenAI-compatible endpoint; one adapter, a per-provider base URL, key, catalog, and prices |
| Gemini | Google | Its own request and response shapes |

The Claude adapter stays as it is.

### No new dependencies

The new adapters use Python's standard library for HTTP (as the worker's API
client already does), not provider SDKs. The dependency allowlist does not
grow. Timeouts, retries, and backoff follow the existing client: a retry never
sends a second request without its own cost reservation.

### Credentials and egress

- Keys are read only by the worker: `OPENAI_API_KEY`, `GROQ_API_KEY`, and
  `GEMINI_API_KEY`. The API refuses to start with any of them, as it does
  for Anthropic. Keys never appear in prompts, events, logs, exports, or
  error messages (errors keep the class name and HTTP status only).
- Each provider's base URL is fixed in code; it is not configurable, so a
  configuration change cannot send a key elsewhere. The host allowlist test
  gains `api.openai.com`, `api.groq.com`, and
  `generativelanguage.googleapis.com`.

### Same conditions for every model

- **Prompt.** The same `finmind-agent-prompt/v1` text for every provider.
- **Tools.** `finmind-tools/v1` JSON schemas, translated mechanically into
  each provider's tool format. A translation is versioned
  (`openai-tools/v1`, `gemini-tools/v1`) and recorded in the snapshot.
- **Sampling.** Each provider's defaults, sent with no sampling parameter.
  The snapshot records exactly what was sent, and that defaults differ
  between providers. (See decision 2 below.)
- **Turn handling.** One request per turn, all returned tool calls executed
  in order, results sent back paired with their call IDs, as with Claude.
  Features a provider lacks or handles differently (parallel calls, seeds,
  refusal signals) are recorded in the snapshot so a comparison can name
  them.
- The comparison already names `agent.provider` and `agent.modelId` as the
  factor when they differ between groups.

### Cost budget without a token-counting endpoint

The cost budget (ADR-0012, ADR-0013 A1) reserves each request's worst case
before it is sent. Anthropic counts tokens exactly; other providers may not
offer a counting endpoint. The worst case for them is the request's UTF-8
byte length plus a fixed margin: a text token covers at least one byte, and
the margin covers tokens the service adds itself (chat formatting, tool
definitions). The margin is checked against each provider's documented usage
reports in the fake-transport tests, and it is an assumption until a live run
confirms it. It reserves more than
the request costs, which can end a tight budget early; the reservation is
replaced by the reported usage after each call, so recorded cost stays
exact. The adapter version and the bound are recorded.

### Catalogs and prices

- A small list of models per provider, taken from the provider's
  documentation when its adapter is built. A model outside the catalog
  cannot be used, because the budget needs its prices.
- Prices are versioned per provider (`openai-pricing/<date>`, and so on),
  copied from the provider's public pricing page on the recorded date, and
  marked "verify before paid runs".
- Each provider has a `verifiedLive` flag, false until a smoke run against
  the live API is reported. The New run form shows it next to the model, and
  the models endpoint returns it.

### Errors

Each adapter maps its provider's responses to the existing outcomes: a
refusal or safety block ends the run with its category; an output-limit stop
is retried once with history unchanged (as ADR-0013 item 17); text that
cannot be represented ends the turn (item 19); rate limits are retried with
backoff inside the wall-time budget; a request with unknown billing keeps its
reservation.

## Delivery

Each step ends with tests, a report, and a stop for review:

1. Groundwork: configuration for the new providers, credential refusal in the
   API, per-provider pricing and catalogs, the byte-length bound, the
   `verifiedLive` flag, and a provider-neutral smoke script.
2. The OpenAI-compatible adapter, for OpenAI.
3. Groq on the same adapter.
4. The Gemini adapter.
5. The New run form, the documentation, and a browser check.

Every adapter step is tested with a fake transport: request shape (no key
in the body or URL, the tools translated, the prompt unchanged), each
documented response and error, cost reservation and recording, and an
end-to-end run through the real API with mock data. CI never calls a
provider.

#### Step 1 as implemented

- `ProviderCatalog` (`apps/api/Finance/ProviderCatalogs.cs`): each paid
  provider's label, price version, prices, and `VerifiedLive` (false for
  Anthropic too: it has not been run against its live API from this
  project). Run configuration, the prompt, the price version in snapshots,
  the cost reservation, and `GET /api/v1/models` read it, instead of
  checking for `anthropic` in five places. Only Anthropic is listed until
  its adapter step adds each new provider.
- The API refuses to start with `OPENAI_API_KEY`, `GROQ_API_KEY`,
  `GEMINI_API_KEY`, or `GOOGLE_API_KEY`, as with the Anthropic credentials,
  before those adapters exist.
- `GET /api/v1/models` gives each model its provider label, price version,
  and `verifiedLive`.
- `finmind_runtime/budget.py`: the input-token bound, the compact UTF-8 JSON
  byte length plus 1,024 (`utf8-bytes+1024/v1`).
- `scripts/smoke_provider.py --provider P [--model M]` replaces the Claude
  smoke script, which remains as a wrapper. It refuses a provider without an
  adapter before any request and prints the summary needed to mark a provider
  verified.
- No change to snapshots or the configuration version: adapter and bound
  versions are recorded when the first new adapter uses them (step 2).

#### Step 2 as implemented (and an amendment)

- **Amendment.** OpenAI's function-calling guide (read 26 November 2026)
  says GPT-6 Astra and GPT-6.1 Sol require the Responses API for tool
  calling, and the Chat Completions reference recommends Responses for new
  projects. A Chat Completions adapter would have left out OpenAI's current
  flagship models. With the researcher's agreement (option A), OpenAI uses
  the Responses API, and Groq gets its own OpenAI-compatible Chat Completions
  adapter in step 3. The order is unchanged.
- `finmind_runtime/openai.py` (`openai-responses/v1`): standard-library
  HTTPS to the fixed URL `https://api.openai.com/v1/responses`, the key only
  in the Authorization header, `store: false`, no `previous_response_id`,
  the whole history sent each turn, each response's output items (reasoning
  with its encrypted content, messages, function calls) appended unchanged,
  then one `function_call_output` per call. No temperature or top_p. Tools
  are not strict. `max_output_tokens` is the granted limit.
- Outcomes: `max_output_tokens` is retried once with history unchanged, then
  ends the run; `content_filter` or a refusal part ends it as a refusal; a
  429 is retried under the same reservation; other statuses fail the run as
  "HTTP n", marked as not run for 4xx and possibly billed otherwise; a
  timeout or dropped connection is possibly billed. Arguments that are not
  valid JSON go to the gateway as text and are recorded as malformed.
- Usage: OpenAI's `input_tokens` includes cached tokens, so FINMIND records
  `input_tokens - cached_tokens` as input and `cached_tokens` as cache reads;
  reasoning tokens are part of `output_tokens`.
- Catalog: `gpt-6.2-astra`, `gpt-6.2-sol`, `gpt-6-astra`, `gpt-6.1-sol`, with
  standard and long-context (over 272K input tokens) prices from OpenAI's
  pricing page as read on 26 November 2026 (`openai-pricing/2026-11-26`; the
  page gives no date). Efforts `low`, `medium`, `high` only: the reasoning
  guide says some of these models reject `none` or `minimal`. The Claude Haiku
  long-prompt price now uses the same tier mechanism.
- OpenAI snapshots record `adapter`, `tokenBound`, and
  `sampling: provider-defaults`; Claude snapshots are unchanged.
- Tests: `tests/test_openai.py`, fourteen unit tests against a fake
  transport and an end-to-end run through the real API and database
  (cost 3,430 micro-dollars for a scripted turn, checked by hand);
  `Finance.Tests/OpenAIProviderTests.cs` for configuration, snapshot, and
  prices. `api.openai.com` was added to the reviewed host list.
- Not verified against the live API.

## Out of scope

Live verification (users do it), providers beyond these four, local models,
fine-tuning, and provider-side features that change behavior (built-in
tools, web search, code execution).

## Decisions to confirm

1. Standard-library HTTP for the new adapters, no provider SDKs.
2. Provider default sampling, recorded, rather than forcing one temperature
   across providers (which not every model accepts).
3. A curated model list per provider, chosen from its documentation when
   the adapter is built.
4. The order: OpenAI, then Groq, then Gemini.
