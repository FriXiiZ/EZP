using System.Diagnostics;
using System.Security.Principal;
using Avalonia;

namespace EFP.Desktop;

internal static class Program
{
    public static string BaseFolder => AppContext.BaseDirectory;

    /// <summary>
    /// Pilotes, réglages et journal d'erreurs. Par défaut dans le dossier de données de l'utilisateur
    /// (%LOCALAPPDATA%\EZP, ~/.local/share/EZP, ~/Library/Application Support/EZP) : l'exe reste seul.
    /// Mode clé USB : s'il y a un dossier « Drivers » à côté de l'exe, tout reste à côté de lui.
    /// </summary>
    public static string DataFolder { get; } = ResolveDataFolder();

    private static string ResolveDataFolder()
    {
        try
        {
            var exeDir = ExeFolder;
            if (Directory.Exists(Path.Combine(exeDir, "Drivers"))) return exeDir;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            var local = Path.Combine(root, "EZP");
            // Ancien nom (EFP) : on reprend ses pilotes et réglages
            var old = Path.Combine(root, "EFP");
            if (!Directory.Exists(local) && Directory.Exists(old))
                try { Directory.Move(old, local); } catch { }
            Directory.CreateDirectory(local);
            return local;
        }
        catch { return BaseFolder; }
    }

    /// <summary>Dossier de l'exe (ou de l'AppImage / du .app), pas le dossier temporaire d'extraction.</summary>
    public static string ExeFolder
    {
        get
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(appImage)) return Path.GetDirectoryName(appImage)!;
            var exe = Environment.ProcessPath;
            if (exe is null) return BaseFolder;
            var dir = Path.GetDirectoryName(exe)!;
            // macOS : EZP.app/Contents/MacOS/EZP → dossier qui contient EZP.app
            if (OperatingSystem.IsMacOS() && dir.EndsWith("/Contents/MacOS"))
                return Path.GetFullPath(Path.Combine(dir, "..", "..", ".."));
            return dir;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };

        // Windows : le manifeste demande déjà les droits administrateur ; filet de sécurité si l'exe est lancé autrement
        if (OperatingSystem.IsWindows() && !IsWindowsAdministrator() && !args.Contains("--no-elevate"))
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--no-elevate") { UseShellExecute = true, Verb = "runas" });
                return 0;
            }
            catch { /* refusé : on continue sans droits, l'interface le signalera */ }
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions
        {
            // Rendu logiciel si EZP_SOFTWARE=1 (vieux pilotes graphiques, machines virtuelles, Wine)
            RenderingMode = Environment.GetEnvironmentVariable("EZP_SOFTWARE") == "1"
                ? new[] { Win32RenderingMode.Software }
                : new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
            CompositionMode = Environment.GetEnvironmentVariable("EZP_SOFTWARE") == "1"
                ? new[] { Win32CompositionMode.RedirectionSurface }
                : new[] { Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface },
        })
        .WithInterFont()
        .LogToTrace();

    public static bool IsWindowsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    /// <summary>Peut-on installer ? Windows : administrateur. Linux/macOS : le mot de passe sera demandé au moment voulu.</summary>
    public static bool CanInstall => !OperatingSystem.IsWindows() || IsWindowsAdministrator();

    public static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try { File.AppendAllText(Path.Combine(DataFolder, "EZP-erreurs.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n"); }
        catch { }
    }
}
