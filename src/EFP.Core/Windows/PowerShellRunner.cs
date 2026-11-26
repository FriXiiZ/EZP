using System.Diagnostics;
using System.Text;

namespace EFP.Core.Windows;

public sealed record ScriptOutput(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface IScriptRunner
{
    /// <param name="onLine">Appelé pour chaque ligne de sortie dès qu'elle arrive (progression en direct).</param>
    Task<ScriptOutput> RunAsync(string script, TimeSpan timeout, CancellationToken ct = default, Action<string>? onLine = null);
}

/// <summary>Exécute un script dans Windows PowerShell 5.1 (présent sur tous les Windows 10/11).</summary>
public sealed class PowerShellRunner : IScriptRunner
{
    public string Executable { get; init; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")
        : "pwsh";

    public async Task<ScriptOutput> RunAsync(string script, TimeSpan timeout, CancellationToken ct = default, Action<string>? onLine = null)
    {
        // -EncodedCommand : pas de fichier temporaire (le script peut contenir un mot de passe) et pas de souci d'accents.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            try { onLine?.Invoke(e.Data); } catch { /* l'affichage ne doit jamais casser l'installation */ }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* déjà terminé */ }
            if (!timedOut) throw;
        }
        proc.WaitForExit(); // vide les tampons de sortie

        return new ScriptOutput(timedOut ? -1 : proc.ExitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }
}
