using EFP.Core.Platform;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EFP.Core;
using EFP.Core.Drivers;
using EFP.Core.Updates;
using static EFP.Tests.Assert;

namespace EFP.Tests;

/// <summary>Faux serveur HTTP : chaque requête est confiée à une fonction de test.</summary>
public sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add($"{request.Method} {request.RequestUri}");
        return Task.FromResult(respond(request));
    }
}

public static class UpdateTests
{
    public const string HpScript = """
        $ErrorActionPreference = 'Stop'
        $version = $env:ChocolateyPackageVersion
        $packageArgs = @{
          packageName    = $env:ChocolateyPackageName
          url            = "https://ftp.example.com/UPD/upd-pcl6-win10-x64-${version}.zip"
          checksum       = '0D6DE14AE6FD549B6028AC38231B625B96F6A525A585950016AD295D97E10210'
          checksumType   = 'sha256'
          url64          = $url
          checksum64     = $checksum
        }
        """;

    public const string KmScript = """
        $PCL = 'https://dl.example.com/KM_UPD_PCL_3.9.821.1.zip'
        $PCL_checksum = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
        $PS = 'https://dl.example.com/KM_UPD_PS_3.9.821.1.zip'
        $PS_checksum = 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB'
        """;

    public static byte[] Nupkg(string id, string version, string script)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open()))
                w.Write($"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd\"><metadata><id>{id}</id><version>{version}</version></metadata></package>");
            using (var w = new StreamWriter(zip.CreateEntry("tools/chocolateyinstall.ps1").Open())) w.Write(script);
        }
        return ms.ToArray();
    }

    public static byte[] DriverZip(string driverName, string? evilEntry = null)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("x64/hpcu.inf").Open()))
                w.Write($"[Version]\nClass=Printer\nProvider=HP\n[Manufacturer]\n\"HP\"=HP,NTamd64\n[HP.NTamd64]\n\"{driverName}\" = U\n");
            using (var w = new StreamWriter(zip.CreateEntry("x64/readme.txt").Open())) w.Write("ok");
            if (evilEntry is not null)
                using (var w = new StreamWriter(zip.CreateEntry(evilEntry).Open())) w.Write("pwned");
        }
        return ms.ToArray();
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static string Temp()
    {
        var d = Path.Combine(Path.GetTempPath(), "efp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static HttpResponseMessage Bytes(byte[] b, string? lastModified = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        r.Content.Headers.ContentLength = b.Length;
        if (lastModified is not null) r.Content.Headers.LastModified = DateTimeOffset.Parse(lastModified);
        return r;
    }

    /// <summary>Petit "internet" simulé : un paquet Chocolatey HP dont on peut changer la version.</summary>
    sealed class FakeHp
    {
        public string Version = "8.1.0.26560";
        public bool CorruptZip;
        public string? EvilEntry;
        public byte[] Zip => DriverZip($"HP Universal Printing PCL 6 (v{Version})", EvilEntry);
        public HttpResponseMessage Respond(HttpRequestMessage req)
        {
            var u = req.RequestUri!.ToString();
            if (u.StartsWith("https://choco.test/api/hp-universal-print-driver-pcl"))
            {
                var zip = Zip;
                var hash = CorruptZip ? new string('0', 64) : Sha(zip);
                var script = HpScript.Replace("https://ftp.example.com", "https://ftp.hp.test").Replace("0D6DE14AE6FD549B6028AC38231B625B96F6A525A585950016AD295D97E10210", hash);
                return Bytes(Nupkg("hp-universal-print-driver-pcl", Version, script));
            }
            if (u == $"https://ftp.hp.test/UPD/upd-pcl6-win10-x64-{Version}.zip") return Bytes(Zip);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    static DriverFeed HpFeed() => new()
    {
        Packages = { new() { Id = "hp-upd-pcl6", Brand = "HP", Name = "HP UPD", Sources = { new() { Type = "chocolatey", Package = "hp-universal-print-driver-pcl" } }, Page = "https://support.hp.test" } },
    };

    static DriverUpdater Updater(string root, FakeHandler h, string? sevenZip = null)
    {
        var http = new HttpClient(h);
        return new DriverUpdater(root, http, new FeedResolver(http) { ChocolateyBase = "https://choco.test/api/" }) { SevenZipPath = sevenZip };
    }

    static DriverFeed ProvisionFeed() => new()
    {
        Packages =
        {
            new() { Id = "hp-ps", Brand = "HP", Name = "HP UPD PS", Sources = { new() { Type = "chocolatey", Package = "hp-ps" } }, Page = "https://hp.test" },
            new() { Id = "hp-pcl", Brand = "HP", Name = "HP UPD PCL6", Fallback = true, Sources = { new() { Type = "chocolatey", Package = "hp-pcl" } }, Page = "https://hp.test" },
        },
    };

    static HttpResponseMessage ProvisionServer(HttpRequestMessage req, bool psOk)
    {
        var u = req.RequestUri!.ToString();
        byte[] Zip(string name) => DriverZip(name);
        if (u.EndsWith("/api/hp-ps"))
            return psOk ? Bytes(Nupkg("hp-ps", "8.2.0", $"$url = 'https://ftp.hp.test/ps.zip'\n$checksum = '{Sha(Zip("HP Universal Printing PS (v8.2.0)"))}'"))
                        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (u.EndsWith("/api/hp-pcl"))
            return Bytes(Nupkg("hp-pcl", "8.2.0", $"$url = 'https://ftp.hp.test/pcl.zip'\n$checksum = '{Sha(Zip("HP Universal Printing PCL 6 (v8.2.0)"))}'"));
        if (u == "https://ftp.hp.test/ps.zip") return Bytes(Zip("HP Universal Printing PS (v8.2.0)"));
        if (u == "https://ftp.hp.test/pcl.zip") return Bytes(Zip("HP Universal Printing PCL 6 (v8.2.0)"));
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static void Register(Action<string, Func<Task>> Test, Action<string, Action> TestSync)
    {

        TestSync("Windows Update : lecture du catalogue et choix du paquet", () =>
        {
            static string Row(string id, int r, string title, string products, string cls, string date, long size) => $"""
                <tr id="{id}_R{r}"><td id="{id}_C0_R{r}"></td><td class="x" id="{id}_C1_R{r}"><a id='{id}_link'> {title} </a></td>
                <td class="x" id="{id}_C2_R{r}"> {products} </td><td class="x" id="{id}_C3_R{r}"> {cls} </td>
                <td class="x " id="{id}_C4_R{r}"> {date} </td><td class="x" id="{id}_C5_R{r}"> n/a </td>
                <td class="x" id="{id}_C6_R{r}"><span id="{id}_size">21.4 MB</span><span class="noDisplay" id="{id}_originalSize">{size}</span></td></tr>
                """;
            var html = "<table>"
                + Row("be064afd-0492-49b2-8589-715337385b8d", 0, "Brother - Printer - 1.5.0.0", "Windows Vista, Windows XP", "Drivers (Printers)", "10/19/2023", 22393767)
                + Row("8b9c950f-5a62-4570-a789-a45973beead2", 1, "Brother - Printer - 1.5.0.0", "Windows 11 Client, version 22H2 and later, Servicing Drivers", "Drivers (Printers)", "10/19/2023", 22393767)
                + Row("c9c19cd1-89ff-49df-9372-4032812956a7", 2, "Brother - Printer - 1.5.0.0", "Windows 10, Vibranium and later, Servicing Drivers", "Drivers (Printers)", "10/19/2023", 22393767)
                + Row("c5d68c77-bc45-4b43-8339-8e3094b5c74c", 3, "Brother - Image - 1.0.18.1", "Windows 11 Client, version 22H2 and later", "Drivers (Other Hardware)", "9/11/2023", 10926506)
                + "</table>";
            var entries = WindowsUpdateCatalog.ParseSearch(html);
            Equal(4, entries.Count, "4 lignes lues");
            Equal("Brother - Printer - 1.5.0.0", entries[1].Title, "titre nettoyé");
            Equal(22393767L, entries[1].Size, "taille exacte");
            var best = WindowsUpdateCatalog.PickBest(entries);
            Equal("8b9c950f-5a62-4570-a789-a45973beead2", best?.Id, "imprimante, Windows 11 (ni XP/Vista ni scanner)");

            var dialog = "downloadInformation[0].files[0].url = 'https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2023/11/91988898-8f1d-4ac6-aa00-033c8a3f6196_6ed6939b5be63d07f83c748c6671c8a29ac66f1e.cab';"
                       + " other = 'https://evil.example.com/x_0000000000000000000000000000000000000000.cab';";
            var urls = WindowsUpdateCatalog.ParseDownloadUrls(dialog);
            Equal(1, urls.Count, "seuls les serveurs Microsoft sont acceptés");
            Equal("6ed6939b5be63d07f83c748c6671c8a29ac66f1e", WindowsUpdateCatalog.ExpectedSha1(urls[0]), "empreinte lue dans le nom");
        });

        Test("Windows Update : paquet altéré refusé", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "efp-wu-" + Guid.NewGuid().ToString("N"));
            var cabUrl = "https://catalog.s.download.windowsupdate.com/d/x_6ed6939b5be63d07f83c748c6671c8a29ac66f1e.cab";
            var handler = new FakeHandler(req =>
            {
                var u = req.RequestUri!.ToString();
                if (u.Contains("Search.aspx")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    """<tr id="8b9c950f-5a62-4570-a789-a45973beead2_R0"><td id="8b9c950f-5a62-4570-a789-a45973beead2_C1_R0">Brother - Printer - 1.5.0.0</td><td id="8b9c950f-5a62-4570-a789-a45973beead2_C2_R0">Windows 11 Client</td><td id="8b9c950f-5a62-4570-a789-a45973beead2_C3_R0">Drivers (Printers)</td><td id="8b9c950f-5a62-4570-a789-a45973beead2_C4_R0">10/19/2023</td></tr>""") };
                if (u.Contains("DownloadDialog")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"url = '{cabUrl}';") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("pas le bon fichier")) };
            });
            try
            {
                var wu = new WindowsUpdateCatalog(new HttpClient(handler));
                var threw = false;
                try { await wu.FetchAsync(root, "Brother", "MFC-L3760CDW", new DriverUpdater(root, new HttpClient(handler))); }
                catch (InvalidDataException) { threw = true; }
                Equal(true, threw, "empreinte SHA-1 vérifiée");
                Equal(false, Directory.Exists(Path.Combine(root, "Brother", "wu-MFC-L3760CDW")), "rien n'est rangé");
                Equal(true, handler.Requests.Any(r => r.StartsWith("POST") && r.Contains("DownloadDialog")), "fenêtre de téléchargement demandée");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

        TestSync("CUPS : choix des pilotes (sans pilote, modèle, génériques)", () =>
        {
            var src = new CupsDriverSource();
            src.SetModels(CupsDriverSource.ParseLpinfo("""
                everywhere IPP Everywhere
                drv:///sample.drv/generic.ppd Generic PostScript Printer
                drv:///sample.drv/generpcl.ppd Generic PCL Laser Printer
                drv:///hpcups.drv/hp-officejet_pro_8120e.ppd HP OfficeJet Pro 8120e, hpcups 3.23
                drv:///hpcups.drv/hp-laserjet_m404.ppd HP LaserJet M404, hpcups 3.23
                lsb/usr/brlaser/brother-MFC-L3760CDW.ppd Brother MFC-L3760CDW, using brlaser v6
                """));
            var fedora = new CupsDriverSource();
            fedora.SetModels(CupsDriverSource.ParseLpinfo("""
                driverless:ipps://Brother%20MFC-L3760CDW%20series._ipps._tcp.local/ Brother MFC-L3760CDW series, driverless, cups-filters 2.0.1
                driverless-fax:ipps://Brother%20MFC-L3760CDW%20series._ipps._tcp.local/ Brother MFC-L3760CDW series, Fax, driverless, cups-filters 2.0.1
                drv:///sample.drv/generic.ppd Generic PostScript Printer
                """));
            var broF = new DiscoveredPrinter { IpAddress = "192.168.100.190", Brand = "Brother", Model = "MFC-L3760CDW" };
            broF.OpenPorts.AddRange(new[] { 9100, 631 });
            var cf = fedora.Candidates(broF);
            Console.WriteLine("      " + string.Join(" | ", cf.Select(x => $"{x.Kind}:{x.DriverName}")));
            Equal(true, cf.Any(x => x.InfPath!.StartsWith("driverless:ipps://Brother")), "pilote sans pilote généré par CUPS pour ce modèle proposé");
            Equal(false, cf.Any(x => x.InfPath!.StartsWith("driverless-fax")), "pas la file fax");
            var hp = new DiscoveredPrinter { IpAddress = "192.168.1.41", Brand = "HP", Model = "OfficeJet Pro 8120e" };
            hp.OpenPorts.AddRange(new[] { 9100, 631 });
            var c = src.Candidates(hp);
            Equal(DriverMatchKind.Driverless, c[0].Kind, "IPP : sans pilote en premier");
            Equal("drv:///hpcups.drv/hp-officejet_pro_8120e.ppd", c[1].InfPath, "puis le PPD du modèle");
            Equal(false, c.Any(x => x.InfPath!.Contains("m404")), "pas un autre modèle");
            Equal(CupsScripts.GenericPostScript, c.First(x => x.Kind == DriverMatchKind.GenericInbox).InfPath, "générique PS avant PCL");

            var bro = new DiscoveredPrinter { IpAddress = "192.168.100.190", Brand = "Brother", Model = "MFC-L3760CDW" };
            bro.OpenPorts.Add(9100);
            Equal("lsb/usr/brlaser/brother-MFC-L3760CDW.ppd", src.Candidates(bro)[0].InfPath, "sans IPP : PPD du modèle");
            Equal("socket://192.168.100.190:9100", CupsScripts.DeviceUri(bro, src.Candidates(bro)[0]), "port 9100");
            Equal("Brother_MFC-L3760CDW", CupsScripts.QueueName("Brother MFC-L3760CDW"), "nom de file CUPS");
            Equal("Compta_HP_1", CupsScripts.QueueName("Compta: HP/1"), "caractères interdits");
        });

        if (File.Exists("/usr/sbin/lpadmin") && Environment.UserName == "root" && Environment.GetEnvironmentVariable("EFP_CUPS") == "1")
            Test("CUPS (réel) : installer, lister, remplacer, par défaut, supprimer", async () =>
            {
                var platform = new CupsPlatform();
                var p = new DiscoveredPrinter { IpAddress = "127.0.0.77", Brand = "HP", Model = "LaserJet M404dn" };
                p.OpenPorts.Add(9100);
                var driver = new DriverChoice("PostScript générique", CupsScripts.GenericPostScript, DriverMatchKind.GenericInbox, "test");
                var r = await platform.InstallAsync(p, driver, new InstallOptions { Location = "Bureau 2" }, "Compta: HP M404");
                foreach (var s in r.Steps) Console.WriteLine($"      {s.Status} {s.Step} : {s.Detail}");
                Equal(true, r.Success, "installée");
                Equal("Compta_HP_M404", r.WindowsName, "nom de file");
                var list = await platform.ListInstalledAsync();
                Equal(true, list.Any(i => i.Name == "Compta_HP_M404" && i.Ip == "127.0.0.77"), "listée avec son IP");

                // 2e installation sur la même IP sous un autre nom : l'ancienne est remplacée
                var r2 = await platform.InstallAsync(p, driver, new InstallOptions(), "HP M404 bis");
                Equal(true, r2.Success, "réinstallée");
                list = await platform.ListInstalledAsync();
                Equal(1, list.Count(i => i.Ip == "127.0.0.77"), "une seule file pour cette IP");
                Equal(true, r2.Steps.Any(s => s.Detail.Contains("remplace")), "remplacement signalé");

                var fin = await platform.FinishAsync("HP_M404_bis", true, false);
                Equal(true, fin.All(s => s.Success), "par défaut");
                var def = await ShellRunner.CaptureAsync("lpstat", new[] { "-d" }, TimeSpan.FromSeconds(10));
                Equal(true, def.Contains("HP_M404_bis"), "imprimante par défaut");

                // File créée toute seule par Bonjour / cups-browsed (adresse .local, pas d'IP) : remplacée, reconnue au modèle
                await ShellRunner.CaptureAsync("lpadmin", new[] { "-p", "Brother_MFC_L3760CDW_series", "-E", "-v", "ipps://Brother%20MFC-L3760CDW%20series._ipps._tcp.local/", "-m", "drv:///sample.drv/generic.ppd" }, TimeSpan.FromSeconds(20));
                await ShellRunner.CaptureAsync("lpadmin", new[] { "-p", "Brother_HL_L2350DW", "-E", "-v", "ipps://Brother%20HL-L2350DW._ipps._tcp.local/", "-m", "drv:///sample.drv/generic.ppd" }, TimeSpan.FromSeconds(20));
                var bro = new DiscoveredPrinter { IpAddress = "127.0.0.78", Brand = "Brother", Model = "MFC-L3760CDW" };
                bro.OpenPorts.Add(9100);
                var auto = (await platform.ListInstalledAsync()).FirstOrDefault(i => i.Name == "Brother_MFC_L3760CDW_series");
                Equal(true, auto is not null && !auto.Ip.Contains("127."), "file Bonjour listée (adresse .local)");
                var rb = await platform.InstallAsync(bro, driver, new InstallOptions(), "Brother Accueil");
                foreach (var s in rb.Steps) Console.WriteLine($"      {s.Status} {s.Step} : {s.Detail}");
                var after = await platform.ListInstalledAsync();
                Equal(false, after.Any(i => i.Name == "Brother_MFC_L3760CDW_series"), "copie Bonjour du même modèle remplacée");
                Equal(true, after.Any(i => i.Name == "Brother_HL_L2350DW"), "autre modèle intact");
                await ShellRunner.CaptureAsync("lpadmin", new[] { "-x", "Brother_HL_L2350DW" }, TimeSpan.FromSeconds(10));
                await ShellRunner.CaptureAsync("lpadmin", new[] { "-x", "Brother_Accueil" }, TimeSpan.FromSeconds(10));

                var bad = await platform.InstallAsync(p, new DriverChoice("x", "drv:///nexistepas.ppd", DriverMatchKind.ExactModel, "test"), new InstallOptions(), "HP M404 bis");
                Equal(true, bad.Success && bad.Steps.Any(s => s.Step == "Pilote" && s.IsWarning), "modèle introuvable → générique + avertissement");
                await ShellRunner.CaptureAsync("lpadmin", new[] { "-x", "HP_M404_bis" }, TimeSpan.FromSeconds(10));
            });

        if (Environment.GetEnvironmentVariable("EFP_LIVE") == "1")
            Test("Sources en ligne : chaque marque trouve son pilote universel", async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "efp-live-feed");
                var http = DriverUpdater.CreateHttpClient();
                var statuses = await new DriverUpdater(root, http).CheckAsync(DriverFeed.Default());
                foreach (var st in statuses)
                    Console.WriteLine($"      {st.Package.Id,-18} {st.State,-14} {st.Latest?.Version ?? "-"}  {st.Error}");
            });

        if (Environment.GetEnvironmentVariable("EFP_LIVE") == "1")
            Test("Windows Update (en ligne) : Brother MFC-L3760CDW de bout en bout", async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "efp-wu-live");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                var http = DriverUpdater.CreateHttpClient();
                var log = new List<string>();
                var dir = await new WindowsUpdateCatalog(http).FetchAsync(root, "Brother", "MFC-L3760CDW", new DriverUpdater(root, http), null, log.Add);
                Console.WriteLine("      " + string.Join("\n      ", log));
                Equal(true, dir is not null, "paquet extrait");
                var lib = DriverLibrary.Load(root);
                var matcher = new DriverMatcher(lib, DriverCatalog.Default());
                var choice = matcher.Choose(new DiscoveredPrinter { IpAddress = "192.168.100.190", Brand = "Brother", Model = "MFC-L3760CDW" });
                Console.WriteLine($"      choisi : {choice.DriverName} ({choice.Kind}) — {choice.InfPath}");
                Equal("Brother MFC-L3760CDW series", choice.DriverName, "pilote exact du modèle choisi");
                Equal(DriverMatchKind.ExactModel, choice.Kind, "type");
            });

        TestSync("MàJ : paquet Chocolatey façon HP (${version} interpolé, sha256)", () =>
        {
            var r = FeedResolver.ParseNupkg(Nupkg("hp-universal-print-driver-pcl", "8.2.0.26778", HpScript));
            Equal("8.2.0.26778", r.Version, "version");
            Equal("https://ftp.example.com/UPD/upd-pcl6-win10-x64-8.2.0.26778.zip", r.DownloadUrl, "URL");
            Equal("0d6de14ae6fd549b6028ac38231b625b96f6a525a585950016ad295d97e10210", r.Sha256, "sha256");
            Equal("upd-pcl6-win10-x64-8.2.0.26778.zip", r.FileName, "nom de fichier");
        });

        TestSync("MàJ : paquet à plusieurs pilotes (Konica : PS choisi, pas PCL)", () =>
        {
            var (url, sha, _) = FeedResolver.ParseInstallScript(KmScript, "3.9.821.1", "PS");
            Equal("https://dl.example.com/KM_UPD_PS_3.9.821.1.zip", url, "URL PS");
            Equal(new string('b', 64), sha, "somme PS");
            var (urlPcl, shaPcl, _) = FeedResolver.ParseInstallScript(KmScript, "3.9.821.1", "PCL");
            Equal("https://dl.example.com/KM_UPD_PCL_3.9.821.1.zip", urlPcl, "URL PCL toujours lisible");
            Equal(new string('a', 64), shaPcl, "somme PCL");
            // Vraie forme des liens Konica (paramètres d'URL entre crochets)
            var real = "$PS = 'https://dl.konicaminolta.eu/en/?tx_kmdownloadproxy_downloadproxy[fileId]=5e2fb66d&tx_kmdownloadproxy_downloadproxy[documentId]=1'\n$PS_checksum = 'D8B8C6DA6DC82C56FDE57B01499980A9E174F3ED552CCA6CED7EDDB1DC12DDE9'";
            var (ru, rs, _) = FeedResolver.ParseInstallScript(real, "3.9.1204", "PS");
            Equal(true, ru?.Contains("[fileId]=5e2fb66d") == true, "lien Konica réel lu entier");
            Equal("d8b8c6da6dc82c56fde57b01499980a9e174f3ed552cca6ced7eddb1dc12dde9", rs, "somme Konica réelle");
        });

        TestSync("MàJ : comparaison de versions", () =>
        {
            Equal(true, VersionComparer.Compare("8.10.0", "8.9.1") > 0, "8.10 > 8.9");
            Equal(true, VersionComparer.Compare("V3.50", "V3.4") > 0, "3.50 > 3.4");
            Equal(0, VersionComparer.Compare("8.2.0", "8.2"), "8.2.0 = 8.2");
            Equal(true, VersionComparer.Compare("2024.05.01", "2023.12.31") > 0, "dates");
        });

        Test("MàJ : cycle complet — absent, installé, à jour, nouvelle version, remplacée", async () =>
        {
            var root = Temp();
            try
            {
                var hp = new FakeHp();
                var up = Updater(root, new FakeHandler(hp.Respond));
                var st = (await up.CheckAsync(HpFeed()))[0];
                Equal(UpdateState.NotInstalled, st.State, "absent au départ : " + st.Error);

                var res = await up.UpdateAsync(st);
                Equal(true, res.Success, "installation : " + res.Message);
                Equal(true, File.Exists(Path.Combine(root, "HP", "hp-upd-pcl6", "x64", "hpcu.inf")), "INF décompressé");
                Equal(true, File.Exists(Path.Combine(root, "HP", "hp-upd-pcl6", DriverUpdater.StateFile)), "état enregistré");
                Equal(false, Directory.EnumerateFiles(Path.Combine(root, DriverUpdater.DownloadsFolder)).Any(), "téléchargement nettoyé");

                var lib = DriverLibrary.Load(root);
                Equal("HP Universal Printing PCL 6 (v8.1.0.26560)", lib.Drivers.Single().Name, "visible par la bibliothèque");

                st = (await up.CheckAsync(HpFeed()))[0];
                Equal(UpdateState.UpToDate, st.State, "à jour");

                hp.Version = "8.2.0.26778";
                st = (await up.CheckAsync(HpFeed()))[0];
                Equal(UpdateState.UpdateAvailable, st.State, "nouvelle version détectée");
                Equal("8.1.0.26560", st.Installed?.Version, "version installée lue");
                res = await up.UpdateAsync(st);
                Equal(true, res.Success, "mise à jour : " + res.Message);
                lib = DriverLibrary.Load(root);
                Equal("HP Universal Printing PCL 6 (v8.2.0.26778)", lib.Drivers.Single().Name, "ancienne version remplacée, pas de doublon");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("MàJ : somme de contrôle fausse -> rien n'est installé", async () =>
        {
            var root = Temp();
            try
            {
                var hp = new FakeHp { CorruptZip = true };
                var up = Updater(root, new FakeHandler(hp.Respond));
                var st = (await up.CheckAsync(HpFeed()))[0];
                var res = await up.UpdateAsync(st);
                Equal(false, res.Success, "refusé");
                Contains(res.Message, "Somme de contrôle", "message");
                Equal(false, Directory.Exists(Path.Combine(root, "HP", "hp-upd-pcl6")), "rien installé");
                Equal(0, DriverLibrary.Load(root).Drivers.Count, "bibliothèque vide");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("MàJ : archive piégée (zip slip) refusée", async () =>
        {
            var root = Temp();
            try
            {
                var hp = new FakeHp { EvilEntry = "../../evil.txt" };
                var up = Updater(root, new FakeHandler(hp.Respond));
                var st = (await up.CheckAsync(HpFeed()))[0];
                var res = await up.UpdateAsync(st);
                Equal(false, res.Success, "refusé");
                Contains(res.Message, "chemin dangereux", "message");
                Equal(false, File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.txt")), "rien écrit hors du dossier");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("MàJ : lien direct (Kyocera) — version par date, changement détecté", async () =>
        {
            var root = Temp();
            try
            {
                var lastMod = "Tue, 01 Jul 2025 10:00:00 GMT";
                var zip = DriverZip("Kyocera Classic Universaldriver PCL6");
                var h = new FakeHandler(req => req.RequestUri!.Host == "kyo.test" ? Bytes(zip, lastMod) : new HttpResponseMessage(HttpStatusCode.NotFound));
                var feed = new DriverFeed { Packages = { new() { Id = "kyocera-kx", Brand = "Kyocera", Name = "KX", Sources = { new() { Type = "direct", Url = "https://kyo.test/Drivers/KX_Driver.zip" } } } } };
                var up = Updater(root, h);
                var st = (await up.CheckAsync(feed))[0];
                Equal(UpdateState.NotInstalled, st.State, "absent : " + st.Error);
                Equal(true, h.Requests.Any(r => r.StartsWith("HEAD ")), "vérification légère (HEAD)");
                var res = await up.UpdateAsync(st);
                Equal(true, res.Success, "installé : " + res.Message);
                Equal(UpdateState.UpToDate, (await up.CheckAsync(feed))[0].State, "à jour");
                lastMod = "Wed, 01 Oct 2025 10:00:00 GMT";
                Equal(UpdateState.UpdateAvailable, (await up.CheckAsync(feed))[0].State, "nouvelle date -> mise à jour");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("MàJ : archive .exe sans 7-Zip -> mise de côté pour décompression manuelle", async () =>
        {
            var root = Temp();
            try
            {
                var exe = Encoding.ASCII.GetBytes("MZ fake self-extractor");
                var script = $"$url = 'https://lex.test/Lexmark_UPD.exe'\n$checksum = '{Sha(exe)}'";
                var h = new FakeHandler(req => req.RequestUri!.ToString() switch
                {
                    var u when u.StartsWith("https://choco.test/api/") => Bytes(Nupkg("lexmark", "3.0.7", script)),
                    "https://lex.test/Lexmark_UPD.exe" => Bytes(exe),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                });
                var feed = new DriverFeed { Packages = { new() { Id = "lexmark-upd", Brand = "Lexmark", Name = "Lexmark", Sources = { new() { Type = "chocolatey", Package = "lexmark" } } } } };
                var up = Updater(root, h);
                var st = (await up.CheckAsync(feed))[0];
                var res = await up.UpdateAsync(st);
                Equal(false, res.Success, "pas installé");
                Equal(true, res.NeedsManualExtraction, "à décompresser à la main");
                Equal(true, File.Exists(Path.Combine(root, DriverUpdater.ManualFolder, "Lexmark_UPD.exe")), "fichier gardé");
                Contains(res.Message, "7za.exe", "explique la solution");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("MàJ : page constructeur, panne réseau et lien non HTTPS", async () =>
        {
            var root = Temp();
            try
            {
                var feed = new DriverFeed
                {
                    Packages =
                    {
                        new() { Id = "canon", Brand = "Canon", Name = "Canon", Sources = { new() { Type = "page" } }, Page = "https://canon.test" },
                        new() { Id = "hs", Brand = "HP", Name = "HS", Sources = { new() { Type = "chocolatey", Package = "x" } } },
                        new() { Id = "http", Brand = "HP", Name = "HTTP", Sources = { new() { Type = "chocolatey", Package = "plainhttp" } } },
                    },
                };
                var h = new FakeHandler(req => req.RequestUri!.ToString().Contains("plainhttp")
                    ? Bytes(Nupkg("plainhttp", "1.0", "$url = 'http://insecure.test/d.zip'"))
                    : throw new HttpRequestException("pas de réseau"));
                var up = Updater(root, h);
                var st = await up.CheckAsync(feed);
                Equal(UpdateState.ManualOnly, st[0].State, "Canon : page");
                Equal("https://canon.test", st[0].Latest?.PageUrl, "lien de la page");
                Equal(UpdateState.Error, st[1].State, "panne réseau signalée sans planter");
                var withPage = new DriverFeed { Packages = { new() { Id = "hp", Brand = "HP", Name = "HP", Sources = { new() { Type = "chocolatey", Package = "x" } }, Page = "https://hp.test" } } };
                Equal(UpdateState.Error, (await up.CheckAsync(withPage))[0].State, "hors ligne même si une page existe (pas « Sur le site »)");
                Contains(st[1].Error ?? "", "pas de réseau", "message d'erreur");
                var res = await up.UpdateAsync(st[2]);
                Equal(false, res.Success, "HTTP refusé");
                Contains(res.Message, "HTTPS", "explication");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("Après scan : PostScript téléchargé pour la marque trouvée", async () =>
        {
            var root = Temp();
            try
            {
                var h = new FakeHandler(req => ProvisionServer(req, psOk: true));
                var up = Updater(root, h);
                var prov = new DriverProvisioner(up, ProvisionFeed(), DriverCatalog.Default(), () => DriverLibrary.Load(root));
                var r = await prov.EnsureAsync("HP");
                Equal(ProvisionOutcome.DownloadedPostScript, r.Outcome, "PS téléchargé : " + string.Join(" | ", r.Log));
                Equal("HP Universal Printing PS (v8.2.0)", r.DriverName, "pilote PS");
                Equal(false, h.Requests.Any(x => x.Contains("pcl")), "PCL pas téléchargé");
                var again = await prov.EnsureAsync("HP");
                Equal(ProvisionOutcome.AlreadyPostScript, again.Outcome, "2e passage : déjà sur la clé, rien à télécharger");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("Après scan : PCL de secours si le PostScript est introuvable", async () =>
        {
            var root = Temp();
            try
            {
                var h = new FakeHandler(req => ProvisionServer(req, psOk: false));
                var prov = new DriverProvisioner(Updater(root, h), ProvisionFeed(), DriverCatalog.Default(), () => DriverLibrary.Load(root));
                var r = await prov.EnsureAsync("HP");
                Equal(ProvisionOutcome.DownloadedPcl, r.Outcome, "PCL : " + string.Join(" | ", r.Log));
                Equal("HP Universal Printing PCL 6 (v8.2.0)", r.DriverName, "pilote PCL");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("Après scan : marque sans lien direct -> page du constructeur", async () =>
        {
            var root = Temp();
            try
            {
                var prov = new DriverProvisioner(Updater(root, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                    DriverFeed.Default(), DriverCatalog.Default(), () => DriverLibrary.Load(root));
                var r = await prov.EnsureAsync("Canon");
                Equal(ProvisionOutcome.ManualOnly, r.Outcome, "manuel");
                Equal(true, r.PageUrl?.StartsWith("https://") == true, "page fournie");
                Equal(ProvisionOutcome.Failed, (await prov.EnsureAsync(EFP.Core.Brands.Unknown)).Outcome, "marque inconnue");
            }
            finally { Directory.Delete(root, true); }
        });

        Test("Import manuel : zip, dossier, .inf seul, et rangement par marque", async () =>
        {
            var root = Temp(); var src = Temp();
            try
            {
                var up = Updater(root, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
                var imp = new DriverImporter(root, up);
                var zip = Path.Combine(src, "Ricoh_PS_Universal.zip");
                File.WriteAllBytes(zip, DriverZip("RICOH PS Universal Driver V4.40").Select(b => b).ToArray());
                // DriverZip crée un INF « HP » : on en fabrique un Ricoh
                using (var ms = new MemoryStream())
                {
                    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    using (var w = new StreamWriter(z.CreateEntry("disk1/oemsetup.inf").Open()))
                        w.Write("[Version]\nClass=Printer\nProvider=RICOH\n[Manufacturer]\n\"RICOH\"=R,NTamd64\n[R.NTamd64]\n\"RICOH PS Universal Driver V4.40\" = U\n");
                    File.WriteAllBytes(zip, ms.ToArray());
                }
                var r = await imp.ImportAsync(zip);
                Equal(true, r.Success, "zip : " + r.Message);
                Equal("Ricoh", r.Brand, "marque détectée");
                Equal(true, Directory.Exists(Path.Combine(root, "Ricoh", "import-Ricoh_PS_Universal")), "rangé dans Drivers\\Ricoh\\import-…");
                var lib = DriverLibrary.Load(root);
                var d = lib.Drivers.Single();
                Equal(true, d.IsImported, "marqué importé");
                var choice = new DriverMatcher(lib, DriverCatalog.Default()).Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "Ricoh", Model = "IM C3000" });
                Equal(DriverMatchKind.Imported, choice.Kind, "prioritaire pour la marque");

                // .inf seul : le dossier entier est copié (fichiers voisins)
                var infDir = Path.Combine(src, "brother"); Directory.CreateDirectory(infDir);
                File.WriteAllText(Path.Combine(infDir, "br.inf"), "[Version]\nClass=Printer\nProvider=Brother\n[Manufacturer]\n\"Brother\"=B,NTamd64\n[B.NTamd64]\n\"Brother BR-Script3 Universal\" = U\n");
                File.WriteAllText(Path.Combine(infDir, "brdriver.dll"), "x");
                var r2 = await imp.ImportAsync(Path.Combine(infDir, "br.inf"));
                Equal(true, r2.Success, ".inf : " + r2.Message);
                Equal(true, File.Exists(Path.Combine(r2.Folder!, "brdriver.dll")), "fichiers voisins copiés");

                // Rien d'utilisable
                var junk = Path.Combine(src, "notes.zip");
                using (var ms = new MemoryStream())
                {
                    using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    using (var w = new StreamWriter(z.CreateEntry("readme.txt").Open())) w.Write("rien");
                    File.WriteAllBytes(junk, ms.ToArray());
                }
                var r3 = await imp.ImportAsync(junk);
                Equal(false, r3.Success, "zip sans pilote refusé");
                Contains(r3.Message, "Aucun pilote", "message clair");
                var r4 = await imp.ImportAsync(Path.Combine(src, "pilote.exe"));
                Equal(false, r4.Success, "fichier absent");
                Equal(false, Directory.EnumerateDirectories(Path.Combine(root, DriverUpdater.DownloadsFolder)).Any(), "dossiers temporaires nettoyés");
            }
            finally { Directory.Delete(root, true); Directory.Delete(src, true); }
        });

        TestSync("MàJ : liste intégrée valide et réglages par défaut", () =>
        {
            var feed = DriverFeed.FromJson(DriverFeed.Default().ToJson());
            Equal(DriverFeed.Default().Packages.Count, feed.Packages.Count, "aller-retour JSON");
            Equal(true, feed.Packages.All(p => p.Page?.StartsWith("https://") == true), "chaque pilote a une page de secours HTTPS");
            Equal(feed.Packages.Count, feed.Packages.Select(p => p.Id).Distinct().Count(), "identifiants uniques");
            var primary = feed.Packages.Where(p => !p.Fallback).ToList();
            Equal(primary.Count, primary.Select(p => p.Brand).Distinct().Count(), "un seul pilote principal (PostScript) par marque");
            Equal(true, feed.Packages.Where(p => p.Fallback).All(p => p.Name.Contains("PCL")), "les secours sont des PCL");
            Equal("hp-universal-print-driver-ps", feed.Packages.First(p => p.Brand == "HP").Sources[0].Package, "HP : paquet PostScript");
            Equal("PS", feed.Packages.First(p => p.Brand == "Konica Minolta").Sources[0].UrlVariable, "Konica : URL PostScript");
            var s = new UpdateSettings();
            Equal(true, s.CheckAtStartup && s.AutoUpdateInstalled, "vérif + MàJ auto par défaut");
        });
    }
}
