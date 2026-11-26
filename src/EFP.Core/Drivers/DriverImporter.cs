using EFP.Core.Updates;

namespace EFP.Core.Drivers;

public sealed class ImportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Folder { get; set; }
    public string? Brand { get; set; }
    public List<string> DriverNames { get; } = new();
}

/// <summary>
/// Ajoute à la clé un pilote fourni par le technicien (quand EFP n'a pu télécharger ni PostScript ni PCL) :
/// archive .zip, .exe / .7z / .cab (7-Zip), .msi, fichier .inf (avec les fichiers de son dossier) ou dossier entier.
/// Le pilote est rangé dans Drivers\&lt;Marque&gt;\import-&lt;nom&gt; et devient prioritaire pour sa marque.
/// </summary>
public sealed class DriverImporter
{
    public const string FolderPrefix = "import-";
    private readonly string _driversRoot;
    private readonly DriverUpdater _extractor;

    public DriverImporter(string driversRoot, DriverUpdater extractor)
    {
        _driversRoot = driversRoot;
        _extractor = extractor;
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken ct = default)
    {
        var result = new ImportResult();
        var name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        var staging = Path.Combine(_driversRoot, DriverUpdater.DownloadsFolder, "import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            if (Directory.Exists(path))
                CopyDirectory(path, staging);
            else if (File.Exists(path))
            {
                if (Path.GetExtension(path).Equals(".inf", StringComparison.OrdinalIgnoreCase))
                    CopyDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!, staging); // l'INF a besoin des fichiers voisins (.cab, .dll, .gpd…)
                else if (!await _extractor.ExtractToAsync(path, staging, ct).ConfigureAwait(false))
                {
                    result.Message = Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".7z" or ".cab"
                        ? Loc.T("Impossible d'ouvrir cette archive : ajoute 7za.exe dans le dossier Tools, ou décompresse-la puis importe le dossier.", "Cannot open this archive: add 7za.exe to the Tools folder, or extract it and import the folder.")
                        : Loc.T("Format non pris en charge : importe un .zip, un .inf, un dossier, ou un .exe avec 7za.exe dans Tools.", "Unsupported format: import a .zip, an .inf, a folder, or an .exe with 7za.exe in Tools.");
                    return result;
                }
            }
            else
            {
                result.Message = Loc.T("Fichier introuvable.", "File not found.");
                return result;
            }

            // Pilotes d'imprimante 64 bits trouvés
            var lib = DriverLibrary.Empty(staging);
            foreach (var inf in Directory.EnumerateFiles(staging, "*.inf", Infs.All))
            {
                try { lib.AddInf(InfParser.ParseFile(inf)); } catch { /* INF illisible ignoré */ }
            }
            if (lib.Drivers.Count == 0)
            {
                result.Message = Loc.T("Aucun pilote d'imprimante 64 bits (.inf) dans ce que tu as importé.", "No 64-bit printer driver (.inf) in what you imported.");
                return result;
            }

            var brand = lib.Drivers.GroupBy(d => d.Brand).OrderByDescending(g => g.Key != Brands.Unknown).ThenByDescending(g => g.Count()).First().Key;
            var target = Path.Combine(_driversRoot, DriverUpdater.SafeName(brand), FolderPrefix + DriverUpdater.SafeName(name));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);

            result.Success = true;
            result.Brand = brand;
            result.Folder = target;
            result.DriverNames.AddRange(lib.Drivers.Select(d => d.Name).Distinct()
                .OrderByDescending(DriverMatcher.IsPostScript).ThenBy(n => n.Length));
            result.Message = Loc.T($"{result.DriverNames.Count} pilote(s) {brand} importé(s) : ", $"{result.DriverNames.Count} {brand} driver(s) imported: ") + $"{string.Join(", ", result.DriverNames.Take(3))}{(result.DriverNames.Count > 3 ? "…" : "")}";
            return result;
        }
        finally
        {
            if (Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch { } }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
