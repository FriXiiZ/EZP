namespace EFP.Core.Windows;

public sealed class PrinterInstaller
{
    private readonly IScriptRunner _runner;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(6);

    public PrinterInstaller(IScriptRunner runner) => _runner = runner;

    public async Task<InstallResult> InstallAsync(DiscoveredPrinter printer, DriverChoice driver, InstallOptions options, CancellationToken ct = default)
        => await InstallAsync(printer, driver, options, null, null, ct).ConfigureAwait(false);

    /// <param name="nameOverride">Nom choisi à la main dans le tableau (sinon : modèle de nom des options).</param>
    /// <param name="progress">Étapes en direct : Success=false + Detail=null signifie « étape en cours ».</param>
    public async Task<InstallResult> InstallAsync(DiscoveredPrinter printer, DriverChoice driver, InstallOptions options, string? nameOverride,
        IProgress<StepProgress>? progress = null, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(nameOverride)
            ? PowerShellScripts.BuildPrinterName(printer, options)
            : PowerShellScripts.BuildPrinterName(printer, new InstallOptions { NameTemplate = nameOverride, Location = options.Location });
        var result = new InstallResult { Printer = printer, Driver = driver, WindowsName = name };
        var script = PowerShellScripts.InstallPrinter(printer, driver, options, name);
        var output = await _runner.RunAsync(script, Timeout, ct, progress is null ? null : line => ReportLine(line, progress)).ConfigureAwait(false);
        Fill(output, result.Steps, special =>
        {
            if (special.Step == "@name") result.WindowsName = special.Detail;
            if (special.Step == "@share") result.ShareUnc = special.Detail;
        });
        result.RawOutput = output.StdOut + output.StdErr;
        return result;
    }

    /// <summary>Par défaut et/ou page de test sur une imprimante déjà installée.</summary>
    public async Task<List<StepResult>> FinishAsync(string printerName, bool setDefault, bool testPage, CancellationToken ct = default)
    {
        var steps = new List<StepResult>();
        if (!setDefault && !testPage) return steps;
        var output = await _runner.RunAsync(PowerShellScripts.FinishPrinter(printerName, setDefault, testPage), TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        Fill(output, steps);
        return steps;
    }

    /// <summary>Imprimantes réseau déjà installées sur ce PC (pour repérer les pilotes à mettre à jour).</summary>
    public async Task<List<InstalledPrinter>> ListInstalledAsync(CancellationToken ct = default)
    {
        var output = await _runner.RunAsync(PowerShellScripts.ListInstalledPrinters(), TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
        var list = new List<InstalledPrinter>();
        foreach (var (step, _, detail) in PowerShellScripts.ParseOutput(output.StdOut))
        {
            if (step != "@inst") continue;
            var f = detail.Split('\t');
            if (f.Length < 4) continue;
            list.Add(new InstalledPrinter(f[0], f[1], string.IsNullOrWhiteSpace(f[2]) ? null : f[2], f[3].Trim()));
        }
        return list;
    }

    internal static void ReportLine(string line, IProgress<StepProgress> progress)
    {
        foreach (var (step, status, detail) in PowerShellScripts.ParseOutput(line))
        {
            if (step.StartsWith('@')) continue;
            progress.Report(new StepProgress(step, status, detail));
        }
    }

    internal static void Fill(ScriptOutput output, List<StepResult> steps, Action<(string Step, string Detail)>? onSpecial = null)
    {
        foreach (var (step, status, detail) in PowerShellScripts.ParseOutput(output.StdOut))
        {
            if (status == "START") continue;
            if (step.StartsWith('@')) { onSpecial?.Invoke((step, detail)); continue; }
            steps.Add(new StepResult
            {
                Step = step,
                Success = status != "FAIL",
                IsWarning = status == "WARN",
                Detail = detail,
            });
        }
        if (output.TimedOut)
            steps.Add(new StepResult { Step = "Délai", Success = false, Detail = Loc.T("le script n'a pas répondu à temps", "the script did not respond in time") });
        else if (steps.Count == 0)
            steps.Add(new StepResult
            {
                Step = "PowerShell",
                Success = false,
                Detail = FirstLine(output.StdErr) ?? Loc.T($"aucun résultat (code {output.ExitCode})", $"no result (code {output.ExitCode})"),
            });
        else if (output.ExitCode != 0 && steps.All(s => s.Success))
            steps.Add(new StepResult { Step = "PowerShell", Success = false, Detail = FirstLine(output.StdErr) ?? Loc.T($"code de sortie {output.ExitCode}", $"exit code {output.ExitCode}") });
    }

    private static string? FirstLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}

/// <summary>Étape en direct : Status = START (en cours), OK, WARN ou FAIL.</summary>
/// <summary>Imprimante déjà présente dans Windows.</summary>
public sealed record InstalledPrinter(string Name, string DriverName, string? DriverVersion, string Ip);

public sealed record StepProgress(string Step, string Status, string Detail);

public sealed class ScanFolderInstaller
{
    private readonly IScriptRunner _runner;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    public ScanFolderInstaller(IScriptRunner runner) => _runner = runner;

    public async Task<ScanFolderResult> CreateAsync(ScanFolderOptions options, CancellationToken ct = default)
    {
        Validate(options);
        var output = await _runner.RunAsync(PowerShellScripts.CreateScanFolder(options), Timeout, ct).ConfigureAwait(false);
        string computer = Environment.MachineName;
        var tmp = new List<StepResult>();
        PrinterInstaller.Fill(output, tmp, special => { if (special.Step == "@computer") computer = special.Detail; });
        var result = new ScanFolderResult { Options = options, ComputerName = computer, RawOutput = output.StdOut + output.StdErr };
        result.Steps.AddRange(tmp);
        result.Warnings.AddRange(tmp.Where(s => s.IsWarning).Select(s => $"{s.Step} : {s.Detail}"));
        return result;
    }

    /// <summary>Vérifie les saisies avant d'envoyer quoi que ce soit à Windows.</summary>
    public static void Validate(ScanFolderOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.FolderPath) || !System.Text.RegularExpressions.Regex.IsMatch(o.FolderPath, @"^[A-Za-z]:\\"))
            throw new ArgumentException("Le dossier doit être un chemin local complet, par ex. C:\\Scans.");
        if (o.FolderPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || o.FolderPath.Contains('"'))
            throw new ArgumentException("Le chemin du dossier contient des caractères invalides.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(o.ShareName ?? "", @"^[A-Za-z0-9_\-\.]{1,40}\$?$"))
            throw new ArgumentException("Nom de partage invalide (lettres, chiffres, - _ . uniquement).");
        if (!System.Text.RegularExpressions.Regex.IsMatch(o.UserName ?? "", @"^[A-Za-z0-9_\-\.]{1,20}$"))
            throw new ArgumentException("Nom d'utilisateur invalide (20 caractères max, lettres, chiffres, - _ .).");
        if (string.IsNullOrEmpty(o.Password) || o.Password.Length < 8)
            throw new ArgumentException("Le mot de passe doit faire au moins 8 caractères.");
    }
}
