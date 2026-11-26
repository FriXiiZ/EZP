using System.Text.RegularExpressions;

namespace EFP.Core.Platform;

/// <summary>Un modèle d'imprimante connu de CUPS (ligne de « lpinfo -m ») ou un fichier PPD.</summary>
public sealed record CupsModel(string Id, string Description, bool IsFile = false, bool IsImported = false);

/// <summary>
/// Pilotes sous Linux / macOS, du meilleur au moins bon :
/// PPD importé → sans pilote (IPP Everywhere / AirPrint, si l'imprimante parle IPP) → PPD du modèle fourni par le système
/// (hplip, foomatic, gutenprint…) → PostScript générique → PCL générique.
/// </summary>
public sealed class CupsDriverSource
{
    private List<CupsModel> _models = new();
    public IReadOnlyList<CupsModel> Models => _models;

    public static List<CupsModel> ParseLpinfo(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => { var i = l.IndexOf(' '); return i > 0 ? new CupsModel(l[..i], l[(i + 1)..].Trim()) : null; })
        .Where(m => m is not null).Select(m => m!).ToList();

    /// <summary>Charge la liste des pilotes CUPS (lpinfo -m, quelques secondes) et les PPD du dossier Drivers.</summary>
    public async Task LoadAsync(string? driversFolder, CancellationToken ct = default)
    {
        var list = new List<CupsModel>();
        try { list.AddRange(ParseLpinfo(await ShellRunner.CaptureAsync("lpinfo", new[] { "-m" }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false))); }
        catch (Exception) { /* CUPS absent ou lpinfo indisponible : génériques seulement */ }
        if (driversFolder is not null && Directory.Exists(driversFolder))
            foreach (var ppd in Directory.EnumerateFiles(driversFolder, "*.ppd", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }))
                list.Add(new CupsModel(ppd, ReadNickName(ppd) ?? Path.GetFileNameWithoutExtension(ppd), true,
                    ppd.Replace('\\', '/').Split('/').Any(s => s.StartsWith("import-", StringComparison.OrdinalIgnoreCase))));
        _models = list;
    }

    public void SetModels(IEnumerable<CupsModel> models) => _models = models.ToList();

    private static bool IsFax(CupsModel m) =>
        m.Id.StartsWith("driverless-fax", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(m.Description, @"\bfax\b", RegexOptions.IgnoreCase);

    private static string CleanDriverless(string d) => Regex.Replace(d, @",\s*driverless.*$", "", RegexOptions.IgnoreCase).Trim();

    /// <summary>
    /// Demande à CUPS les pilotes de ces modèles précis (lpinfo --make-and-model) : les descriptions « sans pilote »
    /// n'apparaissent que quand l'imprimante est visible sur le réseau, souvent après le chargement de départ.
    /// </summary>
    public async Task<bool> RefreshForAsync(IEnumerable<string> makeAndModels, CancellationToken ct = default)
    {
        var found = new List<CupsModel>();
        foreach (var mm in makeAndModels.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var output = await ShellRunner.CaptureAsync("lpinfo", new[] { "--make-and-model", mm, "-m" }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                found.AddRange(ParseLpinfo(output));
            }
            catch (Exception) { }
        }
        var current = _models;
        var fresh = found.Where(m => current.All(x => x.Id != m.Id)).GroupBy(m => m.Id).Select(g => g.First()).ToList();
        if (fresh.Count == 0) return false;
        _models = current.Concat(fresh).ToList(); // remplacement d'un bloc : pas de liste modifiée pendant qu'on la lit
        return true;
    }

    public static string? ReadNickName(string ppd)
    {
        try
        {
            foreach (var line in File.ReadLines(ppd).Take(400))
            {
                var m = Regex.Match(line, @"^\*(NickName|ModelName):\s*""([^""]+)""");
                if (m.Success) return m.Groups[2].Value;
            }
        }
        catch (IOException) { }
        return null;
    }

    public List<DriverChoice> Candidates(DiscoveredPrinter printer)
    {
        var list = new List<(DriverChoice Choice, int Score)>();
        var model = printer.Model ?? "";

        foreach (var m in _models.Where(m => m.IsImported))
        {
            var brand = Brands.Detect(m.Description);
            if (brand == printer.Brand || brand == Brands.Unknown)
                list.Add((new DriverChoice(m.Description, m.Id, DriverMatchKind.Imported, Loc.T("PPD importé", "Imported PPD")), 2000 + Drivers.DriverMatcher.ModelScore(model, m.Description)));
        }

        if (printer.HasIpp)
            list.Add((new DriverChoice("IPP / AirPrint", CupsScripts.Everywhere, DriverMatchKind.Driverless,
                Loc.T("L'imprimante parle IPP : CUPS se règle tout seul", "The printer speaks IPP: CUPS configures itself")), 1500));

        // Fedora / cups-filters 2 : description « sans pilote » générée par CUPS pour CE modèle (vue sur le réseau)
        if (!string.IsNullOrWhiteSpace(model))
            foreach (var m in _models.Where(m => m.Id.StartsWith("driverless:", StringComparison.OrdinalIgnoreCase) && !IsFax(m)))
                if (Drivers.DriverMatcher.ModelScore(model, m.Description) > 0)
                    list.Add((new DriverChoice($"IPP / AirPrint — {CleanDriverless(m.Description)}", m.Id, DriverMatchKind.Driverless,
                        Loc.T("Description générée par CUPS à partir de l'imprimante", "Description generated by CUPS from the printer")), 1600));

        if (!string.IsNullOrWhiteSpace(model) && printer.Brand != Brands.Unknown)
        {
            foreach (var m in _models.Where(m => !m.IsImported && !m.Description.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)
                                                 && m.Id != CupsScripts.Everywhere && !m.Id.StartsWith("driverless", StringComparison.OrdinalIgnoreCase) && !IsFax(m)))
            {
                if (Brands.Detect(m.Description) != printer.Brand) continue;
                var score = Drivers.DriverMatcher.ModelScore(model, m.Description);
                if (score <= 0) continue;
                var bonus = Regex.IsMatch(m.Description, @"postscript|\bps\b|br-script", RegexOptions.IgnoreCase) ? 20 : 0;
                list.Add((new DriverChoice(m.Description, m.Id, DriverMatchKind.ExactModel, Loc.T("Pilote du modèle (CUPS)", "Model driver (CUPS)")), 1000 + score + bonus));
            }
        }

        list.Add((new DriverChoice(Loc.T("PostScript générique", "Generic PostScript"), CupsScripts.GenericPostScript, DriverMatchKind.GenericInbox, Loc.T("Générique CUPS", "CUPS generic")), 100));
        list.Add((new DriverChoice(Loc.T("PCL générique", "Generic PCL"), "drv:///sample.drv/generpcl.ppd", DriverMatchKind.GenericInbox, Loc.T("Générique CUPS", "CUPS generic")), 50));

        return list.OrderByDescending(x => x.Score)
            .GroupBy(x => x.Choice.InfPath).Select(g => g.First().Choice)
            .Take(8).ToList();
    }
}
