using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFP.Core.Updates;

/// <summary>
/// Où trouver la dernière version d'un pilote :
///  - "chocolatey" : paquet communautaire Chocolatey (version + lien officiel + SHA256, maintenus par la communauté) ;
///  - "direct"     : lien officiel qui pointe toujours vers la dernière version (détection par ETag / date / taille) ;
///  - "page"       : page du constructeur à ouvrir (licence à accepter, pas de lien direct stable).
/// </summary>
public sealed class DriverSource
{
    [JsonPropertyName("type")] public string Type { get; set; } = "page";
    [JsonPropertyName("package")] public string? Package { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    /// <summary>Pour un paquet Chocolatey qui contient plusieurs pilotes : motif du nom de variable d'URL à prendre (ex. "PCL").</summary>
    [JsonPropertyName("urlVariable")] public string? UrlVariable { get; set; }
}

public sealed class DriverPackage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("brand")] public string Brand { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sources")] public List<DriverSource> Sources { get; set; } = new();
    /// <summary>Page officielle du constructeur (toujours proposée en secours).</summary>
    [JsonPropertyName("page")] public string? Page { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>Paquet PCL de secours : téléchargé seulement si le PostScript de la marque est introuvable.</summary>
    [JsonPropertyName("fallback")] public bool Fallback { get; set; }
}

public sealed class DriverFeed
{
    [JsonPropertyName("packages")] public List<DriverPackage> Packages { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DriverFeed FromJson(string json) => JsonSerializer.Deserialize<DriverFeed>(json, Opts) ?? new DriverFeed();
    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    public static DriverFeed Load(string path) => File.Exists(path) ? FromJson(File.ReadAllText(path)) : Default();

    /// <summary>Liste intégrée : un pilote universel PostScript par marque, plus un PCL de secours quand il existe.</summary>
    public static DriverFeed Default() => new()
    {
        Packages =
        {
            new()
            {
                Id = "hp-upd-ps", Brand = "HP", Name = "HP Universal Print Driver PS",
                Sources = { new() { Type = "chocolatey", Package = "hp-universal-print-driver-ps" } },
                Page = "https://support.hp.com/drivers/hp-universal-print-driver-series-for-windows/503548",
            },
            new()
            {
                Id = "hp-upd-pcl6", Brand = "HP", Name = "HP Universal Print Driver PCL6 (secours)", Fallback = true,
                Sources = { new() { Type = "chocolatey", Package = "hp-universal-print-driver-pcl" } },
                Page = "https://support.hp.com/drivers/hp-universal-print-driver-series-for-windows/503548",
            },
            new()
            {
                Id = "kyocera-kx", Brand = "Kyocera", Name = "Kyocera KX Driver (universel KPDL / PS)",
                Sources = { new() { Type = "direct", Url = "https://downloads.kyoceradocumentsolutions.com.au/Drivers/KX_Driver.zip" } },
                Page = "https://www.kyoceradocumentsolutions.eu/en/support/downloads.html",
                Notes = "Lien officiel « toujours la dernière version » (site Kyocera Australie).",
            },
            new()
            {
                Id = "konica-upd-ps", Brand = "Konica Minolta", Name = "Konica Minolta Universal Printer Driver PS",
                Sources = { new() { Type = "chocolatey", Package = "kmupd", UrlVariable = "PS" } },
                Page = "https://www.konicaminolta.eu/eu-en/support/download-centre",
                Notes = "Sert aussi pour les Develop / ineo (même matériel).",
            },
            new()
            {
                Id = "konica-upd-pcl", Brand = "Konica Minolta", Name = "Konica Minolta Universal Printer Driver PCL (secours)", Fallback = true,
                Sources = { new() { Type = "chocolatey", Package = "kmupd", UrlVariable = "PCL" } },
                Page = "https://www.konicaminolta.eu/eu-en/support/download-centre",
            },
            new()
            {
                Id = "lexmark-upd", Brand = "Lexmark", Name = "Lexmark Universal Print Driver (PS3)",
                Sources = { new() { Type = "chocolatey", Package = "lexmark-universal-driver-pcl-xl" } },
                Page = "https://www.lexmark.com/en_us/support/universal-print-driver.html",
                Notes = "Le paquet Lexmark contient PCL et PostScript : EFP choisit le PostScript. Archive .exe : 7-Zip (Tools\\7za.exe) nécessaire.",
            },
            new()
            {
                Id = "canon-gplus-ps3", Brand = "Canon", Name = "Canon Generic Plus PS3",
                Sources = { new() { Type = "page" } },
                Page = "https://www.canon.ch/fr/support/business-product-support/",
                Notes = "Licence à accepter sur le site Canon : EFP ouvre la page.",
            },
            new()
            {
                Id = "ricoh-ps-universal", Brand = "Ricoh", Name = "Ricoh PS Driver for Universal Print",
                Sources = { new() { Type = "page" } },
                Page = "https://support.ricoh.com/bb/html/dr_ut_e/sav/model/mpc60/mpc60en.htm",
            },
            new()
            {
                Id = "xerox-gpd-ps", Brand = "Xerox", Name = "Xerox Global Print Driver PS",
                Sources = { new() { Type = "page" } },
                Page = "https://support.xerox.com/en-us/product/global-printer-driver/downloads",
            },
            new()
            {
                Id = "brother-brscript", Brand = "Brother", Name = "Brother Universal Printer Driver (BR-Script / PS)",
                Sources = { new() { Type = "page" } },
                Page = "https://support.brother.com/g/b/productsearch.aspx?c=ch&lang=fr",
            },
        },
    };
}

/// <summary>Réglages de mise à jour (fichier efp-settings.json à côté de l'exe).</summary>
public sealed class UpdateSettings
{
    /// <summary>Vérifier les nouvelles versions au démarrage d'EFP.</summary>
    [JsonPropertyName("checkAtStartup")] public bool CheckAtStartup { get; set; } = true;
    /// <summary>Télécharger tout seul les nouvelles versions des pilotes déjà présents sur la clé.</summary>
    [JsonPropertyName("autoUpdateInstalled")] public bool AutoUpdateInstalled { get; set; } = true;
    /// <summary>Scanner le réseau local dès l'ouverture d'EFP.</summary>
    [JsonPropertyName("autoScanAtStartup")] public bool AutoScanAtStartup { get; set; } = true;
    /// <summary>Liste de pilotes distante facultative (remplace la liste intégrée si elle répond).</summary>
    [JsonPropertyName("feedUrl")] public string? FeedUrl { get; set; }
    /// <summary>Thème de l'interface : "light", "dark" ou null (celui de Windows).</summary>
    [JsonPropertyName("theme")] public string? Theme { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static UpdateSettings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), Opts) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));
}
