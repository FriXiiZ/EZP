using EFP.Core.Windows;

namespace EFP.Core.Platform;

/// <summary>Linux et macOS : installation par CUPS (lpadmin), droits administrateur via pkexec / osascript.</summary>
public sealed class CupsPlatform : IPrinterPlatform
{
    private readonly IScriptRunner _admin;
    private readonly IScriptRunner _user;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    public CupsPlatform(IScriptRunner? admin = null, IScriptRunner? user = null)
    {
        _admin = admin ?? new ShellRunner { Elevate = true };
        _user = user ?? new ShellRunner();
    }

    public string Name => OperatingSystem.IsMacOS() ? "macOS" : "Linux";
    public bool UsesWindowsDrivers => false;

    public async Task<List<InstalledPrinter>> ListInstalledAsync(CancellationToken ct = default)
    {
        var output = await _user.RunAsync(CupsScripts.ListInstalled(), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var list = new List<InstalledPrinter>();
        foreach (var (step, _, detail) in PowerShellScripts.ParseOutput(output.StdOut))
        {
            if (step != "@inst") continue;
            var f = detail.Split('\t');
            if (f.Length < 4) continue;
            list.Add(new InstalledPrinter(f[0], string.IsNullOrWhiteSpace(f[1]) ? "?" : f[1], null, f[3].Trim()));
        }
        return list;
    }

    public async Task<InstallResult> InstallAsync(DiscoveredPrinter printer, DriverChoice driver, InstallOptions options, string? name,
        IProgress<StepProgress>? progress = null, CancellationToken ct = default)
    {
        var description = string.IsNullOrWhiteSpace(name)
            ? PowerShellScripts.BuildPrinterName(printer, options)
            : PowerShellScripts.BuildPrinterName(printer, new InstallOptions { NameTemplate = name, Location = options.Location });
        var queue = CupsScripts.QueueName(description);
        var result = new InstallResult { Printer = printer, Driver = driver, WindowsName = queue };
        var script = CupsScripts.InstallPrinter(printer, driver, options, queue, description);
        var output = await _admin.RunAsync(script, Timeout, ct, progress is null ? null : line => PrinterInstaller.ReportLine(line, progress)).ConfigureAwait(false);
        PrinterInstaller.Fill(output, result.Steps, special => { if (special.Step == "@name") result.WindowsName = special.Detail; });
        result.RawOutput = output.StdOut + output.StdErr;
        return result;
    }

    public async Task<List<StepResult>> FinishAsync(string printerName, bool setDefault, bool testPage, CancellationToken ct = default)
    {
        var steps = new List<StepResult>();
        if (!setDefault && !testPage) return steps;
        var output = await _user.RunAsync(CupsScripts.Finish(printerName, setDefault, testPage), TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        PrinterInstaller.Fill(output, steps);
        return steps;
    }
}
