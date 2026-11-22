using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0013 item 18: what the gateway needs to record an attempt it cannot read. The body itself is not kept.
public sealed record UnreadableToolCall(string ToolCallId, string Tool, long Bytes, string Sha256, string Problem);

public sealed record ReadableToolCall(string ToolCallId, string Tool, JsonNode? Arguments, bool HadInvalidUnicode);

// Every body becomes either a call that can be hashed, stored, and read back, or an unreadable attempt.
public static class ToolCallBody
{
    public const int MaxBytes = 16_384;
    // Well inside the 64-level JSON limit, which also applies when the stored call is read back inside an event.
    private const int MaxDepth = 32;
    private const int MaxNumberLength = 64;
    private const int MaxExponent = 308;
    private const int MaxToolNameLength = 100;

    public static async Task<(ReadableToolCall? Call, UnreadableToolCall? Unreadable)> Read(Stream body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var kept = new MemoryStream();
        var chunk = new byte[MaxBytes];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(chunk)) > 0)
        {
            hash.AppendData(chunk, 0, read);
            kept.Write(chunk, 0, (int)Math.Min(read, MaxBytes - kept.Length));
            total += read;
        }
        return Interpret(kept.ToArray(), total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static (ReadableToolCall?, UnreadableToolCall?) Interpret(byte[] bytes, long total, string sha256)
    {
        string problem;
        if (total > MaxBytes) problem = "too_large";
        else
        {
            try
            {
                var (node, hadInvalidUnicode) = AgentText.Parse(bytes);
                // Walk first: it is what finds duplicate names, which JsonObject reports only when first read.
                if (ProblemIn(node, 0) is { } found) problem = found;
                else if (node is JsonObject call && call.All(p => p.Key is "toolCallId" or "tool" or "arguments")
                    && Text(call["toolCallId"]) is { } id && ToolGateway.IsValidToolCallId(id)
                    && Text(call["tool"]) is { } tool)
                {
                    if (tool.Length <= MaxToolNameLength && ToolArgumentRedaction.RedactName(tool).Length > 0)
                        return (new ReadableToolCall(id, tool, call["arguments"]?.DeepClone(), hadInvalidUnicode), null);
                    problem = "invalid_tool_name";
                }
                else problem = "invalid_shape";
            }
            catch (ArgumentException) { problem = "duplicate_key"; }
            catch (Exception) { problem = "unparsable"; }
        }

        var (givenId, givenTool) = Envelope(bytes);
        var recordedId = givenId is not null && ToolGateway.IsValidToolCallId(givenId) ? givenId : "unreadable-" + sha256[..32];
        // Redaction can add a truncation marker, and the stored name may not exceed 100 units or be empty.
        var recordedTool = TextLimits.Cut(ToolArgumentRedaction.RedactName(givenTool ?? ""), MaxToolNameLength);
        return (null, new UnreadableToolCall(recordedId, recordedTool.Length > 0 ? recordedTool : "(unreadable)", total, sha256, problem));
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string? ProblemIn(JsonNode? node, int depth)
    {
        if (depth > MaxDepth) return "too_deep";
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                    if (ProblemIn(child, depth + 1) is { } inObject) return inObject;
                return null;
            case JsonArray array:
                foreach (var child in array)
                    if (ProblemIn(child, depth + 1) is { } inArray) return inArray;
                return null;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                return IsStorable(value.ToJsonString()) ? null : "number_out_of_range";
            default:
                return null;
        }
    }

    // PostgreSQL numeric expands exponents, so 1e200000 cannot be stored.
    private static bool IsStorable(string number)
    {
        if (number.Length > MaxNumberLength) return false;
        var exponentAt = number.IndexOfAny(['e', 'E']);
        return exponentAt < 0
            || (int.TryParse(number.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent)
                && exponent is >= -MaxExponent and <= MaxExponent);
    }

    // Best effort: the top-level ID and name, as far as the body can be read.
    private static (string? Id, string? Tool) Envelope(byte[] bytes)
    {
        string? id = null, tool = null;
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions { MaxDepth = MaxBytes }));
            while ((id is null || tool is null) && reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var isId = reader.ValueTextEquals("toolCallId");
                var isTool = reader.ValueTextEquals("tool");
                if (!isId && !isTool) continue;
                if (!reader.Read()) break;
                if (reader.TokenType != JsonTokenType.String) continue;
                if (isId) id ??= reader.GetString();
                else tool ??= reader.GetString();
            }
        }
        catch (Exception)
        {
            // Nothing more can be read; what was found so far still identifies the attempt.
        }
        return (id, tool);
    }
}
