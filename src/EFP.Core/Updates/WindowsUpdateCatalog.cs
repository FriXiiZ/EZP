using EFP.Core.Drivers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EFP.Core.Updates;

/// <summary>Une ligne de résultat du catalogue Microsoft Update.</summary>
public sealed record CatalogEntry(string Id, string Title, string Products, string Classification, DateTime? LastUpdated, long Size);

/// <summary>
/// Dernier recours avant le pilote générique : le pilote exact du modèle publié sur Windows Update
/// (catalog.update.microsoft.com). Pas d'API officielle : on lit la page de recherche puis la fenêtre
/// de téléchargement, comme le module PowerShell MSCatalog. Téléchargement HTTPS depuis les serveurs
/// Microsoft uniquement, empreinte SHA-1 vérifiée (elle figure dans le nom du fichier).
/// </summary>
public sealed class WindowsUpdateCatalog
{
    public const string BaseUrl = "https://catalog.update.microsoft.com";
    public const string FolderPrefix = "wu-";

    private readonly HttpClient _http;
    public WindowsUpdateCatalog(HttpClient http) => _http = http;

    public async Task<List<CatalogEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync($"{BaseUrl}/Search.aspx?q={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        return ParseSearch(html);
    }

    public static List<CatalogEntry> ParseSearch(string html)
    {
        var list = new List<CatalogEntry>();
        foreach (Match row in Regex.Matches(html, @"<tr id=""([0-9a-fA-F-]{36})_R\d+"">(.*?)</tr>", RegexOptions.Singleline))
        {
            var cells = Regex.Matches(row.Groups[2].Value, @"_C(\d)_R\d+"">(.*?)</td>", RegexOptions.Singleline)
                .ToDictionary(m => int.Parse(m.Groups[1].Value), m => Clean(m.Groups[2].Value));
            string C(int i) => cells.TryGetValue(i, out var v) ? v : "";
            DateTime? date = DateTime.TryParse(C(4), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d) ? d : null;
            var sizeMatch = Regex.Match(row.Groups[2].Value, @"_originalSize"">(\d+)<");
            list.Add(new CatalogEntry(row.Groups[1].Value, C(1), C(2), C(3), date,
                sizeMatch.Success ? long.Parse(sizeMatch.Groups[1].Value) : 0));
        }
        return list;
    }

    /// <summary>Le meilleur paquet d'imprimante : Windows 11, sinon 10 (jamais XP/Vista), le plus récent.</summary>
    public static CatalogEntry? PickBest(IEnumerable<CatalogEntry> entries) => entries
        .Where(e => e.Classification.Contains("Printers", StringComparison.OrdinalIgnoreCase))
        .Select(e => (e, rank: e.Products.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ? 2
                              : e.Products.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        .Where(x => x.rank > 0)
        .OrderByDescending(x => x.e.LastUpdated ?? DateTime.MinValue)
        .ThenByDescending(x => x.rank)
        .Select(x => x.e)
        .FirstOrDefault();

    public async Task<List<string>> GetDownloadUrlsAsync(string updateId, CancellationToken ct = default)
    {
        var payload = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("updateIDs", payload) });
        using var resp = await _http.PostAsync($"{BaseUrl}/DownloadDialog.aspx", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return ParseDownloadUrls(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static List<string> ParseDownloadUrls(string html) =>
        Regex.Matches(html, @"https?://[^'""\s]+\.cab", RegexOptions.IgnoreCase)
            .Select(m => Regex.Replace(m.Value, "^http://", "https://"))
            .Where(IsMicrosoftDownload)
            .Distinct()
            .ToList();

    /// <summary>On ne télécharge que depuis les serveurs de Windows Update.</summary>
    public static bool IsMicrosoftDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".update.microsoft.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>SHA-1 attendu : les fichiers du catalogue se terminent par « _&lt;sha1&gt;.cab ».</summary>
    public static string? ExpectedSha1(string url)
    {
        var m = Regex.Match(url, @"_([0-9a-fA-F]{40})\.cab$");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Cherche « Marque Modèle », télécharge le paquet et l'extrait dans Drivers\&lt;Marque&gt;\wu-&lt;modèle&gt;.
    /// Renvoie le dossier extrait, ou null si rien de convenable n'a été trouvé.
    /// </summary>
    public async Task<string?> FetchAsync(string driversRoot, string brand, string model, DriverUpdater extractor,
        IProgress<DownloadProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        var query = $"{brand} {model}".Trim();
        var entries = await SearchAsync(query, ct).ConfigureAwait(false);
        var best = PickBest(entries);
        if (best is null) { log?.Invoke($"Windows Update : aucun pilote d'imprimante pour « {query} »."); return null; }
        log?.Invoke($"Windows Update : « {best.Title} » ({best.Products.Split(',')[0]}, {best.LastUpdated:dd.MM.yyyy}, {best.Size / 1048576.0:F1} Mo)");

        var url = (await GetDownloadUrlsAsync(best.Id, ct).ConfigureAwait(false)).FirstOrDefault();
        if (url is null) { log?.Invoke("Windows Update : lien de téléchargement introuvable."); return null; }

        var safeModel = Regex.Replace(model, @"[^A-Za-z0-9._-]+", "_").Trim('_');
        var dest = Path.Combine(driversRoot, brand, FolderPrefix + safeModel);
        var tmp = Path.Combine(driversRoot, "_downloads");
        Directory.CreateDirectory(tmp);
        var cab = Path.Combine(tmp, Path.GetFileName(new Uri(url).LocalPath));

        using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? best.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(cab);
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                progress?.Report(new DownloadProgress("Windows Update", done, total, "Téléchargement"));
            }
        }

        var expected = ExpectedSha1(url);
        if (expected is not null)
        {
            string actual;
            await using (var fs = File.OpenRead(cab)) actual = Convert.ToHexString(await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != expected) { File.Delete(cab); throw new InvalidDataException("Empreinte du paquet Windows Update incorrecte : téléchargement annulé."); }
        }

        progress?.Report(new DownloadProgress("Windows Update", 0, null, "Extraction"));
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        var ok = await extractor.ExtractToAsync(cab, dest, ct).ConfigureAwait(false);
        try { File.Delete(cab); } catch (IOException) { }
        if (!ok || !Directory.EnumerateFiles(dest, "*.inf", Infs.All).Any())
        {
            log?.Invoke("Windows Update : paquet téléchargé mais impossible à extraire.");
            return null;
        }
        log?.Invoke($"Windows Update : pilote rangé dans {Path.GetRelativePath(driversRoot, dest)}");
        return dest;
    }

    private static string Clean(string cell) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(cell, "<[^>]+>", " ")), @"\s+", " ").Trim();
}
