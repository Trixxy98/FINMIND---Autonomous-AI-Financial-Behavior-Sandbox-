# FINMIND ADR 0019: Multiple Model Providers

- Status: Accepted 25 November 2026; implemented (steps 1 to 5), amended 26 November 2026 (steps 2 and 4)
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

#### Step 3 as implemented

- `finmind_runtime/groq.py` (`groq-chat/v1`): Groq's OpenAI-compatible Chat
  Completions at the fixed URL `https://api.groq.com/openai/v1/chat/completions`,
  standard-library HTTPS, the key only in the Authorization header.
- Only fields Groq documents are sent: model, messages (the same prompt as a
  system message), function tools, `tool_choice: auto`, `reasoning_effort`,
  `max_completion_tokens`, and `parallel_tool_calls: false` (Groq's tool-use
  table marks parallel calls as unsupported for gpt-oss). No `store`
  (unsupported), no sampling, no `name` on tool messages (a 400 per Groq).
- History: each assistant turn is replayed with role, content, and
  tool_calls, then one role "tool" message per call. The model's `reasoning`
  text is not sent back, because Groq does not document it as an input
  field. This differs from the OpenAI and Claude adapters, which replay
  reasoning; it is recorded here as a known difference between providers.
  Groq's reasoning guide advises putting instructions in the user message for
  reasoning models; FINMIND keeps the prompt in the system message so every
  provider receives it the same way.
- Outcomes: `length` is retried once, then ends the run; `content_filter`
  ends it as a refusal; 429 and 498 (flex capacity) are retried under the
  same reservation; 500, 502, and 503 are marked as not run (Groq: "You will
  not be charged for requests that return server error codes"); a 422 keeps
  its reservation, since its billing is not stated.
- Catalog: `openai/gpt-oss-120b` ($0.15 input, $0.60 output per million) and
  `openai/gpt-oss-20b` ($0.075, $0.30), from the production table on Groq's
  models page as read on 26 November 2026 (`groq-pricing/2026-11-26`). Llama
  models priced "Contact Sales" and preview models are left out. No cached
  price is listed, so cached tokens (none are reported) would be priced as
  input. Efforts `low`, `medium`, `high`.
- Tests: `tests/test_groq.py` (ten unit tests on a fake transport and an
  end-to-end run through the real API, cost 270 micro-dollars, checked by
  hand) and `Finance.Tests/GroqProviderTests.cs`. `api.groq.com` and
  `console.groq.com` (a pricing citation) were added to the reviewed hosts.
- Not verified against the live API. Groq's data-retention terms were not
  stated on the pages read.

#### Step 4 as implemented (and two decisions)

- **API.** Google's documentation (read 26 November 2026 at
  aistudio.google.com, where ai.google.dev now redirects) calls
  `generateContent` legacy and the Interactions API "the recommended
  interface for all new projects". The researcher chose Interactions.
- `finmind_runtime/gemini.py` (`gemini-interactions/v1`):
  `POST https://generativelanguage.googleapis.com/v1beta/interactions`, the
  key only in the `x-goog-api-key` header (never in the URL, unlike the
  `?key=` examples), `store: false`, no `previous_interaction_id`, no
  background. Each returned step is appended unchanged (Google: "You MUST
  always resend all thought blocks exactly as they were received"), then one
  `function_result` per call in the documented shape
  (`{"type": "function_result", "name", "call_id", "result": [{"type": "text",
  "text"}]}`). `generation_config` carries `max_output_tokens`,
  `thinking_level`, and `tool_choice: auto`; no temperature (ignored or to be
  rejected by newer Gemini models).
- The full JSON of a `function_call` step is not shown verbatim in the docs;
  the adapter uses the documented field names (`id`, `name`, `arguments`,
  an object in every example) and parses `arguments` if it arrives as text.
- Outcomes: `incomplete` is retried once with history unchanged, then ends
  the run; `failed` or another unexpected status fails it. Blocked or
  malformed generations (Google's error codes such as `safety`,
  `prohibited_content`, `malformed_function_call`,
  `missing_thought_signature`) fail the run as "HTTP n code" and keep their
  reservation; other 4xx are marked as not run; 429 is retried under the same
  reservation; 5xx keeps its reservation. Only Google's snake_case error code
  is kept, never the message.
- Usage: output is `total_output_tokens + total_thought_tokens` (Google:
  "response pricing is the sum of output tokens and thinking tokens"). Whether
  cached tokens are inside `total_input_tokens` is not stated, so they are not
  subtracted, and tool-use prompt tokens count as input: the recorded cost can
  be too high, never too low.
- **Dated prices.** Gemini 4 Carbon and Argon double on 1 December 2026, and
  3.8 Flash and 3.6 Flash on 1 January 2027. The researcher chose dated
  prices: `ProviderInfo.PriceChanges` lists changes per model, and each turn
  is priced at the rate in effect when the server records it (the database
  clock for PostgreSQL, the ledger's clock in memory); reservations use the
  same time. The time of day is not stated, so 00:00 UTC is used, the
  earliest possible start. Any provider can now carry dated prices.
- Catalog (`google-pricing/2026-11-26`, paid standard tier):
  `gemini-4-hydrogen` ($1.50 / $7.50 / cached $0.075), `gemini-4-neon` ($3 /
  $15 / $0.15), `gemini-4-carbon` and `gemini-4-argon` ($2 / $10 / $0.10,
  then $4 / $20 / $0.20), `gemini-3.8-flash` and `gemini-3.6-flash` ($0.75 /
  $3.75 / $0.075, then $1.50 / $7.50 / $0.15). Gemini 4 models carry no
  "Stable" badge on the models page; the Flash models do. Flash-Lite models are
  left out because their thinking levels are not listed. Levels `low`,
  `medium`, `high`, which every listed model accepts.
- Data use: on the free tier, Google's terms say prompts and responses are
  used to improve its products and may be read by human reviewers; on the
  paid tier they are not. The README says so.
- Tests: `tests/test_gemini.py` (twelve unit tests on a fake transport and
  an end-to-end run through the real API, cost 3,023 micro-dollars, checked by
  hand) and `Finance.Tests/GeminiProviderTests.cs`, including both sides of
  each price date. `generativelanguage.googleapis.com` and
  `aistudio.google.com` (a pricing citation) were added to the reviewed hosts.
- Not verified against the live API.

#### Step 5 as implemented

- `GET /api/v1/models` gives each model its provider label, price version,
  `verifiedLive`, the efforts its provider accepts, and the provider's notice
  (`ProviderInfo.Notice`): Groq's lack of `store` and unstated retention, and
  Gemini's free-tier data use and dated prices.
- The New run form labels each model "Provider / model (paid, not verified
  live)", offers only that model's efforts (an effort the new model does not
  accept falls back to `medium`), validates against them, and shows a notice
  under the model and on the review screen: the adapter is not verified
  against the live API, with the smoke command to run, plus the provider's
  notice. Mock agents show none.
- Checked in Chrome (test database, no paid run created): every provider's
  models listed; Claude's five efforts, three for the others; switching from
  Claude at `max` to OpenAI gives `medium`; Gemini shows both notices, Groq its
  `store` notice, OpenAI and Claude the live-verification notice only.
- Tests: four form tests (`tests/run-form.test.js`) and the models endpoint
  test (`tests/test_read_api.py`).

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
