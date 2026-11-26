using System.Text;
using System.Text.RegularExpressions;

namespace EFP.Core.Drivers;

public sealed record InfModel(string Name, string Manufacturer, IReadOnlyList<string> HardwareIds);

public sealed class InfInfo
{
    public required string Path { get; init; }
    public string? Provider { get; init; }
    public string? DriverVersion { get; init; }
    public bool IsPrinterClass { get; init; }
    public List<InfModel> Models { get; } = new();
}

/// <summary>
/// Lecteur INF simplifié : lit [Version], [Strings], [Manufacturer] et les sections de modèles 64 bits
/// pour récupérer les noms exacts de pilotes à passer à Add-PrinterDriver.
/// </summary>
public static class InfParser
{
    private const string PrinterClassGuid = "{4d36e979-e325-11ce-bfc1-08002be10318}";
    private static readonly Regex StringToken = new(@"%([^%]+)%", RegexOptions.Compiled);

    public static InfInfo ParseFile(string path)
    {
        string text;
        using (var reader = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        return Parse(text, path);
    }

    public static InfInfo Parse(string text, string path = "<memoire>")
    {
        var sections = ReadSections(text);

        // Chaînes : [Strings] d'abord, puis les variantes localisées en complément
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lines) in sections.OrderBy(s => s.Key.Equals("Strings", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (!name.StartsWith("Strings", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var line in lines)
            {
                var (key, value) = SplitKeyValue(line);
                if (key is not null) strings.TryAdd(Unquote(key), Unquote(value));
            }
        }
        string Resolve(string s) => StringToken.Replace(Unquote(s.Trim()), m => strings.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        string? GetVersion(string key)
        {
            if (!sections.TryGetValue("Version", out var lines)) return null;
            foreach (var line in lines)
            {
                var (k, v) = SplitKeyValue(line);
                if (k is not null && k.Equals(key, StringComparison.OrdinalIgnoreCase)) return Resolve(v);
            }
            return null;
        }

        var cls = GetVersion("Class");
        var guid = GetVersion("ClassGuid");
        var info = new InfInfo
        {
            Path = path,
            Provider = GetVersion("Provider"),
            DriverVersion = GetVersion("DriverVer"),
            IsPrinterClass = string.Equals(cls, "Printer", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(guid, PrinterClassGuid, StringComparison.OrdinalIgnoreCase),
        };
        if (!info.IsPrinterClass || !sections.TryGetValue("Manufacturer", out var mfgLines)) return info;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in mfgLines)
        {
            var (k, v) = SplitKeyValue(line);
            if (k is null) continue;
            var manufacturer = Resolve(k);
            var parts = v.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) continue;
            var baseSection = parts[0];
            var decorations = parts.Skip(1).ToList();

            // Sections à lire : décorations amd64 (ou "NT" générique), sinon la section non décorée
            var targets = new List<string>();
            foreach (var d in decorations)
            {
                var low = d.ToLowerInvariant();
                if (low.StartsWith("ntamd64") || low == "nt" || low.StartsWith("nt.")) targets.Add($"{baseSection}.{d}");
            }
            if (decorations.Count == 0) targets.Add(baseSection);

            foreach (var target in targets)
            {
                if (!sections.TryGetValue(target, out var modelLines)) continue;
                foreach (var ml in modelLines)
                {
                    var (mk, mv) = SplitKeyValue(ml);
                    if (mk is null) continue;
                    var modelName = Resolve(mk);
                    if (modelName.Length == 0 || !seen.Add(modelName)) continue;
                    var hwids = mv.Split(',').Skip(1).Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
                    info.Models.Add(new InfModel(modelName, manufacturer, hwids));
                }
            }
        }
        return info;
    }

    private static Dictionary<string, List<string>> ReadSections(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current))
                {
                    current = new List<string>();
                    sections[name] = current;
                }
                continue;
            }
            current?.Add(line);
        }
        return sections;
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static (string? Key, string Value) SplitKeyValue(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == '=' && !inQuotes) return (line[..i].Trim(), line[(i + 1)..].Trim());
        }
        return (null, line);
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Replace("\"\"", "\"");
    }
}

/// <summary>Recherche des .inf sans tenir compte de la casse (BRPRC20A.INF…), y compris hors Windows.</summary>
public static class Infs
{
    public static readonly EnumerationOptions All = new() { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
}
