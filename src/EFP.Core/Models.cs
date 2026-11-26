namespace EFP.Core;

/// <summary>Une imprimante trouvée sur le réseau (ou ajoutée à la main).</summary>
public sealed class DiscoveredPrinter
{
    public required string IpAddress { get; set; }
    public string? HostName { get; set; }
    public string? MacAddress { get; set; }

    /// <summary>Marque normalisée (HP, Kyocera, Canon…), "Inconnue" si non identifiée.</summary>
    public string Brand { get; set; } = Brands.Unknown;
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? SysDescr { get; set; }
    public string? SysName { get; set; }
    public string? DeviceId1284 { get; set; }

    public bool SnmpResponded { get; set; }
    public List<int> OpenPorts { get; } = new();

    public bool HasRawPort => OpenPorts.Contains(9100);
    public bool HasIpp => OpenPorts.Contains(631);
    public bool HasWeb => OpenPorts.Contains(80) || OpenPorts.Contains(443);

    public string DisplayName => string.IsNullOrWhiteSpace(Model)
        ? $"{Brand} ({IpAddress})"
        : Model!.StartsWith(Brand, StringComparison.OrdinalIgnoreCase) ? Model! : $"{Brand} {Model}";

    public override string ToString() => $"{IpAddress} {DisplayName}";
}

/// <summary>Pilote choisi pour une imprimante.</summary>
public sealed record DriverChoice(
    string DriverName,
    string? InfPath,
    DriverMatchKind Kind,
    string Reason)
{
    public bool IsInbox => InfPath is null;
}

public enum DriverMatchKind
{
    /// <summary>Sans pilote : IPP Everywhere / AirPrint (Linux, macOS).</summary>
    Driverless = 5,
    /// <summary>Pilote importé à la main par le technicien.</summary>
    Imported = 4,
    /// <summary>Le modèle exact figure dans un INF de la bibliothèque.</summary>
    ExactModel = 3,
    /// <summary>Pilote universel de la marque (déclaré dans drivers.json).</summary>
    BrandUniversal = 2,
    /// <summary>Pilote générique Microsoft intégré à Windows (dépannage).</summary>
    GenericInbox = 1,
    None = 0,
}

/// <summary>Options d'installation d'une imprimante.</summary>
public sealed class InstallOptions
{
    /// <summary>Modèle du nom affiché dans Windows. Jetons : {Brand} {Model} {Ip} {Location}.</summary>
    public string NameTemplate { get; set; } = "{Brand} {Model}";
    public string? Location { get; set; }
    public bool SetAsDefault { get; set; } = true;
    public bool DuplexByDefault { get; set; } = true;
    public bool MonochromeByDefault { get; set; }
    public bool PrintTestPage { get; set; } = true;
    public bool EnableSnmpStatus { get; set; } = true;
    /// <summary>
    /// Remplacer les imprimantes déjà installées pour la même IP (copies ajoutées toutes seules par Windows, anciennes installations)
    /// et celle du même nom. Sinon : suffixe « (2) » et simple avertissement.
    /// </summary>
    public bool ReplaceExisting { get; set; } = true;
    /// <summary>Partager l'imprimante depuis ce PC pour les autres postes du réseau.</summary>
    public bool ShareOnNetwork { get; set; }
    /// <summary>Nom du partage (vide = dérivé du nom Windows).</summary>
    public string? ShareName { get; set; }
    /// <summary>Empêcher Windows d'installer tout seul ses propres copies des imprimantes réseau (pilote générique).</summary>
    public bool DisableWindowsAutoSetup { get; set; } = true;
}

/// <summary>Configuration du dossier de numérisation sur le PC.</summary>
public sealed class ScanFolderOptions
{
    public string FolderPath { get; set; } = @"C:\Scans";
    public string ShareName { get; set; } = "Scans";
    public string UserName { get; set; } = "scan";
    public string Password { get; set; } = PasswordGenerator.Generate();
    public bool HideUserFromLogonScreen { get; set; } = true;
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool SetNetworkPrivate { get; set; }
}

public sealed class StepResult
{
    public required string Step { get; init; }
    public bool Success { get; init; }
    /// <summary>Étape non bloquante qui mérite l'attention (réglage refusé, réseau public…).</summary>
    public bool IsWarning { get; init; }
    public string? Detail { get; init; }
    public string Status => !Success ? Loc.T("Échec", "Failed") : IsWarning ? Loc.T("Attention", "Warning") : "OK";
}

public sealed class InstallResult
{
    public required DiscoveredPrinter Printer { get; init; }
    public string? WindowsName { get; set; }
    /// <summary>Chemin réseau si l'imprimante a été partagée (\\PC\partage).</summary>
    public string? ShareUnc { get; set; }
    public DriverChoice? Driver { get; set; }
    public List<StepResult> Steps { get; } = new();
    public bool Success => Steps.Count > 0 && Steps.All(s => s.Success);
    public string RawOutput { get; set; } = "";
}

public sealed class ScanFolderResult
{
    public required ScanFolderOptions Options { get; init; }
    public required string ComputerName { get; init; }
    public List<StepResult> Steps { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Success => Steps.Count > 0 && Steps.All(s => s.Success);
    public string UncPath => $@"\\{ComputerName}\{Options.ShareName}";
    public string RawOutput { get; set; } = "";
}
