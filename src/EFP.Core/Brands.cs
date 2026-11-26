using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EFP.Core;

public static class Brands
{
    public const string Unknown = "Inconnue";

    // Ordre important : les alias les plus spécifiques d'abord.
    // Les marques OEM sont ramenées au constructeur dont elles utilisent les pilotes.
    private static readonly (string Pattern, string Brand)[] Aliases =
    {
        (@"hewlett[\s-]*packard|\bhp\b|laserjet|officejet|deskjet|pagewide|designjet", "HP"),
        (@"samsung", "HP"), // les imprimantes Samsung sont reprises par HP (pilotes HP / Samsung Universal)
        (@"kyocera|ecosys|taskalfa|\butax\b|triumph[\s-]*adler", "Kyocera"),
        (@"canon|imagerunner|i-sensys|imageclass|\bipr\b|\bmf\d", "Canon"),
        (@"brother", "Brother"),
        (@"ricoh|\blanier\b|\bsavin\b|nashuatec|gestetner|rex[\s-]*rotary|infotec", "Ricoh"),
        (@"konica|minolta|bizhub|\bdevelop\b|\bineo\b", "Konica Minolta"),
        (@"fuji\s*xerox|fujifilm\s*business|xerox|versalink|altalink|workcentre|phaser", "Xerox"),
        (@"lexmark", "Lexmark"),
        (@"epson|workforce|ecotank", "Epson"),
        (@"sharp", "Sharp"),
        (@"toshiba|e-studio", "Toshiba"),
        (@"\boki\b|okidata", "OKI"),
        (@"olivetti", "Olivetti"),
        (@"dell", "Dell"),
        (@"pantum", "Pantum"),
        (@"zebra", "Zebra"),
    };

    public static IReadOnlyList<string> All { get; } =
        Aliases.Select(a => a.Brand).Distinct().OrderBy(b => b).ToList();

    /// <summary>Déduit la marque à partir d'un ou plusieurs textes (sysDescr, ID IEEE 1284, nom…).</summary>
    public static string Detect(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (var (pattern, brand) in Aliases)
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                    return brand;
        }
        return Unknown;
    }
}

/// <summary>Lecture de la chaîne IEEE 1284 "MFG:HP;MDL:LaserJet M404;CMD:PCL,PJL;".</summary>
public static class DeviceId1284
{
    public static Dictionary<string, string> Parse(string? deviceId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(deviceId)) return result;
        foreach (var part in deviceId.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf(':');
            if (idx <= 0) continue;
            var key = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            // Normalise les clés longues : MANUFACTURER -> MFG, MODEL -> MDL, COMMAND SET -> CMD
            key = key.ToUpperInvariant() switch
            {
                "MANUFACTURER" => "MFG",
                "MODEL" => "MDL",
                "COMMAND SET" => "CMD",
                "SERIALNUMBER" or "SERN" or "SN" => "SN",
                var k => k,
            };
            result.TryAdd(key, value);
        }
        return result;
    }
}

public static class ModelCleaner
{
    /// <summary>Nettoie un nom de modèle : retire la marque en préfixe, les mentions parasites et espaces multiples.</summary>
    public static string? Clean(string? model, string brand)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var m = model.Replace('\0', ' ').Trim();
        m = Regex.Replace(m, @"\s+", " ");
        // Préfixes marque / variantes
        string[] prefixes = brand switch
        {
            "HP" => new[] { "Hewlett-Packard", "Hewlett Packard", "HP" },
            "Konica Minolta" => new[] { "KONICA MINOLTA", "Konica Minolta", "KONICA", "Develop", "DEVELOP" },
            "Kyocera" => new[] { "KYOCERA Document Solutions", "Kyocera Document Solutions", "KYOCERA", "Kyocera", "UTAX", "TA" },
            "Xerox" => new[] { "FUJI XEROX", "FUJIFILM", "Xerox", "XEROX" },
            _ => new[] { brand },
        };
        foreach (var p in prefixes)
        {
            if (m.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
            {
                m = m[(p.Length + 1)..].Trim();
                break;
            }
        }
        // Mentions inutiles en fin de chaîne
        m = Regex.Replace(m, @"\s*(series|printer|mfp|;.*)$", "", RegexOptions.IgnoreCase).Trim();
        return m.Length == 0 ? null : m;
    }
}

public static class PasswordGenerator
{
    // Sans caractères ambigus (0/O, 1/l/I) ni caractères qui posent problème
    // sur les claviers d'imprimante ou dans les URL SMB (\ / : @ % & …).
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "-_.";

    public static string Generate(int length = 12)
    {
        if (length < 8) length = 8;
        var all = Lower + Upper + Digits + Symbols;
        var chars = new char[length];
        // Garantit au moins un caractère de chaque famille (exigences de complexité Windows)
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Symbols);
        for (var i = 4; i < length; i++) chars[i] = Pick(all);
        // Mélange (Fisher-Yates) puis commence par une lettre (certains claviers d'imprimante n'aiment pas un symbole en tête)
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        if (!char.IsLetter(chars[0]))
        {
            var k = Array.FindIndex(chars, char.IsLetter);
            (chars[0], chars[k]) = (chars[k], chars[0]);
        }
        return new string(chars);

        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}
