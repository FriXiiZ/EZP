using EFP.Core;

namespace EFP.Desktop;

/// <summary>Une ligne de l'onglet Pilotes : en-tête de marque, ou pilote.</summary>
public sealed class DriverListItem
{
    public bool IsHeader { get; init; }
    public bool IsDriver => !IsHeader;
    /// <summary>En-tête : liste de la marque ouverte ?</summary>
    public bool IsExpanded { get; init; }
    public string Chevron => IsExpanded ? "▾" : "▸";
    public string Brand { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DriverChoice? Choice { get; init; }

    /// <summary>Texte de recherche (marque + nom + détail), en minuscules sans tirets ni espaces.</summary>
    public string SearchKey { get; init; } = "";

    public string Badge => Choice?.Kind switch
    {
        DriverMatchKind.Imported => EFP.Core.Loc.T("★ importé", "★ imported"),
        DriverMatchKind.Driverless => "IPP / AirPrint",
        DriverMatchKind.GenericInbox => EFP.Core.Loc.T("générique", "generic"),
        DriverMatchKind.BrandUniversal => EFP.Core.Loc.T("universel", "universal"),
        _ => "",
    };

    public static string Key(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
