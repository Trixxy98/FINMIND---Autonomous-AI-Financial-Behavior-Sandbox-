namespace Finmind.Api.Finance;

internal static class TextLimits
{
    // ADR-0013 A2: at most maxLength UTF-16 units, never half a surrogate pair (PostgreSQL JSONB rejects those).
    public static string Cut(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var splitsPair = maxLength > 0 && char.IsHighSurrogate(text[maxLength - 1]) && char.IsLowSurrogate(text[maxLength]);
        return text[..(splitsPair ? maxLength - 1 : maxLength)];
    }
}
