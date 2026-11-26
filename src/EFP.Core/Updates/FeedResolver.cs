using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFP.Core.Updates;

/// <summary>Dernière version trouvée en ligne pour un pilote.</summary>
public sealed record ReleaseInfo(
    string Version,
    string? DownloadUrl,
    string? Sha256,
    string? Sha512,
    string? PageUrl,
    string SourceType)
{
    public bool IsDownloadable => DownloadUrl is not null;
    public string FileName => DownloadUrl is null ? "" :
        Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var u) && Path.GetFileName(u.LocalPath) is { Length: > 0 } n ? n : "pilote.bin";
}

/// <summary>Interroge les sources d'un pilote (Chocolatey, lien direct, page) pour trouver la dernière version.</summary>
public sealed class FeedResolver
{
    public const string ChocolateyApi = "https://community.chocolatey.org/api/v2/package/";
    private readonly HttpClient _http;
    public string ChocolateyBase { get; init; } = ChocolateyApi;

    public FeedResolver(HttpClient http) => _http = http;

    public async Task<ReleaseInfo> ResolveAsync(DriverPackage pkg, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var src in pkg.Sources)
        {
            try
            {
                var r = src.Type.ToLowerInvariant() switch
                {
                    "chocolatey" => await FromChocolateyAsync(src, ct).ConfigureAwait(false),
                    "direct" => await FromDirectAsync(src, ct).ConfigureAwait(false),
                    _ => null,
                };
                if (r is not null) return r with { PageUrl = pkg.Page };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; } // source suivante
        }
        // Une source en ligne a échoué et aucune n'a répondu : c'est une panne (pas d'internet…), pas un pilote "manuel"
        if (last is not null) throw last;
        return new ReleaseInfo("", null, null, null, pkg.Page, "page");
    }

    // ------------------------------------------------------------ Chocolatey

    private async Task<ReleaseInfo?> FromChocolateyAsync(DriverSource src, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(src.Package)) return null;
        using var resp = await _http.GetAsync(ChocolateyBase + Uri.EscapeDataString(src.Package), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ParseNupkg(bytes, src.UrlVariable);
    }

    /// <summary>Lit un .nupkg Chocolatey : version dans le .nuspec, lien et somme de contrôle dans tools/chocolateyinstall.ps1.</summary>
    public static ReleaseInfo ParseNupkg(byte[] nupkg, string? urlVariable = null)
    {
        using var zip = new ZipArchive(new MemoryStream(nupkg), ZipArchiveMode.Read);
        var nuspec = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException("nuspec absent du paquet");
        string version;
        using (var s = nuspec.Open())
        {
            var doc = XDocument.Load(s);
            version = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim()
                      ?? throw new InvalidDataException("version absente du nuspec");
        }
        var script = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith("tools/chocolateyinstall.ps1", StringComparison.OrdinalIgnoreCase));
        if (script is null) return new ReleaseInfo(version, null, null, null, null, "chocolatey");
        string text;
        using (var r = new StreamReader(script.Open())) text = r.ReadToEnd();
        var (url, sha256, sha512) = ParseInstallScript(text, version, urlVariable);
        return new ReleaseInfo(version, url, sha256, sha512, null, "chocolatey");
    }

    private static readonly Regex Assign = new(
        @"^\s*\$?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<q>['""])(?<val>[^'""\r\n]*)\k<q>",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Extrait l'URL (de préférence 64 bits) et la somme de contrôle d'un script chocolateyinstall.ps1.
    /// Gère les variables simples ($url = '...') et les tables (url64 = "...${version}...").
    /// </summary>
    public static (string? Url, string? Sha256, string? Sha512) ParseInstallScript(string script, string version, string? urlVariable = null)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["version"] = version,
            ["env:ChocolateyPackageVersion"] = version,
        };
        var assignments = new List<(string Name, string Value)>();
        foreach (Match m in Assign.Matches(script))
        {
            var name = m.Groups["name"].Value;
            var value = Expand(m.Groups["val"].Value, vars, m.Groups["q"].Value == "\"");
            assignments.Add((name, value));
            vars.TryAdd(name, value);
        }

        bool IsUrl(string v) => v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var urls = assignments.Where(a => IsUrl(a.Value)).ToList();
        if (urlVariable is not null)
        {
            var filtered = urls.Where(a => Regex.IsMatch(a.Name, $"^{Regex.Escape(urlVariable)}(64)?$", RegexOptions.IgnoreCase)).ToList();
            if (filtered.Count > 0) urls = filtered;
        }
        var url = urls.FirstOrDefault(a => a.Name.EndsWith("64", StringComparison.Ordinal) || a.Value.Contains("x64", StringComparison.OrdinalIgnoreCase)).Value
                  ?? urls.FirstOrDefault(a => a.Name.Equals("url", StringComparison.OrdinalIgnoreCase)).Value
                  ?? urls.FirstOrDefault().Value;

        // Somme de contrôle associée : même suffixe que la variable d'URL si possible (PCL -> PCL_checksum…)
        var urlName = urls.FirstOrDefault(a => a.Value == url).Name ?? "url";
        var hashes = assignments.Where(a => Regex.IsMatch(a.Value, "^[0-9A-Fa-f]{64}$|^[0-9A-Fa-f]{128}$")).ToList();
        var hash = hashes.FirstOrDefault(h => h.Name.StartsWith(urlName, StringComparison.OrdinalIgnoreCase) && h.Name.Length > urlName.Length).Value
                   ?? hashes.FirstOrDefault(h => h.Name.EndsWith("64", StringComparison.Ordinal)).Value
                   ?? hashes.FirstOrDefault().Value;
        string? sha256 = hash is { Length: 64 } ? hash.ToLowerInvariant() : null;
        string? sha512 = hash is { Length: 128 } ? hash.ToLowerInvariant() : null;
        return (url, sha256, sha512);
    }

    private static string Expand(string value, Dictionary<string, string> vars, bool interpolate)
    {
        if (!interpolate) return value;
        return Regex.Replace(value, @"\$\{(?<n>[^}]+)\}|\$(?<n>env:[A-Za-z_]+|[A-Za-z_][A-Za-z0-9_]*)",
            m => vars.TryGetValue(m.Groups["n"].Value, out var v) ? v : m.Value);
    }

    // ------------------------------------------------------------ Lien direct

    private async Task<ReleaseInfo?> FromDirectAsync(DriverSource src, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(src.Url)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Head, src.Url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return new ReleaseInfo(DirectVersion(resp.Headers.ETag, resp.Content.Headers.LastModified, resp.Content.Headers.ContentLength),
            src.Url, null, null, null, "direct");
    }

    /// <summary>Pas de numéro de version pour un lien direct : on fabrique une "empreinte" à partir des en-têtes HTTP.</summary>
    public static string DirectVersion(EntityTagHeaderValue? etag, DateTimeOffset? lastModified, long? length)
    {
        if (lastModified is { } lm) return lm.UtcDateTime.ToString("yyyy.MM.dd") + (length is { } l ? $"-{l}" : "");
        if (etag is not null) return "etag-" + etag.Tag.Trim('"');
        return length is { } len ? $"taille-{len}" : "inconnue";
    }
}
