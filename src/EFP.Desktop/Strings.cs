using Avalonia.Markup.Xaml;
using EFP.Core;

namespace EFP.Desktop;

/// <summary>Textes fixes de l'interface (français, anglais). Dans le XAML : <c>Text="{local:L Ready}"</c>.</summary>
public static class Strings
{
    private static readonly Dictionary<string, (string Fr, string En)> All = new()
    {
        ["Ready"] = ("Prêt", "Ready"),
        ["TipTheme"] = ("Mode clair / sombre", "Light / dark mode"),
        ["TipLanguage"] = ("Passer l'interface en anglais", "Switch the interface to French"),
        ["TipDriversTab"] = ("Tous les pilotes disponibles, triés par marque", "All available drivers, sorted by brand"),
        ["DriversTab"] = ("Pilotes", "Drivers"),
        ["PrintersTab"] = ("Imprimantes", "Printers"),
        ["Tagline"] = ("EazyPrinter · imprimantes en 3 clics", "EazyPrinter · printers in 3 clicks"),
        ["ClickHint"] = ("Clique sur une imprimante pour l'installer, Ctrl + clic pour en choisir plusieurs.", "Click a printer to install it, Ctrl + click to select several."),
        ["AdminWarning"] = ("Lancé sans droits administrateur : l'installation échouera.", "Running without administrator rights: installation will fail."),
        ["RangeWatermark"] = ("192.168.1.0/24, 192.168.1.10-60 ou l'IP d'une imprimante", "192.168.1.0/24, 192.168.1.10-60 or a printer's IP"),
        ["TipRange"] = ("Le réseau à scanner ou directement l'IP d'une imprimante. Entrée = lancer.", "The network to scan, or a printer's IP directly. Enter = start."),
        ["Search"] = ("Rechercher", "Search"),
        ["Stop"] = ("Arrêter", "Stop"),
        ["RangeHint"] = ("Réseau à scanner, ou l'IP d'une imprimante si elle n'apparaît pas.", "Network to scan, or a printer's IP if it doesn't show up."),
        ["IpAddress"] = ("Adresse IP", "IP address"),
        ["TipEditInline"] = ("Double-clic pour modifier, Entrée pour valider", "Double-click to edit, Enter to confirm"),
        ["Name"] = ("Nom", "Name"),
        ["Driver"] = ("Pilote", "Driver"),
        ["SerialMac"] = ("N° de série · MAC", "Serial no. · MAC"),
        ["OnThisComputer"] = ("Sur ce poste", "On this computer"),
        ["EditHint"] = ("Double-clic sur l'IP ou le nom pour les modifier.", "Double-click the IP or name to edit them."),
        ["ImportDriver"] = ("Importer un pilote…", "Import a driver…"),
        ["TipImportDriver"] = ("Pilote introuvable en ligne ? Choisis un fichier (ou glisse-le sur la fenêtre).", "Driver not found online? Pick a file (or drop it on the window)."),
        ["Folder"] = ("Dossier…", "Folder…"),
        ["TipImportFolder"] = ("Importer un dossier de pilote déjà extrait.", "Import an already extracted driver folder."),
        ["Deselect"] = ("Désélectionner", "Deselect"),
        ["Install"] = ("Installer", "Install"),
        ["MultiHint"] = ("Pilotes téléchargés au moment d'installer, une fois par marque. Ctrl + clic pour ajouter ou retirer une imprimante.", "Drivers are downloaded at install time, once per brand. Ctrl + click to add or remove a printer."),
        ["Searching"] = ("Recherche des imprimantes…", "Searching for printers…"),
        ["EmptyHint"] = ("Rien ne s'affiche ? Tape l'IP de l'imprimante dans le champ du haut.", "Nothing showing? Type the printer's IP in the field above."),
        ["DriverSearchWatermark"] = ("Rechercher un pilote : modèle, marque, série… (ex. L3760, M404, bizhub)", "Search a driver: model, brand, series… (e.g. L3760, M404, bizhub)"),
        ["ExpandAll"] = ("Tout ouvrir", "Expand all"),
        ["CollapseAll"] = ("Tout fermer", "Collapse all"),
        ["UseForPrinter"] = ("Utiliser pour l'imprimante", "Use for the printer"),
        ["UseDriverHintNone"] = ("Choisis d'abord une imprimante dans « Imprimantes », puis un pilote ici (double-clic ou bouton).", "First pick a printer in \"Printers\", then a driver here (double-click or button)."),
        ["TipToggleBrand"] = ("Cliquer pour ouvrir ou fermer", "Click to expand or collapse"),
        ["Log"] = ("Journal", "Log"),
        ["SetDefault"] = ("Mettre comme imprimante par défaut sur ce poste", "Set as the default printer on this computer"),
        ["DefaultOnThis"] = ("Imprimante par défaut sur ce poste", "Default printer on this computer"),
        ["TestPage"] = ("Imprimer une page de test", "Print a test page"),
        ["Finish"] = ("Terminer", "Finish"),
        ["DropHere"] = ("Dépose ton pilote ici", "Drop your driver here"),
    };

    public static string Get(string key) =>
        All.TryGetValue(key, out var t) ? Loc.T(t.Fr, t.En) : key;
}

/// <summary>Extension XAML : <c>{local:L Clé}</c> → texte dans la langue choisie.</summary>
public sealed class LExtension : MarkupExtension
{
    public LExtension(string key) => Key = key;
    public string Key { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
