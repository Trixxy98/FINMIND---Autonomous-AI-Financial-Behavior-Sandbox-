using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Finmind.Api.Finance;

// ADR-0013 item 17: agent text can hold invalid Unicode (a lone surrogate escape, or bad UTF-8 bytes).
// Neither .NET JSON nor PostgreSQL JSONB can read it, so it is replaced with U+FFFD and reported to the caller.
public static class AgentText
{
    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    // Throws JsonException when the body is not JSON.
    public static (JsonNode? Body, bool HadInvalidUnicode) Parse(ReadOnlySpan<byte> utf8)
    {
        string text;
        var invalid = false;
        try
        {
            text = Strict.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.UTF8.GetString(utf8);
            invalid = true;
        }

        var repaired = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length;)
        {
            // A backslash only occurs inside a JSON string, where it always starts an escape.
            if (text[index] != '\\' || index + 1 == text.Length)
            {
                repaired.Append(text[index++]);
                continue;
            }
            var length = 2;
            if (EscapedUnit(text, index) is { } unit)
            {
                length = 6;
                if (char.IsHighSurrogate(unit) && EscapedUnit(text, index + 6) is { } next && char.IsLowSurrogate(next))
                {
                    length = 12;
                }
                else if (char.IsSurrogate(unit))
                {
                    repaired.Append(@"�");
                    invalid = true;
                    index += 6;
                    continue;
                }
            }
            repaired.Append(text, index, length);
            index += length;
        }

        if (!invalid) return (JsonNode.Parse(utf8), false);
        using var document = JsonDocument.Parse(repaired.ToString());
        return (Rebuild(document.RootElement), true);
    }

    private static char? EscapedUnit(string text, int index) =>
        index + 6 <= text.Length && text[index] == '\\' && text[index + 1] == 'u'
        && ushort.TryParse(text.AsSpan(index + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit)
            ? (char)unit : null;

    // Replacement can make two property names equal, which JsonObject does not allow: the last one wins.
    private static JsonNode? Rebuild(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var rebuilt = new JsonObject();
                foreach (var property in element.EnumerateObject()) rebuilt[property.Name] = Rebuild(property.Value);
                return rebuilt;
            case JsonValueKind.Array:
                return new JsonArray(element.EnumerateArray().Select(Rebuild).ToArray());
            case JsonValueKind.Null:
                return null;
            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }
}
