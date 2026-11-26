using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EFP.Core.Drivers;

/// <summary>Un pilote disponible : soit dans un INF de la bibliothèque, soit intégré à Windows.</summary>
public sealed record LibraryDriver(string Name, string Brand, string? InfPath, string? Provider, string? Version)
{
    public bool IsInbox => InfPath is null;
    /// <summary>Pilote ajouté à la main par le technicien (dossier « import-… ») : prioritaire pour sa marque.</summary>
    /// <summary>Pilote exact du modèle récupéré sur Windows Update (dossier « wu-… »).</summary>
    public bool IsWindowsUpdate => InfPath is not null &&
        InfPath.Replace('\\', '/').Split('/').Any(seg => seg.StartsWith(EFP.Core.Updates.WindowsUpdateCatalog.FolderPrefix, StringComparison.OrdinalIgnoreCase));
    public bool IsImported => InfPath is not null &&
        InfPath.Replace('\\', '/').Split('/').Any(seg => seg.StartsWith(DriverImporter.FolderPrefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Contenu de drivers.json : quels pilotes universels préférer par marque.</summary>
public sealed class DriverCatalog
{
    [JsonPropertyName("universal")]
    public List<UniversalRule> Universal { get; set; } = new();

    /// <summary>
    /// Faux (par défaut) : toujours le pilote universel de la marque, un seul pilote par marque.
    /// Vrai : le pilote du modèle exact passe avant l'universel s'il est dans la bibliothèque.
    /// </summary>
    [JsonPropertyName("preferExactModel")]
    public bool PreferExactModel { get; set; }

    /// <summary>Pilotes génériques Windows à tenter en dernier recours, dans l'ordre.</summary>
    [JsonPropertyName("inboxFallback")]
    public List<string> InboxFallback { get; set; } = new();

    public sealed class UniversalRule
    {
        [JsonPropertyName("brand")] public string Brand { get; set; } = "";
        /// <summary>Expressions régulières testées sur les noms de pilotes des INF, par ordre de préférence.</summary>
        [JsonPropertyName("patterns")] public List<string> Patterns { get; set; } = new();
        [JsonPropertyName("download")] public string? Download { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static DriverCatalog Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<DriverCatalog>(File.ReadAllText(path), JsonOpts) ?? Default()
            : Default();

    public static DriverCatalog FromJson(string json) => JsonSerializer.Deserialize<DriverCatalog>(json, JsonOpts) ?? Default();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>Par défaut : un seul pilote universel PostScript par marque (PCL seulement en secours).</summary>
    public static DriverCatalog Default() => new()
    {
        PreferExactModel = false,
        Universal =
        {
            Rule("HP", "https://support.hp.com (HP Universal Print Driver PS)",
                @"^HP Universal Printing PS", @"^HP Smart Universal Printing", @"^HP Universal Printing PCL 6"),
            Rule("Kyocera", "https://www.kyoceradocumentsolutions.eu (KX Driver)",
                @"Kyocera.*Universal.*(KPDL|PS)", @"Kyocera.*Universal"),
            Rule("Canon", "https://www.canon.ch (Generic Plus PS3)",
                @"Canon Generic Plus PS3", @"Canon Generic.*PS", @"Canon Generic Plus UFR II"),
            Rule("Ricoh", "https://www.ricoh.ch (PS Driver for Universal Print)",
                @"RICOH PS Universal", @"RICOH.*Universal.*PS", @"RICOH.*Universal"),
            Rule("Konica Minolta", "https://www.konicaminolta.ch (Universal Printer Driver PS)",
                @"KONICA MINOLTA Universal V4 PS", @"KONICA MINOLTA Universal PS", @"KONICA MINOLTA Universal"),
            Rule("Xerox", "https://www.xerox.ch (Global Print Driver PS)",
                @"Xerox Global Print Driver PS", @"Xerox Global Print Driver"),
            Rule("Brother", "https://support.brother.com (Universal Printer Driver for BR-Script)",
                @"Brother.*(BR-Script|PS)", @"Brother.*Universal"),
            Rule("Lexmark", "https://www.lexmark.com (Universal Print Driver, PostScript 3 Emulation)",
                @"Lexmark Universal v\d+ (PS3|PostScript)", @"Lexmark Universal.*(PS|PostScript)", @"Lexmark Universal"),
            Rule("Sharp", "https://www.sharp.ch (UD3 PS)",
                @"SHARP UD\d? PS", @"SHARP.*Universal"),
            Rule("Toshiba", "https://www.toshibatec.eu (Universal PS3)",
                @"TOSHIBA Universal PS3", @"TOSHIBA.*Universal"),
            Rule("Epson", "https://epson.ch (Universal Print Driver)",
                @"EPSON.*Universal.*PS", @"EPSON.*Universal"),
            Rule("OKI", "https://www.oki.com (Universal PS)",
                @"OKI.*Universal.*PS", @"OKI.*Universal"),
        },
        InboxFallback = { "Microsoft PS Class Driver", "Microsoft PCL6 Class Driver" },
    };

    private static UniversalRule Rule(string brand, string download, params string[] patterns) =>
        new() { Brand = brand, Download = download, Patterns = patterns.ToList() };
}

/// <summary>Bibliothèque de pilotes : tous les INF d'imprimante trouvés dans le dossier Drivers (sur la clé USB).</summary>
public sealed class DriverLibrary
{
    public string RootFolder { get; }
    public List<LibraryDriver> Drivers { get; } = new();
    public List<string> Errors { get; } = new();

    private DriverLibrary(string root) => RootFolder = root;

    public static DriverLibrary Empty(string root = "") => new(root);

    public static DriverLibrary Load(string rootFolder)
    {
        var lib = new DriverLibrary(rootFolder);
        if (!Directory.Exists(rootFolder)) return lib;
        foreach (var inf in Directory.EnumerateFiles(rootFolder, "*.inf", Infs.All))
        {
            // Ignore les téléchargements en cours / à décompresser et les mises à jour pas encore validées
            var rel = Path.GetRelativePath(rootFolder, inf).Replace('\\', '/');
            if (rel.StartsWith("_downloads/") || rel.StartsWith("_a_extraire/") || rel.Split('/').Any(seg => seg.EndsWith(".new")))
                continue;
            try
            {
                if (new FileInfo(inf).Length > 8 * 1024 * 1024) continue;
                lib.AddInf(InfParser.ParseFile(inf));
            }
            catch (Exception ex)
            {
                lib.Errors.Add($"{inf} : {ex.Message}");
            }
        }
        return lib;
    }

    public void AddInf(InfInfo inf)
    {
        if (!inf.IsPrinterClass) return;
        foreach (var m in inf.Models)
        {
            if (Regex.IsMatch(m.Name, @"\bfax\b", RegexOptions.IgnoreCase)) continue; // pilotes fax : jamais ce qu'on veut
            var brand = Brands.Detect(m.Name, m.Manufacturer, inf.Provider);
            Drivers.Add(new LibraryDriver(m.Name, brand, inf.Path, inf.Provider, inf.DriverVersion));
        }
    }

    public IEnumerable<string> BrandsAvailable => Drivers.Select(d => d.Brand).Distinct().OrderBy(b => b);
}
