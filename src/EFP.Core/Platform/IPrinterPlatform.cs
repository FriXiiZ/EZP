using EFP.Core.Windows;

namespace EFP.Core.Platform;

/// <summary>
/// Ce qui change d'un système à l'autre : installer une imprimante, lister celles déjà présentes,
/// la mettre par défaut, imprimer une page de test. Windows → PowerShell ; Linux et macOS → CUPS.
/// </summary>
public interface IPrinterPlatform
{
    /// <summary>« Windows », « Linux » ou « macOS ».</summary>
    string Name { get; }

    /// <summary>true sous Windows : pilotes .inf, téléchargements des constructeurs et Windows Update.</summary>
    bool UsesWindowsDrivers { get; }

    Task<List<InstalledPrinter>> ListInstalledAsync(CancellationToken ct = default);

    Task<InstallResult> InstallAsync(DiscoveredPrinter printer, DriverChoice driver, InstallOptions options, string? name,
        IProgress<StepProgress>? progress = null, CancellationToken ct = default);

    Task<List<StepResult>> FinishAsync(string printerName, bool setDefault, bool testPage, CancellationToken ct = default);
}

public static class PrinterPlatforms
{
    public static IPrinterPlatform Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsPlatform() : new CupsPlatform();
}

/// <summary>Windows : le moteur PowerShell existant.</summary>
public sealed class WindowsPlatform : IPrinterPlatform
{
    private readonly PrinterInstaller _installer;
    public WindowsPlatform(IScriptRunner? runner = null) => _installer = new PrinterInstaller(runner ?? new PowerShellRunner());

    public string Name => "Windows";
    public bool UsesWindowsDrivers => true;

    public Task<List<InstalledPrinter>> ListInstalledAsync(CancellationToken ct = default) => _installer.ListInstalledAsync(ct);

    public Task<InstallResult> InstallAsync(DiscoveredPrinter printer, DriverChoice driver, InstallOptions options, string? name,
        IProgress<StepProgress>? progress = null, CancellationToken ct = default)
        => _installer.InstallAsync(printer, driver, options, name, progress, ct);

    public Task<List<StepResult>> FinishAsync(string printerName, bool setDefault, bool testPage, CancellationToken ct = default)
        => _installer.FinishAsync(printerName, setDefault, testPage, ct);
}
