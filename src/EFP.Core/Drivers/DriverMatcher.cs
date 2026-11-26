using System.Text.RegularExpressions;

namespace EFP.Core.Drivers;

/// <summary>Choisit le meilleur pilote pour une imprimante : modèle exact &gt; universel de la marque &gt; générique Windows.</summary>
public sealed class DriverMatcher
{
    private readonly DriverLibrary _library;
    private readonly DriverCatalog _catalog;

    public DriverMatcher(DriverLibrary library, DriverCatalog catalog)
    {
        _library = library;
        _catalog = catalog;
    }

    public DriverChoice Choose(DiscoveredPrinter printer) => Candidates(printer).First();

    /// <summary>Tous les pilotes possibles, du meilleur au moins bon (le dernier est toujours un générique Windows).</summary>
    public List<DriverChoice> Candidates(DiscoveredPrinter printer)
    {
        var list = new List<(DriverChoice Choice, int Score)>();
        var sameBrand = _library.Drivers.Where(d => d.Brand == printer.Brand && printer.Brand != Brands.Unknown).ToList();

        // 0. Pilotes importés à la main : prioritaires pour leur marque (et proposés pour les imprimantes de marque inconnue)
        foreach (var d in _library.Drivers.Where(d => d.IsImported && (d.Brand == printer.Brand || printer.Brand == Brands.Unknown)))
            list.Add((new DriverChoice(d.Name, d.InfPath, DriverMatchKind.Imported, Loc.T("Pilote importé à la main", "Manually imported driver")),
                (d.Brand == printer.Brand ? 1500 : 200) + PdlBonus(d.Name)));

        // 1. Modèle exact (seulement si drivers.json le demande : par défaut, un seul pilote universel par marque)
        if (_catalog.PreferExactModel && !string.IsNullOrWhiteSpace(printer.Model))
        {
            foreach (var d in sameBrand.Where(d => !d.IsImported))
            {
                var score = ModelScore(printer.Model!, d.Name);
                if (score > 0)
                    list.Add((new DriverChoice(d.Name, d.InfPath, DriverMatchKind.ExactModel,
                        Loc.T($"Pilote du modèle trouvé dans {Path.GetFileName(d.InfPath)}", $"Model driver found in {Path.GetFileName(d.InfPath)}")), 1000 + score + PdlBonus(d.Name)));
            }
        }

        // 2. Universel de la marque, dans l'ordre de préférence de drivers.json
        var rule = _catalog.Universal.FirstOrDefault(r => r.Brand.Equals(printer.Brand, StringComparison.OrdinalIgnoreCase));
        if (rule is not null)
        {
            for (var i = 0; i < rule.Patterns.Count; i++)
            {
                Regex rx;
                try { rx = new Regex(rule.Patterns[i], RegexOptions.IgnoreCase); }
                catch (ArgumentException) { continue; } // motif invalide dans drivers.json
                foreach (var d in _library.Drivers.Where(d => !d.IsImported && rx.IsMatch(d.Name)))
                    list.Add((new DriverChoice(d.Name, d.InfPath, DriverMatchKind.BrandUniversal,
                        Loc.T($"Pilote universel {printer.Brand}", $"{printer.Brand} universal driver")), 500 - i * 10 + PdlBonus(d.Name)));
            }
        }

        // 2b. Pilote exact du modèle venu de Windows Update : après l'universel PostScript, avant le générique
        if (!string.IsNullOrWhiteSpace(printer.Model))
        {
            foreach (var d in sameBrand.Where(d => d.IsWindowsUpdate))
            {
                var score = ModelScore(printer.Model!, d.Name);
                if (score > 0)
                    list.Add((new DriverChoice(d.Name, d.InfPath, DriverMatchKind.ExactModel, Loc.T("Pilote du modèle (Windows Update)", "Model driver (Windows Update)")),
                        300 + score / 10 + PdlBonus(d.Name)));
            }
        }

        // 3. Génériques intégrés à Windows
        for (var i = 0; i < _catalog.InboxFallback.Count; i++)
            list.Add((new DriverChoice(_catalog.InboxFallback[i], null, DriverMatchKind.GenericInbox,
                Loc.T("Pilote générique Windows (fonctions limitées : pas de finition, agrafage, codes…)", "Generic Windows driver (limited features: no finishing, stapling, codes…)")), 100 - i));

        if (list.Count == 0)
            list.Add((new DriverChoice("Microsoft PCL6 Class Driver", null, DriverMatchKind.GenericInbox, Loc.T("Pilote générique Windows", "Generic Windows driver")), 1));

        return list
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Choice.DriverName.Length)
            .Select(x => x.Choice)
            .DistinctBy(c => (c.DriverName.ToLowerInvariant(), c.InfPath))
            .ToList();
    }

    public static bool IsPostScript(string driverName) =>
        Regex.IsMatch(driverName, @"\bPS\b|PostScript|BR-Script|PS3|KPDL", RegexOptions.IgnoreCase);

    /// <summary>Bonus pour PostScript (langage préféré), puis PCL ; XPS et fax pénalisés.</summary>
    private static int PdlBonus(string name)
    {
        if (Regex.IsMatch(name, @"\bPS\b|PostScript|BR-Script|PS3|KPDL", RegexOptions.IgnoreCase)) return 5;
        if (Regex.IsMatch(name, @"PCL\s?6|PCL-?XL|\bKX\b|UFR\s?II", RegexOptions.IgnoreCase)) return 2;
        if (Regex.IsMatch(name, @"\bXPS\b|\bFAX\b", RegexOptions.IgnoreCase)) return -5;
        return 0;
    }

    /// <summary>
    /// Score de correspondance modèle ↔ nom de pilote (0 = aucune).
    /// 100 : le modèle complet est contenu dans le nom du pilote.
    /// 60  : la référence principale (ex. "M404" pour "M404dn") apparaît comme mot ou série ("M404-M405").
    /// </summary>
    public static int ModelScore(string model, string driverName)
    {
        var nm = Normalize(model);
        var nd = Normalize(driverName);
        if (nm.Length >= 3 && nd.Contains(nm)) return 100;

        var driverTokens = Tokens(driverName);
        var keyTokens = Tokens(model).Where(t => t.Any(char.IsDigit) && t.Any(char.IsLetter) || t.Count(char.IsDigit) >= 3).ToList();
        if (keyTokens.Count == 0) return 0;

        // La "famille" (mots sans chiffres : taskalfa, ecosys, bizhub, laserjet…) doit aussi correspondre si le pilote en cite une
        var familyWords = Tokens(model).Where(t => !t.Any(char.IsDigit) && t.Length >= 4).ToList();
        if (familyWords.Count > 0 && !familyWords.Any(f => driverTokens.Contains(f)) && driverTokens.Any(t => !t.Any(char.IsDigit) && KnownFamilies.Contains(t)))
            return 0;

        foreach (var key in keyTokens.OrderByDescending(t => t.Length))
        {
            var core = CoreReference(key); // m404dn -> m404 ; 2553ci -> 2553
            if (core.Length < 3) continue;
            foreach (var dt in driverTokens)
            {
                if (dt == key) return 80;
                if (CoreReference(dt) == core) return 60;
            }
        }
        return 0;
    }

    private static readonly HashSet<string> KnownFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "taskalfa", "ecosys", "bizhub", "laserjet", "officejet", "pagewide", "deskjet", "imagerunner", "imageclass",
        "isensys", "versalink", "altalink", "workcentre", "phaser", "estudio", "workforce", "ecotank",
    };

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

    private static List<string> Tokens(string s) =>
        Regex.Split(s.ToLowerInvariant().Replace("e-studio", "estudio").Replace("i-sensys", "isensys"), @"[^a-z0-9]+")
            .Where(t => t.Length > 0).ToList();

    /// <summary>Partie "référence" d'un token : lettres de tête + chiffres, sans le suffixe de variante (dn, ci, cdw…).</summary>
    private static string CoreReference(string token)
    {
        var m = Regex.Match(token, @"^[a-z]*\d+");
        return m.Success ? m.Value : token;
    }
}
