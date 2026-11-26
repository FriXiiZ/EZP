using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EFP.Core;
using EFP.Core.Discovery;
using EFP.Core.Windows;

namespace EFP.Desktop;

public enum TileState { Ready, Working, Done, Warning, Failed }

public sealed class DriverOption
{
    public required DriverChoice Choice { get; init; }

    public string Display => Choice.Kind switch
    {
        DriverMatchKind.Imported => $"★ {Choice.DriverName} (importé)",
        DriverMatchKind.Driverless => $"✔ {Choice.DriverName}",
        DriverMatchKind.ExactModel => $"✔ {Choice.DriverName}",
        DriverMatchKind.BrandUniversal => $"◆ {Choice.DriverName}",
        _ => $"⚠ {Choice.DriverName} (générique)",
    };

    public override string ToString() => Display;
}

/// <summary>Une tuile d'imprimante.</summary>
public sealed class PrinterTile : INotifyPropertyChanged
{
    private static readonly Bitmap PrinterIcon = new(AssetLoader.Open(new Uri("avares://EZP/Assets/printer.png")));
    private static readonly Bitmap DeviceIcon = new(AssetLoader.Open(new Uri("avares://EZP/Assets/device.png")));

    private DriverOption? _driver;
    private string _status = "";
    private string _windowsName = "";
    private bool _isCurrent, _spinning, _working, _outdated;
    private double _progress;
    private TileState _state = TileState.Ready;
    private InstalledPrinter? _installed;
    private string? _latestVersion;

    public PrinterTile(DiscoveredPrinter printer, IEnumerable<DriverChoice> candidates)
    {
        Printer = printer;
        Drivers = candidates.Select(c => new DriverOption { Choice = c }).ToList();
        _driver = Drivers.FirstOrDefault();
    }

    public DiscoveredPrinter Printer { get; }
    public List<DriverOption> Drivers { get; private set; }

    public void ReplaceDrivers(IEnumerable<DriverChoice> candidates)
    {
        Drivers = candidates.Select(c => new DriverOption { Choice = c }).ToList();
        OnChanged(nameof(Drivers));
        SelectedDriver = Drivers.FirstOrDefault();
    }

    public DriverOption? SelectedDriver
    {
        get => _driver;
        set { Set(ref _driver, value); OnChanged(nameof(StateText)); OnChanged(nameof(DriverNote)); }
    }

    // ---------------- Nom et IP (modifiables par double-clic) ----------------

    public string WindowsName
    {
        get => _windowsName;
        set
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0 || v == _windowsName) { OnChanged(); return; }
            _windowsName = v;
            NameEditedByHand = true;
            OnChanged();
        }
    }

    public bool NameEditedByHand { get; private set; }

    public void SetDefaultName(string name)
    {
        if (NameEditedByHand) return;
        _windowsName = name;
        OnChanged(nameof(WindowsName));
    }

    public string Ip
    {
        get => Printer.IpAddress;
        set
        {
            var v = (value ?? "").Trim();
            if (IpRange.TryParseV4(v, out var ip) && ip.ToString() != Printer.IpAddress)
            {
                var old = Printer.IpAddress;
                Printer.IpAddress = ip.ToString();
                IpChanged?.Invoke(this, old);
            }
            OnChanged(nameof(TileTitle)); OnChanged(nameof(TileSubtitle)); OnChanged();
        }
    }

    public event Action<PrinterTile, string>? IpChanged;

    // ---------------- Infos ----------------

    public string Brand => Printer.Brand;
    public string Serial => Printer.SerialNumber ?? "—";
    public string Mac => Printer.MacAddress ?? "—";
    public string SerialAndMac => $"{Serial}  ·  {Mac}";
    public string Tooltip =>
        $"MAC : {Printer.MacAddress ?? "—"} · ports : {(Printer.OpenPorts.Count == 0 ? "—" : string.Join(", ", Printer.OpenPorts))}\n" +
        $"SNMP : {(Printer.SnmpResponded ? "oui" : "non")} · {Printer.SysDescr ?? ""}";

    public string TileTitle => string.IsNullOrWhiteSpace(Printer.Model) ? Printer.IpAddress : Printer.Model!;
    public string TileSubtitle => string.IsNullOrWhiteSpace(Printer.Model) ? Brand : $"{Brand} · {Printer.IpAddress}";

    /// <summary>Vraie imprimante (SNMP, marque, modèle ou port 9100) : icône en couleurs ; sinon grise.</summary>
    public bool IsConfirmedPrinter => Printer.Brand != Brands.Unknown || !string.IsNullOrWhiteSpace(Printer.Model) || Printer.HasRawPort;
    public Bitmap Icon => IsConfirmedPrinter ? PrinterIcon : DeviceIcon;

    // ---------------- État de la tuile ----------------

    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    public bool Spinning
    {
        get => _spinning;
        set { Set(ref _spinning, value); OnChanged(nameof(StateText)); }
    }

    public bool Working { get => _working; set => Set(ref _working, value); }

    public TileState State
    {
        get => _state;
        set
        {
            Set(ref _state, value);
            foreach (var n in new[] { nameof(StateText), nameof(IsDone), nameof(IsWarning), nameof(IsFailed), nameof(HasBadge), nameof(DriverNote) })
                OnChanged(n);
        }
    }

    public bool IsDone => _state == TileState.Done;
    public bool IsWarning => _state == TileState.Warning;
    public bool IsFailed => _state == TileState.Failed;
    public bool HasBadge => _state is TileState.Done or TileState.Warning or TileState.Failed;

    public double Progress
    {
        get => _progress;
        set
        {
            Set(ref _progress, Math.Clamp(value, 0, 1));
            OnChanged(nameof(SweepAngle)); OnChanged(nameof(HasProgress));
        }
    }

    public double SweepAngle => _progress * 360;
    public bool HasProgress => _progress > 0.001;

    public string Status
    {
        get => _status;
        set { Set(ref _status, value); OnChanged(nameof(StateText)); }
    }

    public string StatusDetail { get; set; } = "";

    public string StateText => _spinning && !string.IsNullOrEmpty(_status) ? _status : _state switch
    {
        TileState.Working => string.IsNullOrEmpty(_status) ? "Installation…" : _status,
        TileState.Done => "Installée",
        TileState.Warning => "Installée (remarques)",
        TileState.Failed => "Échec — voir le journal",
        _ when _outdated => "Pilote à mettre à jour",
        _ when _installed is not null => "Déjà installée ici",
        _ => "Prête à installer",
    };

    public string DriverNote =>
        OperatingSystem.IsWindows() && SelectedDriver?.Choice.Kind == DriverMatchKind.GenericInbox && Printer.Brand != Brands.Unknown && _state == TileState.Ready
            ? $"Le pilote {Printer.Brand} sera téléchargé au moment d'installer (le générique ne sert que s'il est introuvable)."
            : "";

    // ---------------- Déjà sur ce PC ? ----------------

    public InstalledPrinter? InstalledHere => _installed;
    public bool IsInstalledHere => _installed is not null;
    public bool NeedsDriverUpdate => _outdated;
    public bool IsUpToDateHere => _installed is not null && !_outdated;

    public void SetInstalledHere(InstalledPrinter? installed, bool outdated, string? latestVersion)
    {
        _installed = installed; _outdated = outdated && installed is not null; _latestVersion = latestVersion;
        foreach (var n in new[] { nameof(InstalledHere), nameof(IsInstalledHere), nameof(NeedsDriverUpdate), nameof(IsUpToDateHere),
                                  nameof(HereText), nameof(DotTooltip), nameof(InstallLabel), nameof(StateText) })
            OnChanged(n);
    }

    public string HereText => _installed is null ? "Pas encore installée"
        : $"« {_installed.Name} » — {_installed.DriverName}{(_installed.DriverVersion is null ? "" : " v" + _installed.DriverVersion)}"
          + (_outdated ? (_latestVersion is null ? "  →  à remplacer par le pilote de la marque" : $"  →  v{_latestVersion} disponible") : "");

    public string DotTooltip => _outdated ? "Pilote à mettre à jour sur ce poste" : "Déjà installée sur ce poste, pilote à jour";
    public string InstallLabel => _outdated ? "Mettre à jour le pilote" : _installed is not null ? "Réinstaller" : "Installer";

    // ---------------- INotifyPropertyChanged ----------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }
}
