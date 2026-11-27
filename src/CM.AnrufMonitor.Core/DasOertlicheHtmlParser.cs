using System.Net;
using System.Text;
using System.Text.Json;

namespace CM.AnrufMonitor.Core;

internal sealed record DasOertlicheParsedResult(ReverseLookupStatus Status, string Name, string Message);

/// <summary>
/// A deliberately small, bounded HTML reader. It never executes scripts or loads resources.
/// Names and complete phone numbers must belong to the same structured entity or result card.
/// It is an independent implementation; no jFritz source or regular expressions are embedded.
/// </summary>
internal static class DasOertlicheHtmlParser
{
    private static readonly HashSet<string> ResultClasses = new(StringComparer.OrdinalIgnoreCase)
        { "hit", "hitentry", "hit-item", "hit-list-item", "result-item", "result-card", "treffer", "treffer-item", "vcard" };
    private static readonly HashSet<string> NameClasses = new(StringComparer.OrdinalIgnoreCase)
        { "name", "iname", "hitlnk", "hitlnk_name", "hit-name", "hit-name-link", "entry-name", "result-name" };
    // Deliberately explicit. Unknown schema types must not turn a page title, FAQ, app name,
    // or unrelated content object into a caller identity merely because it repeats a phone.
    private static readonly HashSet<string> IdentityTypes = new(StringComparer.Ordinal)
    {
        "Person", "Organization", "Corporation", "LocalBusiness", "GovernmentOrganization", "EducationalOrganization",
        "MedicalOrganization", "NGO", "Hotel", "LodgingBusiness", "Restaurant", "FoodEstablishment", "CafeOrCoffeeShop",
        "Bakery", "BankOrCreditUnion", "FinancialService", "InsuranceAgency", "AccountingService", "LegalService",
        "Attorney", "Notary", "Store", "JewelryStore", "GroceryStore", "Supermarket", "ElectronicsStore", "ComputerStore",
        "FurnitureStore", "HardwareStore", "ClothingStore", "BookStore", "Florist", "HomeAndConstructionBusiness",
        "Electrician", "Plumber", "Locksmith", "AutomotiveBusiness", "AutoDealer", "AutoRepair", "GasStation",
        "HealthAndBeautyBusiness", "HairSalon", "BeautySalon", "MedicalBusiness", "Dentist", "Physician", "Pharmacy",
        "VeterinaryCare", "Hospital", "School", "CollegeOrUniversity", "Library", "GovernmentOffice", "PostOffice",
        "PoliceStation", "FireStation", "ProfessionalService", "TravelAgency", "RealEstateAgent", "EmploymentAgency"
    };
    private static readonly HashSet<string> EmptyClasses = new(StringComparer.OrdinalIgnoreCase)
        { "nonumber01" };
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    public static DasOertlicheParsedResult Parse(string html, string number, CancellationToken cancellationToken)
    {
        HtmlNode root = ReadHtml(html, cancellationToken);
        HtmlNode[] nodes = Descendants(root, cancellationToken).ToArray();
        if (IsChallenge(nodes, cancellationToken))
            return new(ReverseLookupStatus.Blocked, "", "Das Örtliche verlangt eine Bestätigung im Browser. Die automatische Suche wurde angehalten.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HtmlNode script in nodes.Where(x => x.Tag == "script" && !IsHidden(x)
            && x.Attribute("type").Equals("application/ld+json", StringComparison.OrdinalIgnoreCase)))
            ReadJsonEntities(script.RawText.ToString(), number, names, cancellationToken);

        foreach (HtmlNode scope in nodes.Where(x => x.HasAttribute("itemscope") && !IsInert(x)
            && Tokens(x.Attribute("itemtype")).Any(IsIdentityTypeName)))
            ReadMicrodata(scope, number, names, cancellationToken);

        foreach (HtmlNode card in nodes.Where(IsResultCard))
            ReadResultCard(card, number, names, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (names.Count > 1)
            return new(ReverseLookupStatus.Ambiguous, "", "Das Örtliche nennt mehrere unterschiedliche Namen zu dieser Rufnummer. Es wurde kein Name automatisch übernommen.");
        if (names.Count == 1)
            return new(ReverseLookupStatus.Found, names.First(), "Name bei Das Örtliche gefunden.");

        // Never search the whole page for these phrases: the provider's FAQ repeats them.
        bool matchingSearchInput = nodes.Any(x => x.Tag == "input" && x.Attribute("name").Equals("ph", StringComparison.OrdinalIgnoreCase)
                && NumberMatches(x.Attribute("value"), number));
        HtmlNode[] emptyResults = nodes.Where(IsExplicitEmptyResult).ToArray();
        bool explicitNoHit = emptyResults.Any(x => HasNoResultMessage(VisibleText(x, 2000, cancellationToken)));
        if (matchingSearchInput && explicitNoHit)
            return new(ReverseLookupStatus.NotFound, "", "Das Örtliche meldet keinen öffentlichen Eintrag zu dieser Rufnummer.");

        // A page that has neither a proven identity nor an explicit no-hit message is not
        // evidence of an access block. Keep it uncached and let the batch continue past this
        // number. Explicit challenges above remain stopping results.
        return new(ReverseLookupStatus.UnrecognizedResponse, "",
            "Die Antwort von Das Örtliche konnte für diese Rufnummer nicht ausgewertet werden. Es wurde kein Name übernommen. "
            + $"Diagnose: Suchfeld passend: {(matchingSearchInput ? "ja" : "nein")}; "
            + $"Nichttrefferbereich: {emptyResults.Length}; Nichttreffertext: {(explicitNoHit ? "ja" : "nein")}; "
            + $"Trefferbereiche: {nodes.Count(x => IsResultCard(x) && !IsHidden(x))}; "
            + $"passende Namen: {names.Count}.");
    }

    private static void ReadJsonEntities(string json, string number, HashSet<string> names, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            Walk(document.RootElement);
        }
        catch (JsonException) { /* Another independent result representation may still be usable. */ }

        void Walk(JsonElement element)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Walk(item);
                return;
            }
            if (element.ValueKind != JsonValueKind.Object) return;
            if (element.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                && element.TryGetProperty("@type", out JsonElement type) && IsIdentityType(type)
                && HasExactJsonTelephone(element, number))
                AddName(names, name.GetString());

            foreach (JsonProperty property in element.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Walk(property.Value);
        }
    }

    private static bool IsIdentityType(JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array) return type.EnumerateArray().Any(IsIdentityType);
        if (type.ValueKind != JsonValueKind.String) return false;
        return IsIdentityTypeName(type.GetString() ?? "");
    }

    private static bool IsIdentityTypeName(string value)
    {
        int separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('#'));
        if (separator >= 0) value = value[(separator + 1)..];
        return IdentityTypes.Contains(value);
    }

    private static bool HasExactJsonTelephone(JsonElement entity, string number)
    {
        if (entity.TryGetProperty("telephone", out JsonElement telephone) && JsonTelephoneMatches(telephone, number)) return true;
        if (!entity.TryGetProperty("contactPoint", out JsonElement contact)) return false;
        return ContactMatches(contact);

        bool ContactMatches(JsonElement point)
        {
            if (point.ValueKind == JsonValueKind.Array) return point.EnumerateArray().Any(ContactMatches);
            return point.ValueKind == JsonValueKind.Object && point.TryGetProperty("telephone", out JsonElement phone)
                && JsonTelephoneMatches(phone, number);
        }
    }

    private static bool JsonTelephoneMatches(JsonElement value, string number) => value.ValueKind switch
    {
        JsonValueKind.String => NumberMatches(value.GetString(), number),
        JsonValueKind.Array => value.EnumerateArray().Any(x => JsonTelephoneMatches(x, number)),
        _ => false
    };

    private static void ReadMicrodata(HtmlNode scope, string number, HashSet<string> names, CancellationToken cancellationToken)
    {
        // Real detail pages intentionally hide their machine-readable Organization properties.
        // CSS visibility does not invalidate that metadata; inactive templates still do.
        HtmlNode[] properties = Descendants(scope, cancellationToken).Where(x => !IsInert(x) && IsSameEntity(x, scope)).ToArray();
        if (!properties.Any(x => HasToken(x.Attribute("itemprop"), "telephone") && NumberMatches(PropertyValue(x, cancellationToken, structured: true), number)))
            return;
        foreach (HtmlNode name in properties.Where(x => HasToken(x.Attribute("itemprop"), "name")))
            AddName(names, PropertyValue(name, cancellationToken, structured: true));
    }

    private static void ReadResultCard(HtmlNode card, string number, HashSet<string> names, CancellationToken cancellationToken)
    {
        if (IsHidden(card)) return;
        HtmlNode[] content = Descendants(card, cancellationToken).Where(x => !IsHidden(x) && IsSameEntity(x, card)).ToArray();
        bool matched = content.Any(x => x.Tag == "a" && x.Attribute("href").StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
            && NumberMatches(x.Attribute("href"), number));
        // Current result pages expose the complete phone as text in a phoneblock, not a tel: link.
        // Read each labelled span alone so that advertisements or a second number cannot be joined.
        matched |= content.Where(x => HasToken(x.Attribute("class"), "phoneblock"))
            .SelectMany(x => x.Children.Where(child => child.Tag == "span" && !IsHidden(child) && IsSameEntity(child, card)))
            .Any(x => LabelledTelephoneMatches(VisibleText(x, 200, cancellationToken), number));
        if (!matched) return;
        foreach (HtmlNode name in content.Where(x => Tokens(x.Attribute("class")).Any(NameClasses.Contains)
            || HasToken(x.Attribute("itemprop"), "name")))
            AddName(names, PropertyValue(name, cancellationToken));
    }

    private static bool IsSameEntity(HtmlNode node, HtmlNode owner)
    {
        for (HtmlNode? current = node; current is not null && current != owner; current = current.Parent)
            if (current.HasAttribute("itemscope") || IsResultCard(current)) return false;
        return true;
    }

    private static bool LabelledTelephoneMatches(string text, string number)
    {
        string value = text.Trim();
        if (value.StartsWith("Tel.", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        else if (value.StartsWith("Telefon:", StringComparison.OrdinalIgnoreCase)) value = value[8..];
        else return false;
        return NumberMatches(value.TrimStart().TrimStart(':').TrimStart(), number);
    }

    private static bool IsResultCard(HtmlNode node) => Tokens(node.Attribute("class")).Any(ResultClasses.Contains);

    private static bool NumberMatches(string? text, string number)
    {
        string value = new(WebUtility.HtmlDecode(text ?? "").Select(x => char.IsWhiteSpace(x) ? ' ' : x).ToArray());
        value = value.Trim();
        if (value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (value.Length is < 5 or > 100) return false;
        // A number belongs to the entity only if it contains an explicit international or trunk prefix.
        if (!(value.StartsWith('+') || value.StartsWith('0'))) return false;
        return ReverseLookupNumbers.Normalize(value, "+49", "") == number;
    }

    private static string PropertyValue(HtmlNode node, CancellationToken cancellationToken, bool structured = false)
    {
        if (node.HasAttribute("content")) return node.Attribute("content");
        if (node.Tag is "a" or "link" && node.Attribute("href").StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return node.Attribute("href");
        return VisibleText(node, 4096, cancellationToken, includeHiddenMetadata: structured);
    }

    private static void AddName(HashSet<string> names, string? value)
    {
        string decoded = WebUtility.HtmlDecode(value ?? "");
        if (decoded.Length > 1000 || decoded.Contains('<') || decoded.Contains('>')) return;
        var cleaned = new StringBuilder();
        bool space = false;
        foreach (char character in decoded)
        {
            if (char.IsWhiteSpace(character)) { space = cleaned.Length > 0; continue; }
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format) continue;
            if (space) { cleaned.Append(' '); space = false; }
            cleaned.Append(character);
        }
        string name = cleaned.ToString().Trim();
        if (name.Length is > 0 and <= 300 && name.Any(char.IsLetter)) names.Add(name);
    }

    private static bool IsChallenge(IEnumerable<HtmlNode> nodes, CancellationToken cancellationToken)
    {
        foreach (HtmlNode node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Tag == "title")
            {
                string title = VisibleText(node, 1000, cancellationToken);
                if (ContainsAny(title, "captcha", "access denied", "zugriff verweigert", "just a moment", "security check")) return true;
            }
            if (node.Tag == "script" && ContainsAny(node.Attribute("src"), "/cdn-cgi/challenge-platform/", "challenges.cloudflare.com/")) return true;
            if (node.Tag is "h1" or "h2" or "form")
            {
                string text = VisibleText(node, 3000, cancellationToken);
                if (ContainsAny(text, "bestätigen sie, dass sie ein mensch", "bestätigen sie, dass sie kein roboter",
                    "verify you are human", "ich bin kein roboter", "überprüfen sie, ob sie ein mensch")) return true;
            }
        }
        return false;
    }

    private static bool IsExplicitEmptyResult(HtmlNode node) => !IsHidden(node)
        && (Tokens(node.Attribute("class")).Any(EmptyClasses.Contains) || EmptyClasses.Contains(node.Attribute("id")));

    private static bool HasNoResultMessage(string text) => ContainsAny(
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
        "keine treffer", "keinen treffer", "keine ergebnisse", "kein ergebnis", "keinen eintrag", "kein eintrag", "nichts gefunden", "nicht erfolgreich");

    private static bool ContainsAny(string text, params string[] choices) => choices.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> Tokens(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static bool HasToken(string value, string token) => Tokens(value).Contains(token, StringComparer.OrdinalIgnoreCase);

    private static bool IsHidden(HtmlNode node)
    {
        if (IsInert(node)) return true;
        for (HtmlNode? current = node; current is not null; current = current.Parent)
        {
            if (current.HasAttribute("hidden") || current.Attribute("aria-hidden").Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            string style = current.Attribute("style").Replace(" ", "", StringComparison.Ordinal);
            if (ContainsAny(style, "display:none", "visibility:hidden")) return true;
        }
        return false;
    }

    private static bool IsInert(HtmlNode node)
    {
        for (HtmlNode? current = node; current is not null; current = current.Parent)
            if (current.Tag is "template" or "textarea" or "iframe" or "object" or "xmp" or "plaintext") return true;
        return false;
    }

    private static string VisibleText(HtmlNode node, int maximum, CancellationToken cancellationToken, bool includeHiddenMetadata = false)
    {
        var text = new StringBuilder();
        Append(node);
        return WebUtility.HtmlDecode(text.ToString());

        void Append(HtmlNode current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length >= maximum || current.Tag is "script" or "style"
                || (includeHiddenMetadata ? IsInert(current) : IsHidden(current))) return;
            if (current.Tag == "#text")
            {
                string part = current.RawText.ToString();
                text.Append(part.AsSpan(0, Math.Min(part.Length, maximum - text.Length)));
                return;
            }
            foreach (HtmlNode child in current.Children) Append(child);
            if (text.Length < maximum && current.Tag is "div" or "p" or "br" or "li" or "h1" or "h2" or "h3") text.Append(' ');
        }
    }

    private static IEnumerable<HtmlNode> Descendants(HtmlNode node, CancellationToken cancellationToken)
    {
        foreach (HtmlNode child in node.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return child;
            foreach (HtmlNode descendant in Descendants(child, cancellationToken)) yield return descendant;
        }
    }

    private static HtmlNode ReadHtml(string html, CancellationToken cancellationToken)
    {
        var root = new HtmlNode("#root", null);
        HtmlNode current = root;
        int cursor = 0, count = 1, depth = 0;
        while (cursor < html.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 60000) throw new InvalidDataException("Too many HTML tokens.");
            int start = html.IndexOf('<', cursor);
            if (start < 0) { AddText(html[cursor..]); break; }
            if (start > cursor) AddText(html[cursor..start]);
            if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                int commentEnd = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (commentEnd < 0) break;
                cursor = commentEnd + 3;
                continue;
            }
            int end = FindTagEnd(html, start + 1);
            if (end < 0) break;
            string tag = html[(start + 1)..end].Trim();
            cursor = end + 1;
            if (tag.Length == 0 || tag[0] is '!' or '?') continue;
            bool closing = tag[0] == '/';
            int position = closing ? 1 : 0;
            int nameStart = position;
            while (position < tag.Length && (char.IsAsciiLetterOrDigit(tag[position]) || tag[position] is '-' or ':' or '_')) position++;
            if (position == nameStart) continue;
            string name = tag[nameStart..position].ToLowerInvariant();
            if (closing)
            {
                HtmlNode? parent = current;
                while (parent != root && parent?.Tag != name) parent = parent?.Parent;
                if (parent is not null && parent != root)
                {
                    while (current != parent) { current = current.Parent!; depth--; }
                    current = current.Parent!; depth--;
                }
                continue;
            }
            var node = new HtmlNode(name, current);
            ReadAttributes(tag, position, node);
            current.Children.Add(node);
            if (name is "script" or "style" or "textarea" or "title")
            {
                int rawEnd = FindRawEnd(html, name, cursor);
                if (rawEnd < 0) break;
                if (name is "script" or "style") node.RawText.Append(html[cursor..rawEnd]);
                else
                {
                    var rawText = new HtmlNode("#text", node);
                    rawText.RawText.Append(html[cursor..rawEnd]);
                    node.Children.Add(rawText);
                }
                int closeEnd = FindTagEnd(html, rawEnd + 2 + name.Length);
                cursor = closeEnd >= 0 ? closeEnd + 1 : html.Length;
                continue;
            }
            if (VoidElements.Contains(name) || tag.EndsWith('/')) continue;
            if (++depth > 128) throw new InvalidDataException("HTML nesting limit.");
            current = node;
        }
        return root;

        void AddText(string value)
        {
            var node = new HtmlNode("#text", current);
            node.RawText.Append(value);
            current.Children.Add(node);
        }
    }

    private static int FindRawEnd(string html, string tag, int position)
    {
        string marker = "</" + tag;
        while (position < html.Length)
        {
            int found = html.IndexOf(marker, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            int after = found + marker.Length;
            if (after == html.Length || char.IsWhiteSpace(html[after]) || html[after] is '/' or '>') return found;
            position = after;
        }
        return -1;
    }

    private static int FindTagEnd(string html, int position)
    {
        char quote = '\0';
        for (int cursor = position; cursor < html.Length; cursor++)
        {
            char character = html[cursor];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '>') return cursor;
        }
        return -1;
    }

    private static void ReadAttributes(string text, int position, HtmlNode node)
    {
        while (position < text.Length)
        {
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == '/')) position++;
            if (position >= text.Length) break;
            int start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('=' or '/' or '>')) position++;
            if (position == start) { position++; continue; }
            string name = text[start..position];
            if (name.Length > 128 || node.Attributes.Count >= 100) throw new InvalidDataException("HTML attribute limit.");
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            string value = "";
            if (position < text.Length && text[position] == '=')
            {
                position++;
                while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                if (position < text.Length && text[position] is '\'' or '"')
                {
                    char quote = text[position++];
                    int end = text.IndexOf(quote, position);
                    if (end < 0) end = text.Length;
                    value = text[position..end];
                    position = Math.Min(end + 1, text.Length);
                }
                else
                {
                    start = position;
                    while (position < text.Length && !char.IsWhiteSpace(text[position])) position++;
                    value = text[start..position];
                }
            }
            if (value.Length > 65536) throw new InvalidDataException("HTML attribute value limit.");
            node.Attributes.TryAdd(name, WebUtility.HtmlDecode(value));
        }
    }

    private sealed class HtmlNode(string tag, HtmlNode? parent)
    {
        public string Tag { get; } = tag;
        public HtmlNode? Parent { get; } = parent;
        public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<HtmlNode> Children { get; } = [];
        public StringBuilder RawText { get; } = new();
        public string Attribute(string name) => Attributes.GetValueOrDefault(name, "");
        public bool HasAttribute(string name) => Attributes.ContainsKey(name);
    }
}
