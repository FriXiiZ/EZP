using System.Globalization;

namespace EFP.Core;

/// <summary>
/// Langue de l'interface : français ou anglais. Les textes restent écrits en français dans le code,
/// avec leur traduction anglaise juste à côté : <c>Loc.T("Prêt", "Ready")</c>.
/// </summary>
public static class Loc
{
    /// <summary>Vrai = anglais. Par défaut : français (les tests vérifient les textes français).</summary>
    public static bool En { get; set; }

    public static string T(string fr, string en) => En ? en : fr;

    /// <summary>"fr", "en" ou null (langue du système : français si le système est en français, sinon anglais).</summary>
    public static void Apply(string? language) =>
        En = language switch
        {
            "en" => true,
            "fr" => false,
            _ => !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase),
        };

    /// <summary>Noms des étapes d'installation : clés internes en français, affichées dans la langue choisie.</summary>
    private static readonly Dictionary<string, string> StepNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pilote"] = "Driver",
        ["Port"] = "Port",
        ["Imprimante"] = "Printer",
        ["Réglages"] = "Settings",
        ["État"] = "Status",
        ["Auto-ajout Windows"] = "Windows auto-setup",
        ["Lancement"] = "Launch",
        ["Délai"] = "Timeout",
        ["PowerShell"] = "PowerShell",
        ["Par défaut"] = "Default",
        ["Imprimante par défaut"] = "Default printer",
        ["Page de test"] = "Test page",
        ["Doublons"] = "Duplicates",
        ["Partage"] = "Sharing",
        ["Dossier"] = "Folder",
        ["Compte"] = "Account",
        ["Droits"] = "Permissions",
        ["Pare-feu"] = "Firewall",
        ["Raccourci"] = "Shortcut",
        ["Réseau"] = "Network",
        ["Autorisation"] = "Authorization",
        ["CUPS"] = "CUPS",
    };

    public static string Step(string step) => En && StepNames.TryGetValue(step, out var en) ? en : step;
}
