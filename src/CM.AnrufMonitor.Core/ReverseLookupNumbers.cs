namespace CM.AnrufMonitor.Core;

/// <summary>Conservative external-number eligibility; this never guesses from a suffix.</summary>
public static class ReverseLookupNumbers
{
    public static string Normalize(string? number, string countryCode = "+49", string areaCode = "")
    {
        if (string.IsNullOrWhiteSpace(number) || number.Length > 128 || number.Any(char.IsControl)) return "";
        string raw = number.Trim();
        if (raw.Any(c => c is '*' or '#') || raw.Any(c => !(char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '(' or ')' or '.' or '/')))
            return "";
        string digits = new(raw.Where(char.IsAsciiDigit).ToArray());
        // Short extensions and service codes must not become external numbers when an area is set.
        if (digits.Length < 5) return "";
        bool international = raw.StartsWith('+') || raw.StartsWith("00", StringComparison.Ordinal);
        string country = international ? "+49" : CleanCountry(countryCode);
        if (country.Length == 0) return "";
        bool local = !international && !raw.StartsWith('0');
        string area = local ? CleanArea(areaCode, country) : "";
        if (local && area.Length == 0) return "";
        // German harmonised services and directory enquiries are not subscriber numbers.
        if (local && country == "+49" && digits.Length <= 6 && digits.StartsWith("11", StringComparison.Ordinal)) return "";
        string normalized = ContactBook.NormalizeNumber(raw, country, area);
        return IsFullNumber(normalized) ? normalized : "";
    }

    public static bool IsFullNumber(string? number) => number is { Length: >= 8 and <= 16 }
        && number[0] == '+' && number[1] is >= '1' and <= '9' && number.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    private static string CleanCountry(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith('+')) text = text[1..];
        else if (text.StartsWith("00", StringComparison.Ordinal)) text = text[2..];
        return text is { Length: >= 1 and <= 3 } && text[0] is >= '1' and <= '9' && text.All(char.IsAsciiDigit)
            ? "+" + text : "";
    }

    private static string CleanArea(string? value, string country)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '(' or ')'))) return "";
        if (text.StartsWith('+') || text.StartsWith("00", StringComparison.Ordinal)) text = text.Replace("(0)", "", StringComparison.Ordinal);
        string compact = new(text.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        if (compact.StartsWith("00", StringComparison.Ordinal)) compact = "+" + compact[2..];
        if (compact.StartsWith('+'))
        {
            if (!compact.StartsWith(country, StringComparison.Ordinal)) return "";
            compact = compact[country.Length..];
        }
        compact = compact.TrimStart('0');
        return compact is { Length: >= 1 and <= 8 } && compact.All(char.IsAsciiDigit) ? "0" + compact : "";
    }
}
