using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CM.AnrufMonitor.Core;

public static class ContactBook
{
    private const int MaxImportBytes = 20 * 1024 * 1024;

    /// <summary>Normalizes exact numbers, never matching only a trailing suffix. Unknown numbers return empty.</summary>
    public static string NormalizeNumber(string? number, string countryCode = "+49", string areaCode = "")
    {
        if (string.IsNullOrWhiteSpace(number) || number.Length > 256) return "";
        string value = number.Trim();
        // Some exported address books use the international optional trunk prefix +49 (0)30.
        if (value.StartsWith('+') || value.StartsWith("00", StringComparison.Ordinal))
            value = value.Replace("(0)", "", StringComparison.Ordinal);
        if (value.Any(c => !(char.IsAsciiDigit(c) || c is '+' or '*' or '#' or ' ' or '\t' or '-' or '(' or ')' or '.' or '/')))
            return "";
        value = new string(value.Where(c => char.IsAsciiDigit(c) || c is '+' or '*' or '#').ToArray());
        if (value.Length == 0 || !value.Any(char.IsAsciiDigit)) return "";
        if (value.StartsWith('*') || value.StartsWith('#'))
            return value.Contains('+') ? "" : value; // Preserve internal dial strings such as **610.
        if (value.Contains('*') || value.Contains('#')) return "";
        if (value.StartsWith("00", StringComparison.Ordinal)) value = "+" + value[2..];
        if (value.StartsWith('+'))
            return value.Length > 1 && value[1..].All(char.IsAsciiDigit) ? value : "";
        if (value.Contains('+')) return "";
        string countryDigits = new string((countryCode ?? "").Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        if (countryDigits.Length is < 1 or > 3) return "local:" + value;
        string prefix = "+" + countryDigits;
        if (value.StartsWith('0') && value.Length > 1) return prefix + value[1..];
        string localArea = new string((areaCode ?? "").Where(char.IsAsciiDigit).ToArray());
        if (localArea.StartsWith("00" + countryDigits, StringComparison.Ordinal)) localArea = localArea[(countryDigits.Length + 2)..];
        else if ((areaCode ?? "").TrimStart().StartsWith('+') && localArea.StartsWith(countryDigits, StringComparison.Ordinal))
            localArea = localArea[countryDigits.Length..];
        localArea = localArea.TrimStart('0');
        return localArea.Length > 0 ? prefix + localArea + value : "local:" + value;
    }

    public static string? ResolveName(string? number, IEnumerable<Contact> contacts, string countryCode = "+49", string areaCode = "")
    {
        string normalized = NormalizeNumber(number, countryCode, areaCode);
        if (normalized.Length == 0) return null;
        // If different names share a number, show the ambiguity instead of choosing silently.
        string[] matches = contacts.Where(x => !string.IsNullOrWhiteSpace(x.Name)
                && NormalizeNumber(x.Number, countryCode, areaCode) == normalized)
            .Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        return matches.Length == 0 ? null : string.Join(" / ", matches);
    }

    public static IReadOnlyList<Contact> ImportFritzXml(string path)
    {
        using var input = OpenImport(path);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxImportBytes,
            IgnoreComments = true
        };
        using var reader = XmlReader.Create(input, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root?.Name.LocalName is not ("phonebooks" or "phonebook"))
            throw new InvalidDataException("Dies ist kein FRITZ!Box-Telefonbuch. Bitte die XML-Datei über Telefonie > Telefonbuch > Sichern exportieren.");
        var results = new List<Contact>();
        foreach (XElement contact in document.Descendants().Where(x => x.Name.LocalName == "contact"))
        {
            string? rawName = contact.Descendants().FirstOrDefault(x => x.Name.LocalName == "realName")?.Value;
            string name = CleanName(rawName);
            if (name.Length == 0) continue;
            foreach (XElement number in contact.Descendants().Where(x => x.Name.LocalName == "number"))
            {
                string value = number.Value.Trim();
                if (NormalizeNumber(value).Length > 0) results.Add(new Contact(name, value));
                if (results.Count > 50000) throw new InvalidDataException("Das Telefonbuch enthält zu viele Rufnummern.");
            }
        }
        return results.Distinct().ToArray();
    }

    public static IReadOnlyList<Contact> ImportCsv(string path)
    {
        using var input = OpenImport(path);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        string firstLine = text.Split('\n', 2)[0];
        char delimiter = firstLine.Count(x => x == ';') >= firstLine.Count(x => x == ',') ? ';' : ',';
        List<string[]> rows = ParseCsv(text, delimiter);
        if (rows.Count == 0) return [];
        string[] header = rows[0].Select(x => x.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToArray();
        int nameColumn = Array.FindIndex(header, x => x is "name" or "kontakt");
        int numberColumn = Array.FindIndex(header, x => x is "nummer" or "rufnummer" or "number" or "telefon" or "phone");
        if (nameColumn < 0 || numberColumn < 0)
            throw new InvalidDataException("Die CSV-Datei braucht die Spalten Name und Nummer. Trennzeichen: Semikolon oder Komma, Zeichensatz UTF-8.");
        var result = new List<Contact>();
        foreach (string[] row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Length <= Math.Max(nameColumn, numberColumn))
                throw new InvalidDataException("Mindestens eine CSV-Zeile enthält zu wenige Spalten.");
            string name = CleanName(row[nameColumn]);
            string number = row[numberColumn].Trim();
            if (name.Length == 0 || NormalizeNumber(number).Length == 0)
                throw new InvalidDataException("Mindestens eine CSV-Zeile enthält keinen gültigen Namen oder keine gültige Rufnummer.");
            result.Add(new Contact(name, number));
            if (result.Count > 50000) throw new InvalidDataException("Die CSV-Datei enthält zu viele Rufnummern.");
        }
        return result.Distinct().ToArray();
    }

    private static FileStream OpenImport(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > MaxImportBytes) throw new InvalidDataException("Die Importdatei darf höchstens 20 MB groß sein.");
        return File.OpenRead(fullPath);
    }
    private static string CleanName(string? value)
    {
        string name = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 300) throw new InvalidDataException("Ein Kontaktname ist zu lang.");
        return name;
    }
    private static List<string[]> ParseCsv(string text, char delimiter)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, quoteClosed = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { quoted = false; quoteClosed = true; }
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0 && !quoteClosed) quoted = true;
            else if (c == delimiter || c is '\r' or '\n')
            {
                row.Add(field.ToString()); field.Clear(); quoteClosed = false;
                if (row.Count > 100) throw new InvalidDataException("Die CSV-Datei enthält zu viele Spalten.");
                if (c != delimiter)
                {
                    rows.Add(row.ToArray()); row.Clear();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
            }
            else if (quoteClosed)
            {
                if (!char.IsWhiteSpace(c)) throw new InvalidDataException("Ungültige CSV-Anführungszeichen.");
            }
            else if (c == '"') throw new InvalidDataException("Ungültige CSV-Anführungszeichen.");
            else field.Append(c);
            if (field.Length > 4096) throw new InvalidDataException("Ein CSV-Feld ist zu lang.");
        }
        if (quoted) throw new InvalidDataException("Nicht geschlossene CSV-Anführungszeichen.");
        if (field.Length > 0 || row.Count > 0 || quoteClosed) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
