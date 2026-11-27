using System.Security.Cryptography;

namespace Finmind.Api.Finance;

// ADR-0012: versioned agent prompts. The API records the hash in each snapshot; the worker refuses a mismatch.
public sealed record PromptFile(string Version, string Hash, string FileName);

public static class PromptCatalog
{
    public const string CurrentFileName = "finmind-agent-prompt.v1.md";
    public const string CurrentVersion = "finmind-agent-prompt/v1";

    private static readonly Lazy<PromptFile> CurrentPrompt = new(() =>
    {
        var path = Path.Combine(FindDirectory(), CurrentFileName);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is 0 or > 64_000) throw new InvalidOperationException($"Prompt file {CurrentFileName} is empty or too large.");
        return new PromptFile(CurrentVersion, Convert.ToHexStringLower(SHA256.HashData(bytes)), CurrentFileName);
    });

    public static PromptFile Current => CurrentPrompt.Value;

    // FINMIND_PROMPTS_DIR, else the nearest "prompts" directory above the working or base directory.
    public static string FindDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("FINMIND_PROMPTS_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "prompts");
                if (File.Exists(Path.Combine(candidate, CurrentFileName))) return candidate;
            }
        }
        throw new DirectoryNotFoundException("No prompts directory found. Set FINMIND_PROMPTS_DIR.");
    }
}

// Prices in micro-USD per million tokens. Integer arithmetic only.
public sealed record ModelPrice(long Input, long Output, long CacheRead, long CacheWrite)
{
    public long Cost(long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens)
    {
        var total = checked(inputTokens * Input + outputTokens * Output + cacheReadTokens * CacheRead + cacheWriteTokens * CacheWrite);
        return (total + 999_999) / 1_000_000; // round up to the next micro-dollar
    }
}

// ADR-0019: one catalog of paid providers. Configuration, prices, credentials, and the app all read it.
// VerifiedLive is true only after a smoke run against the provider's live API has been reported.
// LongPrompts: per model, the prompt size above which a request is priced at the long-context rate.
// Adapter: recorded in each snapshot with the token bound and sampling (null keeps earlier snapshots unchanged).
public sealed record ProviderInfo(string Id, string Label, string PricingVersion, IReadOnlyDictionary<string, ModelPrice> Prices,
    bool VerifiedLive, IReadOnlySet<string> Efforts,
    IReadOnlyDictionary<string, (long Threshold, ModelPrice Price)> LongPrompts, string? Adapter = null, string? TokenBound = null,
    IReadOnlyDictionary<string, IReadOnlyList<(DateTimeOffset From, ModelPrice Price)>>? PriceChanges = null,
    string? Notice = null); // shown to the researcher beside the model (ADR-0019 step 5)

public static class ProviderCatalog
{
    public static readonly IReadOnlyDictionary<string, ProviderInfo> Paid = new Dictionary<string, ProviderInfo>
    {
        ["anthropic"] = new("anthropic", "Anthropic (Claude)", PricingCatalog.Version, PricingCatalog.AnthropicPrices,
            VerifiedLive: false, new HashSet<string> { "low", "medium", "high", "xhigh", "max" }, PricingCatalog.AnthropicLongPrompts),
        // ADR-0019 step 2. Responses API; low, medium, and high are accepted by every listed model (some reject none
        // or minimal). Not verified against the live API.
        ["openai"] = new("openai", "OpenAI (GPT)", OpenAIPricing.Version, OpenAIPricing.Prices,
            VerifiedLive: false, new HashSet<string> { "low", "medium", "high" }, OpenAIPricing.LongPrompts,
            Adapter: "openai-responses/v1", TokenBound: "utf8-bytes+1024/v1"),
        // ADR-0019 step 3. OpenAI-compatible Chat Completions; production models with published prices only.
        // gpt-oss accepts low, medium, and high. Not verified against the live API.
        ["groq"] = new("groq", "Groq", GroqPricing.Version, GroqPricing.Prices,
            VerifiedLive: false, new HashSet<string> { "low", "medium", "high" }, new Dictionary<string, (long, ModelPrice)>(),
            Adapter: "groq-chat/v1", TokenBound: "utf8-bytes+1024/v1",
            Notice: "Groq's Chat Completions does not support store, and the Groq pages read for FINMIND did not state how long " +
                    "requests are kept. Check Groq's data policy."),
        // ADR-0019 step 4. Interactions API with store=false; low, medium, and high are accepted by every listed model.
        // Prices change on announced dates (PriceChanges). Not verified against the live API.
        ["google"] = new("google", "Google (Gemini)", GeminiPricing.Version, GeminiPricing.Prices,
            VerifiedLive: false, new HashSet<string> { "low", "medium", "high" }, new Dictionary<string, (long, ModelPrice)>(),
            Adapter: "gemini-interactions/v1", TokenBound: "utf8-bytes+1024/v1", PriceChanges: GeminiPricing.Changes,
            Notice: "On Google's free tier, Google's terms say prompts and responses are used to improve its products and " +
                    "may be read by human reviewers. Use a paid tier. Some prices change on announced dates."),
    };

    // Refused by the API at startup whether or not the provider's adapter exists yet: credentials belong to the worker.
    public static readonly IReadOnlyList<string> CredentialNames =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "GROQ_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY"];

    public static bool IsPaid(string? provider) => provider is not null && Paid.ContainsKey(provider);
}

public static class PricingCatalog
{
    // Source: Anthropic model table (cached 2026-11-03) as provided with the Claude API documentation.
    // Verify against https://platform.claude.com/docs/en/about-claude/pricing before paid runs; a price change
    // creates a new version so existing runs keep the version they recorded.
    public const string Version = "anthropic-pricing/2026-11-03";

    // Cache writes use the documented ~1.25x input rate for 5-minute caching (an estimate, marked here).
    // Haiku 5.5 cache-read price was not listed; 0.1x input is used as a conservative estimate.
    public static IReadOnlyDictionary<string, ModelPrice> AnthropicPrices => Standard;

    public static IReadOnlyDictionary<string, (long Threshold, ModelPrice Price)> AnthropicLongPrompts =>
        new Dictionary<string, (long, ModelPrice)> { ["claude-haiku-5-5"] = (HaikuLongPromptThreshold, HaikuLongPrompt) };

    private static readonly IReadOnlyDictionary<string, ModelPrice> Standard = new Dictionary<string, ModelPrice>
    {
        ["claude-opus-5-5"] = new(4_000_000, 20_000_000, 200_000, 5_000_000),
        ["claude-sonnet-5-5"] = new(2_000_000, 10_000_000, 100_000, 2_500_000),
        ["claude-haiku-5-5"] = new(100_000, 500_000, 10_000, 125_000),
    };

    // Claude Haiku 5.5 prompts above 100K tokens use a higher rate ($0.50 / $2.50 per million).
    private const long HaikuLongPromptThreshold = 100_000;
    private static readonly ModelPrice HaikuLongPrompt = new(500_000, 2_500_000, 50_000, 625_000);

    // requests: how many requests the totals cover. The long-prompt tier is per request, not per turn.
    // at: when the cost is incurred; a dated price change applies from its date (ADR-0019 step 4). Defaults to now.
    public static long CostMicroUsd(string provider, string modelId, long inputTokens, long outputTokens, long cacheReadTokens,
        long cacheWriteTokens, int requests = 1, DateTimeOffset? at = null)
    {
        if (!ProviderCatalog.IsPaid(provider)) return 0;
        var price = PriceFor(provider, modelId, (inputTokens + cacheReadTokens + cacheWriteTokens) / Math.Max(requests, 1), at);
        return price.Cost(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);
    }

    // ADR-0013 A1: every input token at the dearest input rate, and every allowed output token used.
    public static long WorstCaseMicroUsd(string provider, string modelId, long inputTokens, long maxOutputTokens, DateTimeOffset? at = null)
    {
        var price = PriceFor(provider, modelId, inputTokens, at);
        var dearest = price with { Input = Math.Max(price.Input, price.CacheWrite) };
        return dearest.Cost(inputTokens, maxOutputTokens, 0, 0);
    }

    // The most output tokens whose worst case still fits in the remaining budget. May be zero or negative.
    public static long AffordableOutputTokens(string provider, string modelId, long inputTokens, long remainingMicroUsd,
        DateTimeOffset? at = null)
    {
        var price = PriceFor(provider, modelId, inputTokens, at);
        return (checked(remainingMicroUsd * 1_000_000) - checked(inputTokens * Math.Max(price.Input, price.CacheWrite))) / price.Output;
    }

    public static string VersionFor(string? provider) =>
        provider is not null && ProviderCatalog.Paid.TryGetValue(provider, out var p) ? p.PricingVersion : "none";

    private static ModelPrice PriceFor(string provider, string modelId, long promptTokens, DateTimeOffset? at)
    {
        var price = ProviderCatalog.Paid.TryGetValue(provider, out var info) && info.Prices.TryGetValue(modelId, out var known)
            ? known
            : throw new InvalidOperationException($"No price for {provider} model {modelId}.");
        var when = at ?? DateTimeOffset.UtcNow;
        if (info!.PriceChanges?.TryGetValue(modelId, out var changes) == true)
            foreach (var change in changes.Where(c => c.From <= when).OrderBy(c => c.From))
                price = change.Price;
        if (info!.LongPrompts.TryGetValue(modelId, out var tier) && promptTokens > tier.Threshold)
            price = tier.Price;
        return price;
    }
}

// ADR-0019 step 2. Copied from https://developers.openai.com/api/docs/pricing on 26 November 2026; the page shows no
// update date. Standard tier, per million tokens. OpenAI charges no separate cache-write price, so CacheWrite is the
// input price (the worst case already uses the dearer of the two). Verify before paid runs; a change is a new version.
public static class OpenAIPricing
{
    public const string Version = "openai-pricing/2026-11-26";
    private const long LongContextThreshold = 272_000; // "Long context: >272K input tokens"

    private static ModelPrice P(decimal input, decimal cached, decimal output) =>
        new((long)(input * 1_000_000), (long)(output * 1_000_000), (long)(cached * 1_000_000), (long)(input * 1_000_000));

    public static readonly IReadOnlyDictionary<string, ModelPrice> Prices = new Dictionary<string, ModelPrice>
    {
        ["gpt-6.2-astra"] = P(8.00m, 0.40m, 40.00m),
        ["gpt-6.2-sol"] = P(2.00m, 0.10m, 10.00m),
        ["gpt-6-astra"] = P(10.00m, 1.00m, 50.00m),
        ["gpt-6.1-sol"] = P(2.00m, 0.10m, 10.00m),
    };

    public static readonly IReadOnlyDictionary<string, (long Threshold, ModelPrice Price)> LongPrompts =
        new Dictionary<string, (long, ModelPrice)>
        {
            ["gpt-6.2-astra"] = (LongContextThreshold, P(16.00m, 0.80m, 60.00m)),
            ["gpt-6.2-sol"] = (LongContextThreshold, P(4.00m, 0.20m, 15.00m)),
            ["gpt-6-astra"] = (LongContextThreshold, P(20.00m, 2.00m, 75.00m)),
            ["gpt-6.1-sol"] = (LongContextThreshold, P(4.00m, 0.20m, 15.00m)),
        };
}

// ADR-0019 step 3. From the production models table at https://console.groq.com/docs/models, read 26 November 2026
// ("PRICE PER 1M TOKENS"; no date shown). No cached-input price is listed, so cached tokens are priced as input and
// there is no cache-write price. Models priced "Contact Sales" and preview models are left out: the budget needs a
// published price, and Groq says preview models should not be used in production. Verify before paid runs.
public static class GroqPricing
{
    public const string Version = "groq-pricing/2026-11-26";

    private static ModelPrice P(decimal input, decimal output) =>
        new((long)(input * 1_000_000), (long)(output * 1_000_000), (long)(input * 1_000_000), (long)(input * 1_000_000));

    public static readonly IReadOnlyDictionary<string, ModelPrice> Prices = new Dictionary<string, ModelPrice>
    {
        ["openai/gpt-oss-120b"] = P(0.15m, 0.60m),
        ["openai/gpt-oss-20b"] = P(0.075m, 0.30m),
    };
}

// ADR-0019 step 4. From https://aistudio.google.com/docs/pricing (paid tier, standard, per 1M tokens), read 26 November
// 2026; no publication date shown. Output includes thinking tokens. Cached input is the implicit-caching price; FINMIND
// does not use explicit caching, so storage prices do not apply. No cache-write price, so CacheWrite is the input price.
// Announced changes are dated; their time of day is not stated, so 00:00 UTC is used (the earliest possible start, so a
// budget never underestimates). Verify before paid runs.
public static class GeminiPricing
{
    public const string Version = "google-pricing/2026-11-26";

    private static ModelPrice P(decimal input, decimal cached, decimal output) =>
        new((long)(input * 1_000_000), (long)(output * 1_000_000), (long)(cached * 1_000_000), (long)(input * 1_000_000));

    private static readonly DateTimeOffset December2026 = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset January2027 = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly IReadOnlyDictionary<string, ModelPrice> Prices = new Dictionary<string, ModelPrice>
    {
        ["gemini-4-hydrogen"] = P(1.50m, 0.075m, 7.50m),
        ["gemini-4-neon"] = P(3.00m, 0.15m, 15.00m),
        ["gemini-4-carbon"] = P(2.00m, 0.10m, 10.00m),
        ["gemini-4-argon"] = P(2.00m, 0.10m, 10.00m),
        ["gemini-3.8-flash"] = P(0.75m, 0.075m, 3.75m),
        ["gemini-3.6-flash"] = P(0.75m, 0.075m, 3.75m),
    };

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<(DateTimeOffset From, ModelPrice Price)>> Changes =
        new Dictionary<string, IReadOnlyList<(DateTimeOffset, ModelPrice)>>
        {
            ["gemini-4-carbon"] = [(December2026, P(4.00m, 0.20m, 20.00m))],
            ["gemini-4-argon"] = [(December2026, P(4.00m, 0.20m, 20.00m))],
            ["gemini-3.8-flash"] = [(January2027, P(1.50m, 0.15m, 7.50m))],
            ["gemini-3.6-flash"] = [(January2027, P(1.50m, 0.15m, 7.50m))],
        };
}
