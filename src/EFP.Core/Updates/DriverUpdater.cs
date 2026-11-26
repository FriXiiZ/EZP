using EFP.Core.Drivers;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EFP.Core.Updates;

public enum UpdateState
{
    /// <summary>Pas encore vérifié.</summary>
    Unknown,
    /// <summary>Absent de la clé, téléchargeable.</summary>
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    /// <summary>Pas de lien direct : page du constructeur à ouvrir.</summary>
    ManualOnly,
    /// <summary>La vérification a échoué (pas d'internet, source en panne…).</summary>
    Error,
}

/// <summary>Ce qu'EFP sait d'un paquet installé sur la clé (fichier .efp-package.json).</summary>
public sealed class InstalledPackage
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string? File { get; set; }
    public DateTime InstalledAt { get; set; }
    public int InfCount { get; set; }
}

public sealed class PackageStatus
{
    public required DriverPackage Package { get; init; }
    public InstalledPackage? Installed { get; set; }
    public ReleaseInfo? Latest { get; set; }
    public UpdateState State { get; set; } = UpdateState.Unknown;
    public string? Error { get; set; }

    public bool CanDownload => Latest?.IsDownloadable == true && State is UpdateState.NotInstalled or UpdateState.UpdateAvailable;
}

public sealed record DownloadProgress(string PackageId, long Received, long? Total, string Stage);

public sealed class UpdateResult
{
    public required PackageStatus Status { get; init; }
    public bool Success { get; set; }
    /// <summary>Téléchargé mais pas décompressable automatiquement (fichier laissé dans _a_extraire).</summary>
    public bool NeedsManualExtraction { get; set; }
    public string Message { get; set; } = "";
    public string? Folder { get; set; }
}

/// <summary>
/// Tient le dossier Drivers à jour : compare la clé avec les dernières versions en ligne,
/// télécharge, vérifie la somme de contrôle, décompresse et remplace l'ancienne version.
/// </summary>
public sealed class DriverUpdater
{
    public const string StateFile = ".efp-package.json";
    public const string DownloadsFolder = "_downloads";
    public const string ManualFolder = "_a_extraire";

    private readonly HttpClient _http;
    private readonly FeedResolver _resolver;
    public string DriversRoot { get; }
    /// <summary>Chemin de 7za.exe (facultatif) pour les archives .exe / .7z / .cab.</summary>
    public string? SevenZipPath { get; init; }
    /// <summary>Taille maximale acceptée pour un téléchargement.</summary>
    public long MaxDownloadBytes { get; init; } = 1024L * 1024 * 1024;

    public DriverUpdater(string driversRoot, HttpClient http, FeedResolver? resolver = null)
    {
        DriversRoot = driversRoot;
        _http = http;
        _resolver = resolver ?? new FeedResolver(http);
    }

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = TimeSpan.FromMinutes(20),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EZP/2.1 (+driver-updater)");
        return http;
    }

    public string PackageFolder(DriverPackage p) => Path.Combine(DriversRoot, SafeName(p.Brand), SafeName(p.Id));

    public InstalledPackage? ReadInstalled(DriverPackage p)
    {
        var f = Path.Combine(PackageFolder(p), StateFile);
        try { return File.Exists(f) ? JsonSerializer.Deserialize<InstalledPackage>(File.ReadAllText(f)) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Vérifie tous les paquets en parallèle (4 à la fois).</summary>
    public async Task<List<PackageStatus>> CheckAsync(DriverFeed feed, CancellationToken ct = default)
    {
        var statuses = feed.Packages.Select(p => new PackageStatus { Package = p, Installed = ReadInstalled(p) }).ToList();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(statuses.Select(async s =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await CheckOneAsync(s, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return statuses;
    }

    public async Task CheckOneAsync(PackageStatus s, CancellationToken ct = default)
    {
        try
        {
            s.Latest = await _resolver.ResolveAsync(s.Package, ct).ConfigureAwait(false);
            s.Error = null;
            s.State = Compare(s.Installed, s.Latest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            s.State = UpdateState.Error;
            s.Error = ex.Message;
        }
    }

    public static UpdateState Compare(InstalledPackage? installed, ReleaseInfo latest)
    {
        if (!latest.IsDownloadable) return UpdateState.ManualOnly;
        if (installed is null) return UpdateState.NotInstalled;
        if (latest.SourceType == "direct")
            return string.Equals(installed.Version, latest.Version, StringComparison.OrdinalIgnoreCase) ? UpdateState.UpToDate : UpdateState.UpdateAvailable;
        return VersionComparer.Compare(latest.Version, installed.Version) > 0 ? UpdateState.UpdateAvailable : UpdateState.UpToDate;
    }

    /// <summary>Télécharge et installe la dernière version d'un paquet dans le dossier Drivers.</summary>
    public async Task<UpdateResult> UpdateAsync(PackageStatus s, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new UpdateResult { Status = s };
        var latest = s.Latest;
        if (latest?.DownloadUrl is null)
        {
            result.Message = "Pas de téléchargement direct : ouvre la page du constructeur.";
            return result;
        }
        if (!latest.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            result.Message = "Lien refusé : seuls les téléchargements HTTPS sont acceptés.";
            return result;
        }

        var downloads = Path.Combine(DriversRoot, DownloadsFolder);
        Directory.CreateDirectory(downloads);
        var file = Path.Combine(downloads, SafeName(latest.FileName));
        try
        {
            // 1. Téléchargement (.part puis renommage)
            await DownloadAsync(latest.DownloadUrl, file, s.Package.Id, progress, ct).ConfigureAwait(false);

            // 2. Somme de contrôle
            progress?.Report(new DownloadProgress(s.Package.Id, 0, null, "Vérification"));
            if (!VerifyHash(file, latest.Sha256, latest.Sha512, out var actual))
            {
                File.Delete(file);
                result.Message = $"Somme de contrôle incorrecte (fichier corrompu ou modifié) : {actual[..12]}…";
                return result;
            }

            // 3. Décompression dans un dossier temporaire
            progress?.Report(new DownloadProgress(s.Package.Id, 0, null, "Décompression"));
            var target = PackageFolder(s.Package);
            var staging = target + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            var extracted = await ExtractAsync(file, staging, depth: 0, ct).ConfigureAwait(false);
            var infCount = Directory.Exists(staging) ? Directory.EnumerateFiles(staging, "*.inf", Infs.All).Count() : 0;

            if (!extracted || infCount == 0)
            {
                // Impossible à décompresser automatiquement : on garde le fichier pour l'utilisateur
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                var manual = Path.Combine(DriversRoot, ManualFolder);
                Directory.CreateDirectory(manual);
                var dest = Path.Combine(manual, Path.GetFileName(file));
                File.Move(file, dest, overwrite: true);
                result.NeedsManualExtraction = true;
                result.Folder = manual;
                result.Message = SevenZipPath is null || !File.Exists(SevenZipPath)
                    ? "Téléchargé, mais archive .exe : ajoute 7za.exe dans Tools ou décompresse-la à la main dans Drivers."
                    : "Téléchargé, mais aucun fichier .inf trouvé dans l'archive : à décompresser à la main.";
                return result;
            }

            // 4. Remplacement de l'ancienne version
            var record = new InstalledPackage
            {
                Id = s.Package.Id, Version = latest.Version, SourceType = latest.SourceType,
                File = Path.GetFileName(file), InstalledAt = DateTime.Now, InfCount = infCount,
            };
            await File.WriteAllTextAsync(Path.Combine(staging, StateFile), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            File.Delete(file);

            s.Installed = record;
            s.State = UpdateState.UpToDate;
            result.Success = true;
            result.Folder = target;
            result.Message = $"Version {latest.Version} installée ({infCount} fichier(s) .inf).";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(file + ".part");
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(file + ".part");
            result.Message = ex.Message;
            return result;
        }
    }

    private async Task DownloadAsync(string url, string file, string id, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        if (total > MaxDownloadBytes) throw new InvalidDataException("Fichier trop volumineux.");
        var part = file + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[81920];
            long received = 0;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                if (received > MaxDownloadBytes) throw new InvalidDataException("Fichier trop volumineux.");
                if ((DateTime.Now - lastReport).TotalMilliseconds > 200)
                {
                    progress?.Report(new DownloadProgress(id, received, total, "Téléchargement"));
                    lastReport = DateTime.Now;
                }
            }
            progress?.Report(new DownloadProgress(id, received, total, "Téléchargement"));
        }
        File.Move(part, file, overwrite: true);
    }

    public static bool VerifyHash(string file, string? sha256, string? sha512, out string actual)
    {
        actual = "";
        if (sha256 is null && sha512 is null)
        {
            actual = "non vérifiée";
            return true; // lien direct officiel sans somme publiée
        }
        using var fs = File.OpenRead(file);
        if (sha256 is not null)
        {
            actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            return actual == sha256.ToLowerInvariant();
        }
        actual = Convert.ToHexString(SHA512.HashData(fs)).ToLowerInvariant();
        return actual == sha512!.ToLowerInvariant();
    }

    /// <summary>Décompresse une archive de pilote (zip natif ; exe/7z/cab via 7za ; msi via msiexec) dans <paramref name="dest"/>.</summary>
    public Task<bool> ExtractToAsync(string file, string dest, CancellationToken ct = default) => ExtractAsync(file, dest, 0, ct);

    private async Task<bool> ExtractAsync(string file, string dest, int depth, CancellationToken ct)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var ok = false;
        if (ext == ".zip" || IsZip(file)) { SafeExtractZip(file, dest); ok = true; }
        else if (ext == ".inf") { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true); ok = true; }
        else if (ext == ".cab" || IsCab(file))
        {
            // Paquets Windows Update : expand.exe (intégré à Windows), cabextract ailleurs (tests)
            if (OperatingSystem.IsWindows())
                ok = await RunAsync(Path.Combine(Environment.SystemDirectory, "expand.exe"), new[] { file, "-F:*", dest }, ct).ConfigureAwait(false);
            else
                ok = await RunAsync("cabextract", new[] { "-q", "-d", dest, file }, ct).ConfigureAwait(false);
            ok = ok || Directory.EnumerateFiles(dest, "*.inf", Infs.All).Any();
        }
        else if (ext == ".msi" && OperatingSystem.IsWindows()) ok = await RunAsync("msiexec.exe", new[] { "/a", file, "/qn", $"TARGETDIR={dest}" }, ct).ConfigureAwait(false);
        else if (SevenZipPath is not null && File.Exists(SevenZipPath))
            ok = await RunAsync(SevenZipPath, new[] { "x", "-y", $"-o{dest}", file }, ct).ConfigureAwait(false);

        // Archive dans l'archive (exe qui contient un zip ou un msi…) : un niveau de plus
        if (ok && depth == 0 && !Directory.EnumerateFiles(dest, "*.inf", Infs.All).Any())
        {
            foreach (var inner in Directory.EnumerateFiles(dest, "*.*", SearchOption.AllDirectories)
                         .Where(f => Regex.IsMatch(f, @"\.(zip|msi|exe|7z|cab)$", RegexOptions.IgnoreCase)).ToList())
            {
                var sub = Path.Combine(dest, "x_" + Path.GetFileNameWithoutExtension(inner));
                Directory.CreateDirectory(sub);
                await ExtractAsync(inner, sub, depth + 1, ct).ConfigureAwait(false);
            }
        }
        return ok;
    }

    private static bool IsCab(string file)
    {
        Span<byte> sig = stackalloc byte[4];
        using var fs = File.OpenRead(file);
        return fs.Read(sig) == 4 && sig[0] == (byte)'M' && sig[1] == (byte)'S' && sig[2] == (byte)'C' && sig[3] == (byte)'F';
    }

    private static bool IsZip(string file)
    {
        Span<byte> sig = stackalloc byte[4];
        using var fs = File.OpenRead(file);
        return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
    }

    /// <summary>Décompression protégée contre les chemins du type "..\..\Windows\…" (zip slip).</summary>
    public static void SafeExtractZip(string zipFile, string dest)
    {
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipFile);
        foreach (var e in zip.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('\\', '/')));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive refusée : chemin dangereux « {e.FullName} »");
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            e.ExtractToFile(path, overwrite: true);
        }
    }

    private static async Task<bool> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        _ = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return p.ExitCode == 0;
    }

    public static string SafeName(string s)
    {
        var clean = Regex.Replace(s, @"[^\p{L}\p{N}\-_. ]", "_").Trim(' ', '.');
        return clean.Length == 0 ? "pilote" : clean;
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
}

/// <summary>Comparaison de versions "naturelle" : 8.10.0 &gt; 8.9.1, V3.50 &gt; V3.4, 2024.05 &gt; 2023.12.</summary>
public static class VersionComparer
{
    public static int Compare(string? a, string? b)
    {
        var pa = Parts(a);
        var pb = Parts(b);
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var x = i < pa.Count ? pa[i] : 0;
            var y = i < pb.Count ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static List<long> Parts(string? v) =>
        Regex.Matches(v ?? "", @"\d+").Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToList();
}
