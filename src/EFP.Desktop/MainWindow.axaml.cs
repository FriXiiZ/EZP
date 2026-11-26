using System.Collections.ObjectModel;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EFP.Core;
using EFP.Core.Discovery;
using EFP.Core.Drivers;
using EFP.Core.Platform;
using EFP.Core.Updates;
using EFP.Core.Windows;

namespace EFP.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IPrinterPlatform _platform = PrinterPlatforms.Current;
    private readonly ObservableCollection<PrinterTile> _rows = new();
    private readonly List<PrinterTile> _selected = new();
    private PrinterTile? _current, _anchor;
    private CancellationTokenSource? _scanCts;
    private bool _busy, _driverBusy;

    // Windows : bibliothèque .inf + téléchargements des constructeurs + Windows Update
    private DriverLibrary _library = DriverLibrary.Empty();
    private DriverCatalog _catalog = DriverCatalog.Default();
    private DriverMatcher _matcher;
    private readonly DriverFeed _feed = DriverFeed.Default();
    private static readonly HttpClient Http = DriverUpdater.CreateHttpClient();
    // Linux / macOS : pilotes CUPS
    private readonly CupsDriverSource _cups = new();

    private List<InstalledPrinter> _installedHere = new();

    private static string DriversFolder => Path.Combine(Program.DataFolder, "Drivers");
    private static string CatalogPath => Path.Combine(Program.DataFolder, "drivers.json");

    public MainWindow() : this(new AppSettings()) { }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        _matcher = new DriverMatcher(_library, _catalog);
        InitializeComponent();

        PrinterTiles.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => { EmptyHint.IsVisible = _rows.Count == 0; UpdateSelectionCount(); };

        ScanButton.Click += (_, _) => StartSearch();
        StopButton.Click += (_, _) => _scanCts?.Cancel();
        RangeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { StartSearch(); e.Handled = true; } };
        ThemeButton.Click += (_, _) => ToggleTheme();
        DetailInstallButton.Click += async (_, _) => { if (_current is not null) await InstallRowsAsync(new List<PrinterTile> { _current }); };
        MultiInstallButton.Click += async (_, _) => await InstallRowsAsync(_selected.ToList());
        MultiClearButton.Click += (_, _) => SetSelection(Array.Empty<PrinterTile>());
        ImportDriverButton.Click += async (_, _) => await PickDriverFilesAsync();
        ImportFolderButton.Click += async (_, _) => await PickDriverFolderAsync();
        PrinterTiles.AddHandler(PointerReleasedEvent, OnTilePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        foreach (var box in new[] { DetailIpBox, DetailNameBox })
        {
            box.DoubleTapped += (_, _) => { box.IsReadOnly = false; box.SelectAll(); box.Focus(); };
            box.LostFocus += (_, _) => box.IsReadOnly = true;
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { box.IsReadOnly = true; DetailInstallButton.Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape)
                {
                    var tile = DetailPanel.DataContext as PrinterTile;
                    box.Text = box == DetailIpBox ? tile?.Ip : tile?.WindowsName;
                    box.IsReadOnly = true; DetailInstallButton.Focus(); e.Handled = true;
                }
            };
        }

        KeyDown += (_, e) =>
        {
            if (FocusManager?.GetFocusedElement() is TextBox || _busy) return;
            if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SetSelection(_rows); e.Handled = true; }
            else if (e.Key == Key.Escape) { SetSelection(Array.Empty<PrinterTile>()); e.Handled = true; }
        };

        AddHandler(DragDrop.DragEnterEvent, (_, e) => { if (e.Data.Contains(DataFormats.Files)) DropOverlay.IsVisible = true; });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            DropOverlay.IsVisible = false;
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
            if (paths is { Count: > 0 }) await ImportDriversAsync(paths);
        });
        DropHint.Text = _platform.UsesWindowsDrivers
            ? ".zip · .exe · .inf · .cab · dossier — il devient prioritaire pour sa marque"
            : "fichier .ppd du constructeur — il devient prioritaire pour sa marque";

        DriversTabButton.IsCheckedChanged += (_, _) => ShowDriversPage(DriversTabButton.IsChecked == true);
        DriverSearchBox.TextChanged += (_, _) => { _searchCollapsed.Clear(); ScheduleDriverFilter(); };
        BrandFilter.SelectionChanged += (_, _) =>
        {
            if (BrandFilter.SelectedIndex > 0 && BrandFilter.SelectedItem is string b) _expandedBrands.Add(b); // marque choisie : ouverte (refermable)
            ScheduleDriverFilter();
        };
        DriverList.SelectionChanged += (_, _) =>
        {
            // Clic sur une marque : ouvre ou ferme sa liste
            if (DriverList.SelectedItem is DriverListItem { IsHeader: true } h)
            {
                var set = SearchActive ? _searchCollapsed : _expandedBrands; // en recherche : on retient les marques fermées
                if (!set.Remove(h.Brand)) set.Add(h.Brand);
                ApplyDriverFilter(keepBrand: h.Brand);
                return;
            }
            UpdateUseDriverButton();
        };
        ExpandAllButton.Click += (_, _) =>
        {
            _searchCollapsed.Clear();
            foreach (var b in _allDrivers.Where(d => d.IsHeader)) _expandedBrands.Add(b.Brand);
            ApplyDriverFilter();
        };
        CollapseAllButton.Click += (_, _) =>
        {
            _expandedBrands.Clear();
            foreach (var b in _allDrivers.Where(d => d.IsHeader)) _searchCollapsed.Add(b.Brand);
            ApplyDriverFilter();
        };
        DriverList.DoubleTapped += (_, _) => UseSelectedDriver();
        UseDriverButton.Click += (_, _) => UseSelectedDriver();

        Title = $"EZP — EazyPrinter — v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · {_platform.Name}";
        UpdateThemeIcon();
        AdminWarningText.IsVisible = !Program.CanInstall;
        Opened += async (_, _) => await OnOpenedAsync();
    }

    // ------------------------------------------------------------------ Démarrage

    private async Task OnOpenedAsync()
    {
        try
        {
            var subnets = IpRange.GetLocalSubnets();
            if (subnets.Count > 0) RangeBox.Text = subnets[0].Cidr;
            else Log("Aucune carte réseau IPv4 active trouvée : tape un réseau (ex. 192.168.1.0/24) ou l'IP d'une imprimante.");
        }
        catch (Exception ex) { Log($"Cartes réseau illisibles ({ex.Message}) : tape le réseau à la main."); }

        try { await LoadDriversAsync(); }
        catch (Exception ex) { Log($"Lecture des pilotes impossible : {ex.Message}"); }

        if (!_platform.UsesWindowsDrivers && !File.Exists("/usr/sbin/lpadmin") && !File.Exists("/usr/bin/lpadmin"))
            Log("⚠ CUPS n'est pas installé : installe le paquet « cups » pour pouvoir ajouter des imprimantes.");

        if (_settings.ScanAtStartup && !string.IsNullOrWhiteSpace(RangeBox.Text)) StartSearch();
        else EmptyHintTitle.Text = "Lance une recherche avec le bouton « Rechercher »";
    }

    private void ToggleTheme()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
        _settings.Theme = dark ? "light" : "dark";
        _settings.Save();
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        var dark = (Application.Current?.ActualThemeVariant ?? ThemeVariant.Light) == ThemeVariant.Dark;
        ThemeIcon.Data = (Geometry)this.FindResource(dark ? "IconSun" : "IconMoon")!;
    }

    // ------------------------------------------------------------------ Recherche

    private void StartSearch()
    {
        if (_scanCts is not null || _busy) return;
        var text = (RangeBox.Text ?? "").Trim();
        if (IpRange.TryParseV4(text, out var single) && !text.Contains('/') && !text.Contains('-'))
            _ = AddManualAsync(single);
        else
            _ = ScanAsync(text);
    }

    private async Task ScanAsync(string range)
    {
        List<IPAddress> addresses;
        try { addresses = IpRange.Parse(range); }
        catch (FormatException ex) { Warn(ex.Message); return; }
        if (addresses.Count == 0) { Warn("Plage vide."); return; }

        _scanCts = new CancellationTokenSource();
        SetScanning(true);
        ScanProgressBar.Value = 0;
        EmptyHintTitle.Text = "Recherche des imprimantes…";
        var started = DateTime.Now;
        Log($"Scan de {addresses.Count} adresses ({range})…");

        var known = new HashSet<string>(_rows.Select(r => r.Ip));
        var maxDone = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Done < maxDone) return;
            maxDone = p.Done;
            ScanProgressBar.Value = (double)p.Done / p.Total;
            StatusText.Text = $"Recherche : {p.Done}/{p.Total} · {p.Found} imprimante(s)";
        });

        try
        {
            await new PrinterScanner().ScanAsync(addresses, printer => Dispatcher.UIThread.Post(() =>
            {
                if (!known.Add(printer.IpAddress)) return;
                AddRow(printer);
                Log($"  trouvée : {printer.IpAddress}  {printer.DisplayName}{(printer.SerialNumber is null ? "" : $"  (S/N {printer.SerialNumber})")}");
            }), progress, _scanCts.Token);
            Log($"Scan terminé en {(DateTime.Now - started).TotalSeconds:F0} s : {_rows.Count} imprimante(s).");
            StatusText.Text = $"{_rows.Count} imprimante(s)";
            if (_rows.Count == 0) EmptyHintTitle.Text = "Aucune imprimante trouvée : vérifie le réseau ou tape son IP";
            await RefreshCupsModelsAsync();
            await RefreshInstalledHereAsync();
        }
        catch (OperationCanceledException) { Log("Scan arrêté."); StatusText.Text = "Scan arrêté"; }
        finally
        {
            SetScanning(false);
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private async Task AddManualAsync(IPAddress ip)
    {
        var existing = _rows.FirstOrDefault(r => r.Ip == ip.ToString());
        if (existing is not null) { SetSelection(new[] { existing }); return; }
        StatusText.Text = $"Interrogation de {ip}…";
        var printer = await new PrinterScanner().ProbeAsync(ip, requirePrinterEvidence: false);
        if (printer is null)
        {
            Log($"{ip} ne répond ni en SNMP ni sur les ports d'impression : ajoutée quand même (icône grise), choisis le pilote dans son menu.");
            printer = new DiscoveredPrinter { IpAddress = ip.ToString() };
        }
        var row = AddRow(printer);
        SetSelection(new[] { row });
        Log($"Ajoutée à la main : {printer.IpAddress}  {printer.DisplayName}");
        StatusText.Text = "Prêt";
        await RefreshCupsModelsAsync();
        await RefreshInstalledHereAsync();
    }

    private PrinterTile AddRow(DiscoveredPrinter printer)
    {
        var row = new PrinterTile(printer, Candidates(printer));
        row.SetDefaultName(DefaultNameFor(printer));
        row.IpChanged += (r, oldIp) =>
        {
            Log($"IP modifiée à la main : {oldIp} → {r.Ip} ({r.Printer.DisplayName}).");
            r.SetDefaultName(DefaultNameFor(r.Printer));
        };
        var key = IpRange.ToUInt(IPAddress.Parse(printer.IpAddress));
        var index = 0;
        while (index < _rows.Count && IpRange.ToUInt(IPAddress.Parse(_rows[index].Ip)) < key) index++;
        _rows.Insert(index, row);
        return row;
    }

    private string DefaultNameFor(DiscoveredPrinter p) =>
        PowerShellScripts.BuildPrinterName(p, new InstallOptions { NameTemplate = string.IsNullOrWhiteSpace(_settings.NameTemplate) ? "{Brand} {Model}" : _settings.NameTemplate });

    private List<DriverChoice> Candidates(DiscoveredPrinter p) =>
        _platform.UsesWindowsDrivers ? _matcher.Candidates(p) : _cups.Candidates(p);

    // ------------------------------------------------------------------ Sélection (clic, Ctrl + clic, Maj + clic)

    private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var row = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<PrinterTile>().FirstOrDefault();
        if (row is null) return;
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Shift) && _anchor is not null && _rows.Contains(_anchor))
        {
            var i1 = _rows.IndexOf(_anchor); var i2 = _rows.IndexOf(row);
            var range = _rows.Skip(Math.Min(i1, i2)).Take(Math.Abs(i1 - i2) + 1).ToList();
            SetSelection(mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta) ? _selected.Union(range) : range);
            return;
        }
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta)) // Meta = Cmd sur Mac
            SetSelection(_selected.Contains(row) ? _selected.Where(r => r != row) : _selected.Append(row));
        else
            SetSelection(_selected.Count == 1 && _selected[0] == row ? Array.Empty<PrinterTile>() : new[] { row });
        _anchor = row;
    }

    private void SetSelection(IEnumerable<PrinterTile> rows)
    {
        var list = rows.Where(r => _rows.Contains(r)).Distinct().ToList();
        foreach (var r in _rows) r.IsCurrent = list.Contains(r);
        _selected.Clear();
        _selected.AddRange(_rows.Where(list.Contains));
        _current = _selected.Count == 1 ? _selected[0] : null;
        DetailIpBox.IsReadOnly = true; DetailNameBox.IsReadOnly = true;
        DetailPanel.DataContext = _current;
        DetailPanel.IsVisible = _current is not null;
        MultiPanel.IsVisible = _selected.Count > 1;
        if (_selected.Count > 1)
        {
            MultiTitle.Text = $"{_selected.Count} imprimantes sélectionnées";
            MultiText.Text = string.Join(" · ", _selected.Select(r => r.TileTitle));
            MultiInstallText.Text = $"Installer les {_selected.Count}";
        }
    }

    // ------------------------------------------------------------------ Pilotes

    private DriverUpdater CreateUpdater() => new(DriversFolder, Http);

    private async Task LoadDriversAsync()
    {
        Directory.CreateDirectory(DriversFolder);
        if (_platform.UsesWindowsDrivers)
        {
            LibraryText.Text = "Lecture des pilotes…";
            if (!File.Exists(CatalogPath))
                try { await File.WriteAllTextAsync(CatalogPath, DriverCatalog.Default().ToJson()); } catch { }
            var (lib, cat) = await Task.Run(() => (DriverLibrary.Load(DriversFolder), File.Exists(CatalogPath) ? DriverCatalog.Load(CatalogPath) : DriverCatalog.Default()));
            _library = lib; _catalog = cat;
            _matcher = new DriverMatcher(_library, _catalog);
            var brands = _library.BrandsAvailable.ToList();
            LibraryText.Text = _library.Drivers.Count == 0 ? "Pilotes téléchargés à l'installation" : $"{_library.Drivers.Count} pilotes · {string.Join(", ", brands)}";
        }
        else
        {
            LibraryText.Text = "Lecture des pilotes CUPS…";
            await _cups.LoadAsync(DriversFolder);
            LibraryText.Text = $"CUPS · {_cups.Models.Count} pilotes disponibles";
        }
        foreach (var row in _rows) row.ReplaceDrivers(Candidates(row.Printer));
        if (_installedHere.Count > 0) await RefreshInstalledHereAsync(reload: false);
    }

    /// <summary>Linux / macOS : pilotes CUPS propres aux imprimantes trouvées (sans pilote généré pour le modèle…).</summary>
    private async Task RefreshCupsModelsAsync()
    {
        if (_platform.UsesWindowsDrivers || _rows.Count == 0) return;
        var wanted = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Printer.Model)).Select(r => r.Printer.DisplayName).ToList();
        if (!await _cups.RefreshForAsync(wanted)) return;
        foreach (var row in _rows)
        {
            var before = row.SelectedDriver?.Choice.InfPath;
            row.ReplaceDrivers(Candidates(row.Printer));
            if (row.SelectedDriver?.Choice.InfPath != before) Log($"Pilote proposé pour {row.Printer.DisplayName} : {row.SelectedDriver?.Choice.DriverName}");
        }
        LibraryText.Text = $"CUPS · {_cups.Models.Count} pilotes disponibles";
    }

    private async Task<bool> WaitDriversIdleAsync()
    {
        var until = DateTime.Now.AddMinutes(5);
        while (_driverBusy && DateTime.Now < until) await Task.Delay(300);
        return !_driverBusy;
    }

    /// <summary>Windows : PostScript, sinon PCL de la marque (téléchargés une fois et gardés).</summary>
    private async Task ProvisionBrandsAsync(IEnumerable<string> brands)
    {
        var list = brands.Where(b => b != Brands.Unknown).Distinct().ToList();
        if (list.Count == 0 || !await WaitDriversIdleAsync()) return;
        _driverBusy = true;
        try
        {
            var prov = new DriverProvisioner(CreateUpdater(), _feed, _catalog, () => DriverLibrary.Load(DriversFolder));
            foreach (var brand in list)
            {
                var rows = _rows.Where(r => r.Printer.Brand == brand).ToList();
                var progress = new Progress<DownloadProgress>(p =>
                {
                    var pct = p.Total is > 0 ? $"{100 * p.Received / p.Total.Value} %" : p.Stage;
                    foreach (var r in rows) r.Status = p.Stage == "Téléchargement" ? $"Pilote : {pct}" : p.Stage + "…";
                });
                ProvisionResult result;
                try { result = await Task.Run(() => prov.EnsureAsync(brand, progress)); }
                catch (Exception ex) { Log($"Pilote {brand} : {ex.Message}"); continue; }
                foreach (var line in result.Log) Log($"    {line}");
                Log(result.Outcome switch
                {
                    ProvisionOutcome.AlreadyPostScript => $"Pilote {brand} : PostScript déjà présent ({result.DriverName}).",
                    ProvisionOutcome.DownloadedPostScript => $"Pilote {brand} : PostScript téléchargé ({result.DriverName}).",
                    ProvisionOutcome.AlreadyPcl => $"Pilote {brand} : PCL déjà présent ({result.DriverName}).",
                    ProvisionOutcome.DownloadedPcl => $"Pilote {brand} : PCL téléchargé ({result.DriverName}).",
                    ProvisionOutcome.ManualOnly => $"Pilote {brand} : pas de lien direct chez {brand}, essai sur Windows Update…",
                    _ => $"Pilote {brand} : rien trouvé chez le constructeur, essai sur Windows Update…",
                });
            }
        }
        finally { _driverBusy = false; }
    }

    /// <summary>Windows : pilote exact du modèle sur Windows Update.</summary>
    private async Task<bool> FetchFromWindowsUpdateAsync(string brand, string model)
    {
        var lib = DriverLibrary.Load(DriversFolder);
        if (lib.Drivers.Any(d => d.IsWindowsUpdate && d.Brand == brand && DriverMatcher.ModelScore(model, d.Name) > 0)) return false;
        var rows = _rows.Where(r => r.Printer.Brand == brand && string.Equals(r.Printer.Model, model, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in rows) { r.Status = "Windows Update…"; r.Spinning = true; }
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = p.Total is > 0 ? $"{100 * p.Received / p.Total.Value} %" : p.Stage;
            foreach (var r in rows) r.Status = p.Stage == "Téléchargement" ? $"Pilote : {pct}" : p.Stage + "…";
        });
        try
        {
            var lines = new List<string>();
            var dir = await Task.Run(() => new WindowsUpdateCatalog(Http).FetchAsync(DriversFolder, brand, model, CreateUpdater(), progress, l => { lock (lines) lines.Add(l); }));
            foreach (var l in lines) Log($"    {l}");
            return dir is not null;
        }
        catch (Exception ex) { Log($"    Windows Update indisponible pour {model} : {ex.Message}"); return false; }
        finally { foreach (var r in rows) { r.Spinning = false; r.Status = ""; } }
    }

    // ------------------------------------------------------------------ Import d'un pilote

    private async Task PickDriverFilesAsync()
    {
        var types = _platform.UsesWindowsDrivers
            ? new FilePickerFileType("Pilote") { Patterns = new[] { "*.zip", "*.exe", "*.inf", "*.cab", "*.msi", "*.7z" } }
            : new FilePickerFileType("Pilote PPD") { Patterns = new[] { "*.ppd", "*.PPD" } };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Importer un pilote", AllowMultiple = true, FileTypeFilter = new[] { types } });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        if (paths.Count > 0) await ImportDriversAsync(paths);
    }

    private async Task PickDriverFolderAsync()
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Dossier du pilote" });
        var paths = dirs.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        if (paths.Count > 0) await ImportDriversAsync(paths);
    }

    private async Task ImportDriversAsync(List<string> paths)
    {
        var imported = 0;
        foreach (var path in paths)
        {
            Log($"Import du pilote : {path}");
            try
            {
                if (_platform.UsesWindowsDrivers)
                {
                    var r = await Task.Run(() => new DriverImporter(DriversFolder, CreateUpdater()).ImportAsync(path));
                    Log($"    {(r.Success ? "✓" : "✗")} {r.Message}");
                    if (r.Success) imported++;
                }
                else
                {
                    var ppds = Directory.Exists(path)
                        ? Directory.EnumerateFiles(path, "*.ppd", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).ToList()
                        : path.EndsWith(".ppd", StringComparison.OrdinalIgnoreCase) ? new List<string> { path } : new List<string>();
                    if (ppds.Count == 0) { Log("    ✗ aucun fichier .ppd (sous Linux/macOS, il faut le PPD du constructeur)"); continue; }
                    foreach (var ppd in ppds)
                    {
                        var nick = CupsDriverSource.ReadNickName(ppd) ?? Path.GetFileNameWithoutExtension(ppd);
                        var brand = Brands.Detect(nick);
                        var dest = Path.Combine(DriversFolder, brand == Brands.Unknown ? "Autres" : brand, "import-" + CupsScripts.QueueName(nick));
                        Directory.CreateDirectory(dest);
                        File.Copy(ppd, Path.Combine(dest, Path.GetFileName(ppd)), true);
                        Log($"    ✓ {nick} ({brand})");
                        imported++;
                    }
                }
            }
            catch (Exception ex) { Log($"    ✗ {ex.Message}"); }
        }
        if (imported > 0)
        {
            await LoadDriversAsync();
            Log("Pilote importé : il est maintenant proposé en premier (★) pour sa marque.");
            StatusText.Text = $"{imported} pilote(s) importé(s)";
        }
        else Warn("Aucun pilote importé (détail dans le journal).");
    }

    // ------------------------------------------------------------------ Déjà installées sur ce poste ?

    private async Task RefreshInstalledHereAsync(bool reload = true)
    {
        if (reload)
        {
            try { _installedHere = await _platform.ListInstalledAsync(); }
            catch (Exception ex) { Log($"Lecture des imprimantes de ce poste impossible : {ex.Message}"); return; }
        }
        var outdatedCount = 0;
        foreach (var row in _rows)
        {
            var here = _installedHere.Where(i => i.Ip == row.Ip).OrderBy(i => IsGenericDriver(i.DriverName) ? 1 : 0).FirstOrDefault()
                       ?? AutoAddedCopy(row);
            var best = row.SelectedDriver?.Choice;
            bool outdated = false; string? latest = null;
            if (here is not null && best is not null && best.Kind != DriverMatchKind.GenericInbox)
            {
                if (IsGenericDriver(here.DriverName)) outdated = true;
                else if (_platform.UsesWindowsDrivers && string.Equals(here.DriverName, best.DriverName, StringComparison.OrdinalIgnoreCase))
                {
                    latest = LibraryVersion(best.DriverName);
                    outdated = latest is not null && here.DriverVersion is not null && VersionComparer.Compare(here.DriverVersion, latest) < 0;
                }
            }
            row.SetInstalledHere(here, outdated, outdated ? latest : null);
            if (outdated) outdatedCount++;
        }
        var already = _rows.Count(r => r.IsInstalledHere);
        if (already > 0 && reload)
            Log($"Sur ce poste : {already} imprimante(s) déjà installée(s){(outdatedCount > 0 ? $", dont {outdatedCount} avec un pilote à mettre à jour (point orange)" : ", pilotes à jour")}.");
        UpdateSelectionCount();
    }

    /// <summary>
    /// Copie ajoutée toute seule par le système, sans IP dans son adresse (WSD sous Windows, Bonjour / cups-browsed
    /// sous Linux et macOS) : reconnue au modèle (MFC_L3760CDW ≈ MFC-L3760CDW).
    /// </summary>
    private InstalledPrinter? AutoAddedCopy(PrinterTile row)
    {
        var key = PowerShellScripts.ModelKey(row.Printer.Model);
        if (key is null) return null;
        foreach (var i in _installedHere)
        {
            if (IpRange.TryParseV4(i.Ip, out _)) continue; // adresse IP : déjà comparée
            var text = System.Text.RegularExpressions.Regex.Replace($"{i.Name} {i.DriverName} {Uri.UnescapeDataString(i.Ip)}", "[^A-Za-z0-9]", "").ToUpperInvariant();
            if (text.Contains(key)) return i with { DriverName = i.DriverName + " — ajoutée automatiquement par le système" };
        }
        return null;
    }

    private static bool IsGenericDriver(string name) =>
        name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || name.Contains("WSD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Generic", StringComparison.OrdinalIgnoreCase) || name.Contains("Class Driver", StringComparison.OrdinalIgnoreCase)
        || (OperatingSystem.IsWindows() && name.Contains("IPP", StringComparison.OrdinalIgnoreCase));

    private string? LibraryVersion(string driverName)
    {
        var v = _library.Drivers.FirstOrDefault(d => string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase))?.Version;
        if (string.IsNullOrWhiteSpace(v)) return null;
        var i = v.IndexOf(',');
        return (i >= 0 ? v[(i + 1)..] : v).Trim();
    }

    // ------------------------------------------------------------------ Installation

    private async Task InstallRowsAsync(List<PrinterTile> rows)
    {
        if (_busy || rows.Count == 0) return;
        FocusManager?.ClearFocus();
        SetBusy(true);
        SetSelection(Array.Empty<PrinterTile>());
        foreach (var r in rows) { r.State = TileState.Working; r.Working = true; r.Progress = 0; r.StatusDetail = ""; r.Status = "En attente…"; }
        var done = new List<(PrinterTile Row, string Name)>();
        try
        {
            // 1. Windows : pilote de la marque téléchargé maintenant (une fois par marque), puis Windows Update pour le modèle
            if (_platform.UsesWindowsDrivers)
            {
                static bool Generic(PrinterTile r) => r.SelectedDriver is null || r.SelectedDriver.Choice.Kind == DriverMatchKind.GenericInbox;
                var brands = rows.Where(r => Generic(r) && r.Printer.Brand != Brands.Unknown).Select(r => r.Printer.Brand).Distinct().ToList();
                if (brands.Count > 0)
                {
                    var waiting = rows.Where(r => brands.Contains(r.Printer.Brand)).ToList();
                    foreach (var r in waiting) { r.Status = "Téléchargement du pilote…"; r.Spinning = true; }
                    try
                    {
                        await ProvisionBrandsAsync(brands);
                        await LoadDriversAsync();
                        var models = rows.Where(r => Generic(r) && r.Printer.Brand != Brands.Unknown && !string.IsNullOrWhiteSpace(r.Printer.Model))
                                         .Select(r => (r.Printer.Brand, Model: r.Printer.Model!)).Distinct().ToList();
                        var added = false;
                        foreach (var (brand, model) in models)
                            if (await FetchFromWindowsUpdateAsync(brand, model)) added = true;
                        if (added) await LoadDriversAsync();
                    }
                    finally { foreach (var r in waiting) { r.Spinning = false; r.Status = "En attente…"; } }
                }
                foreach (var r in rows.Where(Generic))
                    Log($"⚠ {r.Printer.DisplayName} : aucun pilote {r.Printer.Brand} trouvé (ni PostScript, ni PCL, ni Windows Update) — pilote générique. Glisse le pilote du constructeur sur la fenêtre pour l'utiliser.");
            }

            // 2. Installation, une imprimante après l'autre
            var n = 0;
            foreach (var row in rows)
            {
                n++;
                StatusText.Text = rows.Count > 1 ? $"Installation {n}/{rows.Count} : {row.Printer.DisplayName}…" : $"Installation de {row.Printer.DisplayName}…";
                var name = await InstallOneAsync(row);
                if (name is not null) done.Add((row, name));
            }
            UpdateSelectionCount();
            if (done.Count == 0)
            {
                StatusText.Text = "Échec de l'installation : ouvre le journal pour le détail";
                return;
            }

            // 3. Fin : par défaut ? pages de test ?
            var (defaultName, testPages) = await AskFinishAsync(done.Select(x => x.Name).ToList(), rows.Count - done.Count);
            foreach (var (_, name) in done)
            {
                var setDefault = name == defaultName;
                if (!setDefault && !testPages) continue;
                foreach (var s in await _platform.FinishAsync(name, setDefault, testPages))
                    Log($"    {(s.Success ? (s.IsWarning ? "!" : "✓") : "✗")} {name} — {s.Step} : {s.Detail}");
            }
            StatusText.Text = done.Count == 1 ? $"« {done[0].Name} » est prête"
                : $"{done.Count} imprimante(s) installée(s){(rows.Count > done.Count ? $", {rows.Count - done.Count} en échec" : "")}";
            await RefreshInstalledHereAsync();
        }
        finally
        {
            foreach (var r in rows) { r.Working = false; if (r.State == TileState.Working) { r.State = TileState.Ready; r.Status = ""; } }
            SetBusy(false);
        }
    }

    private async Task<string?> InstallOneAsync(PrinterTile row)
    {
        var options = new InstallOptions
        {
            NameTemplate = string.IsNullOrWhiteSpace(_settings.NameTemplate) ? "{Brand} {Model}" : _settings.NameTemplate,
            SetAsDefault = false,
            PrintTestPage = false,
            DisableWindowsAutoSetup = _settings.DisableWindowsAutoSetup,
            DuplexByDefault = _settings.DuplexByDefault,
            ReplaceExisting = _settings.ReplaceExisting,
        };
        var driver = row.SelectedDriver?.Choice ?? Candidates(row.Printer).First();
        Log($"Installation : {row.Printer.IpAddress}  {row.Printer.DisplayName}  —  pilote « {driver.DriverName} »");

        var steps = new List<string> { "Pilote", "Port", "Imprimante", "Réglages", "État", "Auto-ajout Windows" };
        if (!_platform.UsesWindowsDrivers) steps.Remove("Auto-ajout Windows");
        var labels = new Dictionary<string, string>
        {
            ["Pilote"] = "Installation du pilote…", ["Port"] = "Création du port…", ["Imprimante"] = "Ajout de l'imprimante…",
            ["Réglages"] = "Réglages…", ["État"] = "Vérification…", ["Auto-ajout Windows"] = "Finitions…",
        };
        row.Progress = 0.06;
        row.Status = _platform.UsesWindowsDrivers ? "Préparation…" : "Autorisation administrateur…";
        var started = DateTime.Now;
        var live = new Progress<StepProgress>(p =>
        {
            var i = steps.IndexOf(p.Step);
            if (p.Status == "START")
            {
                row.Status = labels.TryGetValue(p.Step, out var l) ? l : p.Step + "…";
                if (i >= 0) row.Progress = Math.Max(row.Progress, (i + 0.3) / steps.Count);
                return;
            }
            if (i >= 0) row.Progress = Math.Max(row.Progress, (i + 1.0) / steps.Count);
            Log($"    {(p.Status == "FAIL" ? "✗" : p.Status == "WARN" ? "!" : "✓")} {p.Step} : {p.Detail}");
        });

        InstallResult result;
        try { result = await _platform.InstallAsync(row.Printer, driver, options, row.WindowsName, live); }
        catch (Exception ex)
        {
            result = new InstallResult { Printer = row.Printer, Driver = driver };
            result.Steps.Add(new StepResult { Step = "Lancement", Success = false, Detail = ex.Message });
        }
        foreach (var s in result.Steps.Where(s => s.Step is "Lancement" or "Délai" or "PowerShell"))
            Log($"    ✗ {s.Step} : {s.Detail}");

        row.StatusDetail = string.Join("\n", result.Steps.Select(s => $"{s.Status} — {s.Step} : {s.Detail}"));
        row.Progress = 1;
        row.State = !result.Success ? TileState.Failed : result.Steps.Any(s => s.IsWarning) ? TileState.Warning : TileState.Done;
        row.Status = "";
        row.Working = false;
        Log(result.Success ? $"  → installée sous « {result.WindowsName} » en {(DateTime.Now - started).TotalSeconds:F0} s" : "  → échec (détail ci-dessus)");
        return result.Success ? result.WindowsName ?? row.WindowsName : null;
    }

    private Task<(string? DefaultName, bool TestPages)> AskFinishAsync(List<string> names, int failed)
    {
        var tcs = new TaskCompletionSource<(string?, bool)>();
        var single = names.Count == 1;
        FinishTitle.Text = single ? $"{names[0]} est installée" : $"{names.Count} imprimantes installées";
        FinishSubtitle.Text = failed > 0 ? $"{failed} en échec (voir le journal). Dernière étape :" : "Dernière étape :";
        DefaultCheck.IsVisible = single;
        DefaultPanel.IsVisible = !single;
        DefaultCheck.IsChecked = true;
        DefaultCombo.ItemsSource = new[] { "Aucune (ne rien changer)" }.Concat(names).ToList();
        DefaultCombo.SelectedIndex = 0;
        TestPageCheck.Content = single ? "Imprimer une page de test" : "Imprimer une page de test sur chacune";
        TestPageCheck.IsChecked = single;
        FinishOverlay.IsVisible = true;
        void Done(object? _, RoutedEventArgs __)
        {
            FinishButton.Click -= Done;
            FinishOverlay.IsVisible = false;
            string? def = single ? (DefaultCheck.IsChecked == true ? names[0] : null)
                                 : DefaultCombo.SelectedIndex > 0 ? names[DefaultCombo.SelectedIndex - 1] : null;
            tcs.TrySetResult((def, TestPageCheck.IsChecked == true));
        }
        FinishButton.Click += Done;
        FinishButton.Focus();
        return tcs.Task;
    }

    // ------------------------------------------------------------------ Onglet Pilotes

    private List<DriverListItem> _allDrivers = new();
    private readonly HashSet<string> _expandedBrands = new();
    private readonly HashSet<string> _searchCollapsed = new();
    private bool SearchActive => !string.IsNullOrWhiteSpace(DriverSearchBox.Text);
    private DispatcherTimer? _filterTimer;

    private void ShowDriversPage(bool show)
    {
        DriversPage.IsVisible = show;
        if (show)
        {
            _allDrivers = BuildDriverList();
            var brands = _allDrivers.Select(d => d.Brand).Distinct().ToList();
            var keep = BrandFilter.SelectedItem as string;
            BrandFilter.ItemsSource = new[] { "Toutes les marques" }.Concat(brands).ToList();
            BrandFilter.SelectedItem = keep is not null && brands.Contains(keep) ? keep : "Toutes les marques";
            // Imprimante choisie : sa marque est ouverte et amenée à l'écran (les autres restent fermées)
            var focus = _current is not null && brands.Contains(_current.Printer.Brand) ? _current.Printer.Brand : null;
            if (focus is not null) _expandedBrands.Add(focus);
            ApplyDriverFilter(keepBrand: focus);
            DriverSearchBox.Focus();
        }
        UpdateUseDriverButton();
    }

    /// <summary>Tous les pilotes connus sur ce système, regroupés par marque puis triés par nom.</summary>
    private List<DriverListItem> BuildDriverList()
    {
        var drivers = new List<(string Brand, DriverChoice Choice, string Detail)>();
        if (_platform.UsesWindowsDrivers)
        {
            foreach (var d in _library.Drivers)
            {
                var kind = d.IsImported ? DriverMatchKind.Imported : d.IsWindowsUpdate ? DriverMatchKind.ExactModel : DriverMatchKind.BrandUniversal;
                var source = d.IsImported ? "importé" : d.IsWindowsUpdate ? "Windows Update" : "téléchargé";
                drivers.Add((d.Brand, new DriverChoice(d.Name, d.InfPath, kind, source),
                    $"{source}{(d.Version is null ? "" : " · " + d.Version)}{(d.InfPath is null ? "" : " · " + Path.GetFileName(d.InfPath))}"));
            }
            foreach (var g in _catalog.InboxFallback.Append("Microsoft PCL6 Class Driver").Distinct())
                drivers.Add(("Windows (générique)", new DriverChoice(g, null, DriverMatchKind.GenericInbox, "Pilote générique Windows"), "intégré à Windows"));
        }
        else
        {
            foreach (var m in _cups.Models)
            {
                if (m.Id.StartsWith("driverless-fax", StringComparison.OrdinalIgnoreCase)) continue;
                var kind = m.IsImported ? DriverMatchKind.Imported
                    : m.Id == CupsScripts.Everywhere || m.Id.StartsWith("driverless:", StringComparison.OrdinalIgnoreCase) ? DriverMatchKind.Driverless
                    : m.Description.StartsWith("Generic", StringComparison.OrdinalIgnoreCase) ? DriverMatchKind.GenericInbox
                    : DriverMatchKind.ExactModel;
                var brand = kind is DriverMatchKind.GenericInbox ? "Génériques" : Brands.Detect(m.Description);
                if (brand == Brands.Unknown)
                {
                    var first = m.Description.Split(' ', 2)[0];
                    brand = first.Length > 1 ? char.ToUpper(first[0]) + first[1..] : "Autres";
                }
                drivers.Add((brand, new DriverChoice(m.Description, m.Id, kind, "CUPS"), m.Id));
            }
        }

        var list = new List<DriverListItem>();
        foreach (var g in drivers.GroupBy(d => d.Brand).OrderBy(g => g.Key.StartsWith("Génériques") || g.Key.StartsWith("Windows (") ? "zz" : g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var items = g.GroupBy(d => d.Choice.InfPath ?? d.Choice.DriverName).Select(x => x.First())
                         .OrderBy(d => d.Choice.DriverName, NaturalComparer.Instance).ToList();
            list.Add(new DriverListItem { IsHeader = true, Brand = g.Key, Detail = $"{items.Count} pilote(s)", SearchKey = DriverListItem.Key(g.Key) });
            list.AddRange(items.Select(d => new DriverListItem
            {
                Brand = g.Key, Title = d.Choice.DriverName, Detail = d.Detail, Choice = d.Choice,
                SearchKey = DriverListItem.Key($"{g.Key} {d.Choice.DriverName} {d.Detail}"),
            }));
        }
        return list;
    }

    private void ScheduleDriverFilter()
    {
        _filterTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => { _filterTimer!.Stop(); ApplyDriverFilter(); });
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>
    /// Liste affichée : une ligne par marque (cliquable) ; ses pilotes seulement si elle est ouverte.
    /// Pendant une recherche ou avec une seule marque filtrée, tout ce qui correspond est ouvert.
    /// </summary>
    private void ApplyDriverFilter(string? keepBrand = null)
    {
        var words = (DriverSearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(DriverListItem.Key).Where(w => w.Length > 0).ToList();
        var brand = BrandFilter.SelectedIndex > 0 ? BrandFilter.SelectedItem as string : null;
        var searching = words.Count > 0;
        var result = new List<DriverListItem>();
        var count = 0;
        DriverListItem? keep = null;
        foreach (var group in _allDrivers.Where(d => brand is null || d.Brand == brand).GroupBy(d => d.Brand))
        {
            var matches = group.Where(d => d.IsDriver && words.All(w => d.SearchKey.Contains(w))).ToList();
            if (matches.Count == 0) continue;
            // Sans recherche : ouverte seulement si on l'a ouverte. En recherche : ouverte sauf si on l'a refermée.
            var open = searching ? !_searchCollapsed.Contains(group.Key) : _expandedBrands.Contains(group.Key);
            var header = new DriverListItem
            {
                IsHeader = true, Brand = group.Key, IsExpanded = open,
                Detail = words.Count > 0 ? $"{matches.Count} pilote(s) trouvé(s)" : $"{matches.Count} pilote(s)",
            };
            result.Add(header);
            if (group.Key == keepBrand) keep = header;
            if (open) result.AddRange(matches);
            count += matches.Count;
        }
        DriverList.ItemsSource = result;
        if (keep is not null) { DriverList.SelectedItem = null; DriverList.ScrollIntoView(keep); }
        var total = _allDrivers.Count(d => d.IsDriver);
        var brands = result.Count(r => r.IsHeader);
        DriverCountText.Text = words.Count == 0 && brand is null
            ? $"{brands} fabricants · {total} pilotes sur ce poste ({_platform.Name}) — clique sur un fabricant pour voir ses pilotes"
            : $"{count} pilote(s) sur {total}{(count == 0 ? " — essaie un autre mot (juste le numéro du modèle, par exemple)" : "")}";
        UpdateUseDriverButton();
    }

    private void UpdateUseDriverButton()
    {
        var item = DriverList.SelectedItem as DriverListItem;
        UseDriverButton.IsEnabled = _current is not null && item?.Choice is not null && !_busy;
        UseDriverButton.Content = _current is null ? "Utiliser pour l'imprimante" : $"Utiliser pour {_current.TileTitle}";
        UseDriverHint.Text = _current is null
            ? "Choisis d'abord une imprimante dans « Imprimantes », puis un pilote ici (double-clic ou bouton)."
            : "Double-clic sur un pilote, ou bouton à droite, pour l'utiliser.";
    }

    private void UseSelectedDriver()
    {
        if (_current is null || DriverList.SelectedItem is not DriverListItem { Choice: { } choice }) return;
        var tile = _current;
        var option = tile.Drivers.FirstOrDefault(o => o.Choice.InfPath == choice.InfPath && o.Choice.DriverName == choice.DriverName);
        if (option is null)
        {
            // Pilote choisi à la main : ajouté en tête de la liste de la tuile, marqué comme choix du technicien
            var manual = choice with { Reason = "Choisi dans l'onglet Pilotes" };
            tile.ReplaceDrivers(new[] { manual }.Concat(tile.Drivers.Select(o => o.Choice)));
            option = tile.Drivers[0];
        }
        tile.SelectedDriver = option;
        Log($"Pilote choisi à la main pour {tile.Printer.DisplayName} : {choice.DriverName}");
        DriversTabButton.IsChecked = false;
        SetSelection(new[] { tile });
    }

    // ------------------------------------------------------------------ Divers

    private void UpdateSelectionCount()
    {
        var done = _rows.Count(r => r.State is TileState.Done or TileState.Warning);
        var outdated = _rows.Count(r => r.NeedsDriverUpdate);
        SelectionCountText.Text = _rows.Count == 0 ? "Clique sur une imprimante pour l'installer, Ctrl + clic pour en choisir plusieurs."
            : $"{_rows.Count} imprimante(s) trouvée(s){(done > 0 ? $" · {done} installée(s)" : "")}{(outdated > 0 ? $" · {outdated} pilote(s) à mettre à jour" : "")} — clic pour installer, Ctrl + clic pour en choisir plusieurs.";
    }

    private void SetScanning(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        StopButton.IsEnabled = scanning;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy && _scanCts is null;
        DetailInstallButton.IsEnabled = !busy;
        MultiInstallButton.IsEnabled = !busy;
    }

    private void Log(string message)
    {
        void Append()
        {
            LogBox.Text += $"{DateTime.Now:HH:mm:ss}  {message}\n";
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        }
        if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
    }

    private void Warn(string message)
    {
        Log(message);
        StatusText.Text = message.Split('\n')[0];
    }
}
