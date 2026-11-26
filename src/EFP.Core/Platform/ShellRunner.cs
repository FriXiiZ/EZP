using System.Diagnostics;
using System.Text;
using EFP.Core.Windows;

namespace EFP.Core.Platform;

/// <summary>
/// Exécute un script sh. Avec <see cref="Elevate"/>, il passe par pkexec (Linux, fenêtre de mot de passe du bureau)
/// ou osascript « with administrator privileges » (macOS), sauf si on est déjà root.
/// </summary>
public sealed class ShellRunner : IScriptRunner
{
    public bool Elevate { get; init; }

    public static bool IsRoot => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    public async Task<ScriptOutput> RunAsync(string script, TimeSpan timeout, CancellationToken ct = default, Action<string>? onLine = null)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ezp-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(file, script, new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            ProcessStartInfo psi;
            if (!Elevate || IsRoot)
                psi = new ProcessStartInfo("/bin/sh") { ArgumentList = { file } };
            else if (OperatingSystem.IsMacOS())
                psi = new ProcessStartInfo("/usr/bin/osascript")
                {
                    ArgumentList = { "-e", $"do shell script \"/bin/sh '{file}' 2>&1\" with administrator privileges with prompt \"{Loc.T("EZP doit installer une imprimante.", "EZP needs to install a printer.")}\"" },
                };
            else
                psi = new ProcessStartInfo("pkexec") { ArgumentList = { "/bin/sh", file } };

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.Environment["LC_ALL"] = "C";

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                // osascript renvoie les fins de ligne en \r
                foreach (var line in e.Data.Split('\r'))
                {
                    lock (stdout) stdout.AppendLine(line);
                    try { onLine?.Invoke(line); } catch { }
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var timedOut = false;
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { proc.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
            }
            proc.WaitForExit();
            var err = stderr.ToString();
            // pkexec : 126 = mot de passe refusé / fenêtre fermée, 127 = pas d'agent d'authentification
            if (proc.ExitCode is 126 or 127 && Elevate && !IsRoot && stdout.Length == 0)
                err = Loc.T("Autorisation administrateur refusée ou impossible (mot de passe annulé ?). ", "Administrator authorization denied or unavailable (password cancelled?). ") + err;
            return new ScriptOutput(timedOut ? -1 : proc.ExitCode, stdout.ToString(), err, timedOut);
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Lance une commande simple et renvoie sa sortie (sans élévation).</summary>
    public static async Task<string> CaptureAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        using var proc = Process.Start(psi)!;
        var read = proc.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw new TimeoutException($"{exe} " + Loc.T("ne répond pas", "is not responding")); }
        return await read.ConfigureAwait(false);
    }
}
