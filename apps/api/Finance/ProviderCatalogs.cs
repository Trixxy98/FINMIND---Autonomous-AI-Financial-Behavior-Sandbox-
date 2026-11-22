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

public static class PricingCatalog
{
    // Source: Anthropic model table (cached 2026-11-03) as provided with the Claude API documentation.
    // Verify against https://platform.claude.com/docs/en/about-claude/pricing before paid runs; a price change
    // creates a new version so existing runs keep the version they recorded.
    public const string Version = "anthropic-pricing/2026-11-03";

    // Cache writes use the documented ~1.25x input rate for 5-minute caching (an estimate, marked here).
    // Haiku 5.5 cache-read price was not listed; 0.1x input is used as a conservative estimate.
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
    public static long CostMicroUsd(string provider, string modelId, long inputTokens, long outputTokens, long cacheReadTokens,
        long cacheWriteTokens, int requests = 1)
    {
        if (provider != "anthropic") return 0;
        var price = PriceFor(modelId, (inputTokens + cacheReadTokens + cacheWriteTokens) / Math.Max(requests, 1));
        return price.Cost(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);
    }

    // ADR-0013 A1: every input token at the dearest input rate, and every allowed output token used.
    public static long WorstCaseMicroUsd(string modelId, long inputTokens, long maxOutputTokens)
    {
        var price = PriceFor(modelId, inputTokens);
        var dearest = price with { Input = Math.Max(price.Input, price.CacheWrite) };
        return dearest.Cost(inputTokens, maxOutputTokens, 0, 0);
    }

    // The most output tokens whose worst case still fits in the remaining budget. May be zero or negative.
    public static long AffordableOutputTokens(string modelId, long inputTokens, long remainingMicroUsd)
    {
        var price = PriceFor(modelId, inputTokens);
        return (checked(remainingMicroUsd * 1_000_000) - checked(inputTokens * Math.Max(price.Input, price.CacheWrite))) / price.Output;
    }

    private static ModelPrice PriceFor(string modelId, long promptTokens)
    {
        var price = Standard.TryGetValue(modelId, out var known)
            ? known
            : throw new InvalidOperationException($"No price for model {modelId}.");
        if (modelId == "claude-haiku-5-5" && promptTokens > HaikuLongPromptThreshold)
            price = HaikuLongPrompt;
        return price;
    }
}
