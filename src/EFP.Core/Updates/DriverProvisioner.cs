using EFP.Core.Drivers;

namespace EFP.Core.Updates;

public enum ProvisionOutcome
{
    /// <summary>Le pilote universel PostScript de la marque est déjà sur la clé.</summary>
    AlreadyPostScript,
    /// <summary>PostScript téléchargé.</summary>
    DownloadedPostScript,
    /// <summary>Pas de PostScript disponible : un PCL est déjà sur la clé.</summary>
    AlreadyPcl,
    /// <summary>Pas de PostScript disponible : PCL de secours téléchargé.</summary>
    DownloadedPcl,
    /// <summary>Téléchargement manuel nécessaire (licence à accepter sur le site du constructeur).</summary>
    ManualOnly,
    /// <summary>Rien trouvé (pas d'internet, marque non suivie…).</summary>
    Failed,
}

public sealed class ProvisionResult
{
    public required string Brand { get; init; }
    public ProvisionOutcome Outcome { get; set; }
    public string? DriverName { get; set; }
    public string? PageUrl { get; set; }
    public List<string> Log { get; } = new();
    public bool Ready => Outcome is ProvisionOutcome.AlreadyPostScript or ProvisionOutcome.DownloadedPostScript
                                  or ProvisionOutcome.AlreadyPcl or ProvisionOutcome.DownloadedPcl;
}

/// <summary>
/// Après le scan : pour chaque marque trouvée, s'assure que le bon pilote universel est sur la clé.
/// Ordre : PostScript déjà présent › PostScript à télécharger › PCL déjà présent › PCL de secours à télécharger › page du constructeur.
/// </summary>
public sealed class DriverProvisioner
{
    private readonly DriverUpdater _updater;
    private readonly DriverFeed _feed;
    private readonly DriverCatalog _catalog;
    private readonly Func<DriverLibrary> _loadLibrary;

    public DriverProvisioner(DriverUpdater updater, DriverFeed feed, DriverCatalog catalog, Func<DriverLibrary> loadLibrary)
    {
        _updater = updater;
        _feed = feed;
        _catalog = catalog;
        _loadLibrary = loadLibrary;
    }

    /// <summary>Meilleur pilote universel de la marque actuellement sur la clé (null s'il n'y en a pas).</summary>
    public string? BestUniversal(DriverLibrary lib, string brand)
    {
        var probe = new DiscoveredPrinter { IpAddress = "0.0.0.0", Brand = brand };
        return new DriverMatcher(lib, _catalog).Candidates(probe)
            .FirstOrDefault(c => c.Kind == DriverMatchKind.BrandUniversal)?.DriverName;
    }

    public async Task<ProvisionResult> EnsureAsync(string brand, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ProvisionResult { Brand = brand };
        if (brand == Brands.Unknown)
        {
            result.Outcome = ProvisionOutcome.Failed;
            result.Log.Add(Loc.T("Marque inconnue : pilote générique Windows.", "Unknown brand: generic Windows driver."));
            return result;
        }

        var current = BestUniversal(_loadLibrary(), brand);
        if (current is not null && DriverMatcher.IsPostScript(current))
        {
            result.Outcome = ProvisionOutcome.AlreadyPostScript;
            result.DriverName = current;
            return result;
        }

        var packages = _feed.Packages.Where(p => p.Brand.Equals(brand, StringComparison.OrdinalIgnoreCase)).ToList();
        result.PageUrl = packages.Select(p => p.Page).FirstOrDefault(u => u is not null);

        // 1. PostScript
        foreach (var pkg in packages.Where(p => !p.Fallback))
        {
            var name = await TryDownloadAsync(pkg, result, progress, ct).ConfigureAwait(false);
            if (name is not null && DriverMatcher.IsPostScript(name))
            {
                result.Outcome = ProvisionOutcome.DownloadedPostScript;
                result.DriverName = name;
                return result;
            }
        }

        // 2. PCL déjà présent (ou apporté par le paquet PostScript, ex. Kyocera KX)
        current = BestUniversal(_loadLibrary(), brand);
        if (current is not null)
        {
            result.Outcome = ProvisionOutcome.AlreadyPcl;
            result.DriverName = current;
            result.Log.Add(Loc.T($"Pas de PostScript disponible pour {brand} : PCL utilisé ({current}).", $"No PostScript available for {brand}: PCL used ({current})."));
            return result;
        }

        // 3. PCL de secours
        foreach (var pkg in packages.Where(p => p.Fallback))
        {
            var name = await TryDownloadAsync(pkg, result, progress, ct).ConfigureAwait(false);
            if (name is not null)
            {
                result.Outcome = ProvisionOutcome.DownloadedPcl;
                result.DriverName = name;
                return result;
            }
        }

        result.Outcome = result.PageUrl is not null && packages.Any(p => p.Sources.All(s => s.Type == "page"))
            ? ProvisionOutcome.ManualOnly
            : ProvisionOutcome.Failed;
        if (result.Outcome == ProvisionOutcome.ManualOnly)
            result.Log.Add(Loc.T($"{brand} : licence à accepter sur le site du constructeur ({result.PageUrl}).", $"{brand}: license must be accepted on the manufacturer's site ({result.PageUrl})."));
        else if (packages.Count == 0)
            result.Log.Add(Loc.T($"{brand} : marque pas encore suivie par EZP, pilote générique Windows.", $"{brand}: brand not yet supported by EZP, generic Windows driver."));
        return result;
    }

    /// <summary>Télécharge un paquet et renvoie le pilote universel de la marque disponible ensuite.</summary>
    private async Task<string?> TryDownloadAsync(DriverPackage pkg, ProvisionResult result, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (pkg.Sources.All(s => s.Type == "page")) return null;
        var status = new PackageStatus { Package = pkg, Installed = _updater.ReadInstalled(pkg) };
        await _updater.CheckOneAsync(status, ct).ConfigureAwait(false);
        if (status.State == UpdateState.Error)
        {
            result.Log.Add(Loc.T($"{pkg.Name} : source injoignable ({status.Error}).", $"{pkg.Name}: source unreachable ({status.Error})."));
            return null;
        }
        if (status.State == UpdateState.UpToDate)
            return BestUniversal(_loadLibrary(), pkg.Brand);
        if (!status.CanDownload) return null;

        result.Log.Add(Loc.T($"Téléchargement de {pkg.Name} {status.Latest?.Version}…", $"Downloading {pkg.Name} {status.Latest?.Version}…"));
        var up = await _updater.UpdateAsync(status, progress, ct).ConfigureAwait(false);
        result.Log.Add((up.Success ? "✓ " : "✗ ") + up.Message);
        return up.Success ? BestUniversal(_loadLibrary(), pkg.Brand) : null;
    }
}
