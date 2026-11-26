using System.Diagnostics;
using System.Net;
using System.Text;
using EFP.Core;
using EFP.Core.Discovery;
using EFP.Core.Drivers;
using EFP.Core.Snmp;
using EFP.Core.Windows;
using EFP.Tests;
using static EFP.Tests.Assert;

var tests = new List<(string Name, Func<Task> Body)>();
void Test(string name, Func<Task> body) => tests.Add((name, body));
void TestSync(string name, Action body) => tests.Add((name, () => { body(); return Task.CompletedTask; }));

// ---------------------------------------------------------------- BER / SNMP
TestSync("BER : entiers (aller-retour)", () =>
{
    foreach (var v in new long[] { 0, 1, 127, 128, 255, 256, -1, -128, -129, 65535, int.MaxValue, int.MinValue })
    {
        var enc = Ber.EncodeInteger(v);
        var node = Ber.Read(enc, 0);
        Equal(v, Ber.DecodeInteger(enc, node), $"entier {v}");
    }
    Equal("02-02-00-80", BitConverter.ToString(Ber.EncodeInteger(128)), "forme minimale de 128");
});

TestSync("BER : OID (aller-retour, grands arcs)", () =>
{
    foreach (var oid in new[] { "1.3.6.1.2.1.1.1.0", PrinterOids.PwgDeviceId1284, "1.3.6.1.4.1.11.2.3.9.1.1.7.0", "1.3.6.1.4.1.4294967295.1", "2.999.3" })
    {
        var enc = Ber.EncodeOid(oid);
        Equal(oid, Ber.DecodeOid(enc, Ber.Read(enc, 0)), oid);
    }
});

TestSync("BER : longueurs longues", () =>
{
    var big = new byte[300];
    var tlv = Ber.Tlv(Ber.OctetString, big);
    var n = Ber.Read(tlv, 0);
    Equal(300, n.Length, "longueur");
    Equal(4, n.ValueOffset, "en-tête 0x04 0x82 0x01 0x2C");
});

TestSync("SNMP : réponse corrompue ignorée sans exception", () =>
{
    Equal(true, SnmpClient.ParseResponse(new byte[] { 0x30, 0x05, 0x02 }) is null, "tronqué");
    Equal(true, SnmpClient.ParseResponse(new byte[] { 0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF }) is null, "longueur absurde");
    Equal(true, SnmpClient.ParseResponse(Array.Empty<byte>()) is null, "vide");
});

var hpProfile = new Dictionary<string, object>
{
    [PrinterOids.SysDescr] = "HP ETHERNET MULTI-ENVIRONMENT,ROM none,JETDIRECT,JD153,EEPROM JSI23900012",
    [PrinterOids.SysName] = "NPI8A2B3C",
    [PrinterOids.HrDeviceType1] = "oid:" + PrinterOids.HrDevicePrinter,
    [PrinterOids.HrDeviceDescr1] = "HP LaserJet Pro M404dn",
    [PrinterOids.PrtGeneralSerialNumber] = "PHBQK12345",
    [PrinterOids.HpDeviceId1284] = "MFG:Hewlett-Packard;CMD:PJL,PCL,PCLXL,PCL5,PDF,POSTSCRIPT,URF;MDL:HP LaserJet Pro M404dn;CLS:PRINTER;DES:W1A53A;",
    ["1.3.6.1.2.1.2.2.1.6.1"] = Array.Empty<byte>(),                         // loopback : MAC vide
    ["1.3.6.1.2.1.2.2.1.6.2"] = new byte[] { 0x3C, 0x52, 0x82, 0x8A, 0x2B, 0x3C },
    ["1.3.6.1.2.1.2.2.1.7.1"] = 1,
};

var kyoceraProfile = new Dictionary<string, object>
{
    [PrinterOids.SysDescr] = "KYOCERA Document Solutions Printing System",
    [PrinterOids.HrDeviceType1] = "oid:" + PrinterOids.HrDevicePrinter,
    [PrinterOids.HrDeviceDescr1] = "TASKalfa 2553ci",
    [PrinterOids.PrtGeneralSerialNumber] = "W8R1234567\0\0",
    ["1.3.6.1.2.1.2.2.1.6.1"] = new byte[] { 0x00, 0x17, 0xC8, 0x11, 0x22, 0x33 },
};

ScannerOptions LocalScanner(int snmpPort, params int[] tcp) => new()
{
    SnmpPort = snmpPort,
    ProbePorts = tcp,
    SnmpTimeout = TimeSpan.FromMilliseconds(400),
    TcpTimeout = TimeSpan.FromMilliseconds(300),
    ResolveHostNames = false,
};

Test("Scan : HP identifié (1284, série, MAC après loopback, port TCP)", async () =>
{
    using var agent = new FakeSnmpAgent(hpProfile);
    using var tcp = new FakeTcpPort();
    var p = await new PrinterScanner(LocalScanner(agent.Port, tcp.Port)).ProbeAsync(IPAddress.Loopback, true);
    NotNull(p, "imprimante trouvée");
    Equal("HP", p!.Brand, "marque");
    Equal("LaserJet Pro M404dn", p.Model, "modèle");
    Equal("PHBQK12345", p.SerialNumber, "série");
    Equal("3C:52:82:8A:2B:3C", p.MacAddress, "MAC");
    Equal(true, p.OpenPorts.Contains(tcp.Port), "port ouvert détecté");
    Equal("HP LaserJet Pro M404dn", p.DisplayName, "nom affiché");
});

Test("Scan : Kyocera sans 1284 (hrDeviceDescr + série avec \\0)", async () =>
{
    using var agent = new FakeSnmpAgent(kyoceraProfile);
    var p = await new PrinterScanner(LocalScanner(agent.Port)).ProbeAsync(IPAddress.Loopback, true);
    NotNull(p, "imprimante trouvée");
    Equal("Kyocera", p!.Brand, "marque");
    Equal("TASKalfa 2553ci", p.Model, "modèle");
    Equal("W8R1234567", p.SerialNumber, "série nettoyée");
    Equal("00:17:C8:11:22:33", p.MacAddress, "MAC");
});

Test("Scan : agent SNMP v1 strict (pas de v2c, noSuchName)", async () =>
{
    using var agent = new FakeSnmpAgent(kyoceraProfile) { V1Only = true };
    var p = await new PrinterScanner(LocalScanner(agent.Port)).ProbeAsync(IPAddress.Loopback, true);
    NotNull(p, "imprimante trouvée en v1");
    Equal("Kyocera", p!.Brand, "marque");
    Equal("TASKalfa 2553ci", p.Model, "modèle");
});

Test("Scan : un routeur SNMP n'est pas pris pour une imprimante", async () =>
{
    using var agent = new FakeSnmpAgent(new() { [PrinterOids.SysDescr] = "Linux router 5.10 #1 SMP", [PrinterOids.SysName] = "fritz.box" });
    var scanner = new PrinterScanner(LocalScanner(agent.Port));
    Equal(true, await scanner.ProbeAsync(IPAddress.Loopback, true) is null, "écarté au scan");
    var manual = await scanner.ProbeAsync(IPAddress.Loopback, false);
    NotNull(manual, "accepté en ajout manuel");
});

Test("Scan : hôte muet ignoré rapidement", async () =>
{
    var sw = Stopwatch.StartNew();
    var p = await new PrinterScanner(LocalScanner(1)).ProbeAsync(IPAddress.Parse("127.0.0.2"), true);
    Equal(true, p is null, "rien");
    Equal(true, sw.Elapsed < TimeSpan.FromSeconds(4), $"durée {sw.Elapsed.TotalSeconds:F1}s");
});

Test("Scan : plage complète avec progression", async () =>
{
    using var agent = new FakeSnmpAgent(hpProfile);
    var found = new List<DiscoveredPrinter>();
    ScanProgress? last = null;
    var addrs = new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.3"), IPAddress.Parse("127.0.0.4") };
    var res = await new PrinterScanner(LocalScanner(agent.Port)).ScanAsync(addrs, p => { lock (found) found.Add(p); },
        new SyncProgress<ScanProgress>(p => last = p));
    Equal(1, res.Count, "une imprimante");
    Equal(1, found.Count, "callback");
    Equal(3, last?.Done, "progression finale");
});

// ---------------------------------------------------------------- Identification
TestSync("Marques : alias et OEM", () =>
{
    Equal("HP", Brands.Detect("Hewlett-Packard"), "Hewlett-Packard");
    Equal("HP", Brands.Detect("Samsung M2070 Series"), "Samsung");
    Equal("Kyocera", Brands.Detect("UTAX 3505ci"), "UTAX");
    Equal("Kyocera", Brands.Detect("TA Triumph-Adler 2508ci"), "Triumph-Adler");
    Equal("Konica Minolta", Brands.Detect("Develop ineo+ 250i"), "Develop");
    Equal("Konica Minolta", Brands.Detect("bizhub C258"), "bizhub");
    Equal("Ricoh", Brands.Detect("LANIER MP C3004"), "Lanier");
    Equal("Ricoh", Brands.Detect("NRG MP 2554 / nashuatec"), "Nashuatec");
    Equal("Canon", Brands.Detect("Canon iR-ADV C3530"), "Canon");
    Equal("Brother", Brands.Detect("Brother MFC-L8690CDW series"), "Brother");
    Equal("Xerox", Brands.Detect("Xerox VersaLink C405"), "Xerox");
    Equal(Brands.Unknown, Brands.Detect("Linux 5.10", null, ""), "inconnu");
});

TestSync("IEEE 1284 : clés longues et courtes", () =>
{
    var d = DeviceId1284.Parse("MANUFACTURER:Brother;COMMAND SET:PJL,PCL;MODEL:HL-L5100DN series;CLASS:PRINTER;SN:E7890");
    Equal("Brother", d["MFG"], "MFG");
    Equal("HL-L5100DN series", d["MDL"], "MDL");
    Equal("E7890", d["SN"], "SN");
});

TestSync("Nettoyage des modèles", () =>
{
    Equal("LaserJet Pro M404dn", ModelCleaner.Clean("HP LaserJet Pro M404dn", "HP"), "préfixe HP");
    Equal("bizhub C258", ModelCleaner.Clean("KONICA MINOLTA bizhub C258", "Konica Minolta"), "Konica");
    Equal("HL-L5100DN", ModelCleaner.Clean("Brother HL-L5100DN series", "Brother"), "series");
    Equal("ECOSYS M2540dn", ModelCleaner.Clean("ECOSYS M2540dn", "Kyocera"), "sans préfixe");
    Equal(null, ModelCleaner.Clean("   ", "HP"), "vide");
});

// ---------------------------------------------------------------- Plages IP
TestSync("Plages IP", () =>
{
    Equal(254, IpRange.Parse("192.168.1.0/24").Count, "/24");
    Equal(11, IpRange.Parse("192.168.1.10-20").Count, "plage courte");
    Equal(3, IpRange.Parse("10.0.0.1-10.0.0.3").Count, "plage complète");
    Equal(2, IpRange.Parse("10.0.0.5, 10.0.0.5; 10.0.0.6").Count, "doublons");
    Equal("192.168.1.0/24", IpRange.DefaultScanRange(new LocalSubnet("eth", IPAddress.Parse("192.168.1.42"), 16)), "/16 ramené au /24");
    Throws<FormatException>(() => IpRange.Parse("10.0.0.0/16"), "/16 refusé");
    Throws<FormatException>(() => IpRange.Parse("abc"), "texte");
    Throws<FormatException>(() => IpRange.Parse("10.0.0"), "IP incomplète");
});

// ---------------------------------------------------------------- INF / pilotes
const string KxInf = """"
    ; Pilote KX de test
    [Version]
    Signature="$Windows NT$"
    Class=Printer
    ClassGuid={4D36E979-E325-11CE-BFC1-08002BE10318}
    Provider=%KYOCERA%
    DriverVer=03/15/2025,8.4.1716.0

    [Manufacturer]
    %KYOCERA%=Kyocera,NTamd64,NTx86

    [Kyocera.NTamd64]
    "Kyocera TASKalfa 2553ci KX" = KX_TA2553, USBPRINT\KyoceraTASKalfa_2553ci3E2F, WSDPRINT\KyoceraTASKalfa_2553ci3E2F
    %MODEL_M2540% = KX_M2540, USBPRINT\KyoceraECOSYS_M2540dn1234
    "Kyocera TASKalfa 2553ci KX (FAX)" = KX_FAX
    "Kyocera TASKalfa 2553ci KX; test ""guillemets""" = KX_X

    [Kyocera.NTx86]
    "Kyocera Ancien Pilote 32 bits" = KX_OLD

    [Strings]
    KYOCERA="Kyocera"
    MODEL_M2540="Kyocera ECOSYS M2540dn KX"
    """";

TestSync("INF : modèles 64 bits, chaînes, commentaires, x86 ignoré", () =>
{
    var inf = InfParser.Parse(KxInf, "kx.inf");
    Equal(true, inf.IsPrinterClass, "classe imprimante");
    Equal("Kyocera", inf.Provider, "fournisseur résolu");
    var names = inf.Models.Select(m => m.Name).ToList();
    Equal(true, names.Contains("Kyocera TASKalfa 2553ci KX"), "modèle direct");
    Equal(true, names.Contains("Kyocera ECOSYS M2540dn KX"), "modèle via %chaîne%");
    Equal(false, names.Any(n => n.Contains("32 bits")), "x86 ignoré");
    Equal(true, names.Contains("Kyocera TASKalfa 2553ci KX; test \"guillemets\""), "point-virgule et guillemets entre guillemets");
    Equal(2, inf.Models.First().HardwareIds.Count, "ID matériels");
});

TestSync("INF : fichier UTF-16 avec BOM", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"pp_{Guid.NewGuid():N}.inf");
    File.WriteAllText(path, KxInf, new UnicodeEncoding(false, true));
    try { Equal(4, InfParser.ParseFile(path).Models.Count, "modèles lus"); }
    finally { File.Delete(path); }
});

TestSync("INF : un INF non-imprimante est ignoré", () =>
{
    var inf = InfParser.Parse("[Version]\nClass=Net\n[Manufacturer]\nX=X,NTamd64\n[X.NTamd64]\n\"Carte réseau\"=a,b");
    Equal(false, inf.IsPrinterClass, "classe");
    Equal(0, inf.Models.Count, "aucun modèle");
});

DriverLibrary BuildLibrary()
{
    var lib = DriverLibrary.Empty("/drivers");
    lib.AddInf(InfParser.Parse(KxInf, "/drivers/Kyocera/KX/OEMSETUP.INF"));
    lib.AddInf(InfParser.Parse("""
        [Version]
        Class=Printer
        Provider="Kyocera"
        [Manufacturer]
        "Kyocera"=K,NTamd64
        [K.NTamd64]
        "Kyocera Classic Universaldriver PCL6" = U
        "Kyocera Classic Universaldriver KPDL" = U2
        """, "/drivers/Kyocera/Universal/oemsetup.inf"));
    lib.AddInf(InfParser.Parse("""
        [Version]
        Class=Printer
        Provider=HP
        [Manufacturer]
        "HP"=HP,NTamd64.10.0
        [HP.NTamd64.10.0]
        "HP Universal Printing PCL 6" = U
        "HP Universal Printing PS" = U
        "HP LaserJet Pro M404-M405 PCL 6" = M
        """, "/drivers/HP/hpcu.inf"));
    return lib;
}

TestSync("Pilotes : par défaut, un seul universel PostScript par marque (jamais le modèle exact)", () =>
{
    var d = new DriverMatcher(BuildLibrary(), DriverCatalog.Default());
    var kyo = d.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "Kyocera", Model = "TASKalfa 2553ci" });
    Equal("Kyocera Classic Universaldriver KPDL", kyo.DriverName, "Kyocera : universel KPDL (PostScript)");
    Equal(DriverMatchKind.BrandUniversal, kyo.Kind, "type universel");
    var hp = d.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "HP", Model = "LaserJet Pro M404dn" });
    Equal("HP Universal Printing PS", hp.DriverName, "HP : UPD PS même si le pilote du modèle est présent");
    Equal(false, d.Candidates(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "HP", Model = "LaserJet Pro M404dn" }).Any(c => c.Kind == DriverMatchKind.ExactModel), "aucun pilote de modèle proposé");
    var unk = d.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1" });
    Equal("Microsoft PS Class Driver", unk.DriverName, "générique : PS d'abord");
});

TestSync("Pilotes : modèle exact prioritaire (option preferExactModel)", () =>
{
    var m = new DriverMatcher(BuildLibrary(), new DriverCatalog { PreferExactModel = true, Universal = DriverCatalog.Default().Universal, InboxFallback = DriverCatalog.Default().InboxFallback });
    var c = m.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "Kyocera", Model = "TASKalfa 2553ci" });
    Equal("Kyocera TASKalfa 2553ci KX", c.DriverName, "pilote");
    Equal(DriverMatchKind.ExactModel, c.Kind, "type");
});

TestSync("Pilotes : série HP « M404-M405 » reconnue pour un M404dn (option modèle exact)", () =>
{
    var m = new DriverMatcher(BuildLibrary(), new DriverCatalog { PreferExactModel = true, Universal = DriverCatalog.Default().Universal });
    var c = m.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "HP", Model = "LaserJet Pro M404dn" });
    Equal("HP LaserJet Pro M404-M405 PCL 6", c.DriverName, "pilote");
});

TestSync("Pilotes : PostScript avant PCL6, PCL6 en secours", () =>
{
    var m = new DriverMatcher(BuildLibrary(), DriverCatalog.Default());
    var c = m.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "Kyocera", Model = "ECOSYS P3145dn" });
    Equal("Kyocera Classic Universaldriver KPDL", c.DriverName, "KPDL (PS) avant PCL6");
    Equal(DriverMatchKind.BrandUniversal, c.Kind, "type");
    var hp = m.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "HP", Model = "Color LaserJet MFP M480f" });
    Equal("HP Universal Printing PS", hp.DriverName, "HP universel PS");
    var pclOnly = DriverLibrary.Empty();
    pclOnly.AddInf(InfParser.Parse("[Version]\nClass=Printer\n[Manufacturer]\n\"HP\"=HP,NTamd64\n[HP.NTamd64]\n\"HP Universal Printing PCL 6\" = U\n"));
    Equal("HP Universal Printing PCL 6", new DriverMatcher(pclOnly, DriverCatalog.Default()).Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "HP", Model = "X" }).DriverName, "PCL6 si pas de PS sur la clé");
});

TestSync("Pilotes : marque inconnue -> générique Windows, liste toujours non vide", () =>
{
    var m = new DriverMatcher(BuildLibrary(), DriverCatalog.Default());
    var p = new DiscoveredPrinter { IpAddress = "1.1.1.1" };
    var c = m.Choose(p);
    Equal(DriverMatchKind.GenericInbox, c.Kind, "type");
    Equal(true, c.IsInbox, "intégré");
    var empty = new DriverMatcher(DriverLibrary.Empty(), new DriverCatalog());
    Equal(1, empty.Candidates(p).Count, "repli même sans catalogue");
});

TestSync("Pilotes : pas de faux positif entre modèles voisins", () =>
{
    Equal(0, DriverMatcher.ModelScore("TASKalfa 2552ci", "Kyocera TASKalfa 2553ci KX"), "2552 ≠ 2553");
    Equal(0, DriverMatcher.ModelScore("ECOSYS M2540dn", "Kyocera TASKalfa 2540ci KX"), "famille différente");
    Equal(true, DriverMatcher.ModelScore("ECOSYS M2540dn", "Kyocera ECOSYS M2540dn KX") >= 100, "exact");
    Equal(false, DriverMatcher.ModelScore("MFC-L8690CDW", "Brother MFC-L8900CDW") > 0, "Brother voisin");
});

TestSync("Catalogue JSON : commentaires, virgules finales, aller-retour", () =>
{
    var json = DriverCatalog.Default().ToJson();
    var back = DriverCatalog.FromJson(json);
    Equal(DriverCatalog.Default().Universal.Count, back.Universal.Count, "règles");
    var custom = DriverCatalog.FromJson("""
        { // perso
          "universal": [ { "brand": "HP", "patterns": ["^HP Universal Printing PS"], } ],
          "inboxFallback": ["Microsoft PS Class Driver"],
        }
        """);
    var m = new DriverMatcher(BuildLibrary(), custom);
    Equal("HP Universal Printing PS", m.Choose(new DiscoveredPrinter { IpAddress = "1.1.1.1", Brand = "HP", Model = "X999" }).DriverName, "préférence perso");
});

// ---------------------------------------------------------------- Scripts PowerShell
TestSync("Nom Windows : doublon de marque, caractères interdits", () =>
{
    var p = new DiscoveredPrinter { IpAddress = "192.168.1.50", Brand = "HP", Model = "HP LaserJet, Pro\\M404" };
    Equal("HP LaserJet Pro M404", PowerShellScripts.BuildPrinterName(p, new InstallOptions()), "nom");
    var o = new InstallOptions { NameTemplate = "{Brand} {Model} - {Location}", Location = "Réception" };
    Equal("Kyocera TASKalfa 2553ci - Réception", PowerShellScripts.BuildPrinterName(new DiscoveredPrinter { IpAddress = "1.2.3.4", Brand = "Kyocera", Model = "TASKalfa 2553ci" }, o), "emplacement");
    Equal("Imprimante 1.2.3.4", PowerShellScripts.BuildPrinterName(new DiscoveredPrinter { IpAddress = "1.2.3.4" }, new InstallOptions { NameTemplate = "{Model}" }), "repli");
    Equal("Compta HP 1", PowerShellScripts.BuildShareName("Compta: HP/1"), "nom de partage nettoyé");
});

TestSync("Lecture des lignes ##PP", () =>
{
    var r = new InstallResult { Printer = new DiscoveredPrinter { IpAddress = "1.1.1.1" } };
    PrinterInstaller.Fill(new ScriptOutput(0, "bruit\r\n##PP|Pilote|OK|installé\r\n##PP|@name|OK|HP (2)\r\n##PP|Réglages|WARN|refusé\r\n", "", false), r.Steps,
        s => { if (s.Step == "@name") r.WindowsName = s.Detail; });
    Equal(2, r.Steps.Count, "étapes");
    Equal("HP (2)", r.WindowsName, "nom final");
    Equal(true, r.Success, "warning non bloquant");
    var fail = new List<StepResult>();
    PrinterInstaller.Fill(new ScriptOutput(1, "", "powershell introuvable\n", false), fail);
    Equal(false, fail[0].Success, "échec sans sortie");
    Equal("powershell introuvable", fail[0].Detail, "message stderr");
});

TestSync("Validation du dossier de scan", () =>
{
    ScanFolderInstaller.Validate(new ScanFolderOptions());
    Throws<ArgumentException>(() => ScanFolderInstaller.Validate(new ScanFolderOptions { FolderPath = "Scans" }), "chemin relatif");
    Throws<ArgumentException>(() => ScanFolderInstaller.Validate(new ScanFolderOptions { ShareName = "Sc ans'" }), "partage");
    Throws<ArgumentException>(() => ScanFolderInstaller.Validate(new ScanFolderOptions { UserName = "a;b" }), "utilisateur");
    Throws<ArgumentException>(() => ScanFolderInstaller.Validate(new ScanFolderOptions { Password = "court" }), "mot de passe");
});

TestSync("Mots de passe générés", () =>
{
    for (var i = 0; i < 300; i++)
    {
        var pw = PasswordGenerator.Generate();
        Equal(12, pw.Length, "longueur");
        Equal(true, char.IsLetter(pw[0]), $"commence par une lettre : {pw}");
        Equal(true, pw.Any(char.IsUpper) && pw.Any(char.IsLower) && pw.Any(char.IsDigit), $"complexité : {pw}");
        Equal(false, pw.IndexOfAny("0O1lI\\/:@%&'\" ".ToCharArray()) >= 0, $"caractères ambigus : {pw}");
    }
});

var pwsh = FindPwsh();
if (pwsh is not null)
{
    Test("PowerShell : Quote résiste à l'injection (apostrophes droites et typographiques)", async () =>
    {
        var evil = "x'; Write-Output PWNED; '\u2019; Write-Output PWNED2; \u2018 $env:PATH $(whoami) `n \"";
        var script = $"$v = {PowerShellScripts.Quote(evil)}\n[Console]::Out.Write($v)";
        var res = await new PowerShellRunner { Executable = pwsh }.RunAsync(script, TimeSpan.FromSeconds(30));
        Equal(evil, res.StdOut.TrimEnd('\r', '\n'), "valeur restituée à l'identique, rien exécuté");
    });

    Test("PowerShell : scripts générés sans erreur de syntaxe", async () =>
    {
        var p = new DiscoveredPrinter { IpAddress = "192.168.1.50", Brand = "HP", Model = "LaserJet O'Neil" };
        var d = new DriverChoice("HP Universal Printing PCL 6", @"D:\Drivers\HP\hpcu.inf", DriverMatchKind.BrandUniversal, "");
        foreach (var (label, script) in new[]
        {
            ("installation", PowerShellScripts.InstallPrinter(p, d, new InstallOptions { SetAsDefault = true, MonochromeByDefault = true, Location = "Bureau d'Anne" }, "HP LaserJet O'Neil")),
            ("dossier de scan", PowerShellScripts.CreateScanFolder(new ScanFolderOptions())),
        })
        {
            var path = Path.Combine(Path.GetTempPath(), $"pp_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(path, script, new UTF8Encoding(true));
            try
            {
                var check = $"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile({PowerShellScripts.Quote(path)}, [ref]$null, [ref]$e); [Console]::Out.Write(($e | ForEach-Object {{ $_.Message + ' @' + $_.Extent.StartLineNumber }}) -join ' | ')";
                var res = await new PowerShellRunner { Executable = pwsh }.RunAsync(check, TimeSpan.FromSeconds(30));
                Equal("", res.StdOut.Trim(), $"erreurs de syntaxe ({label})");
            }
            finally { File.Delete(path); }
        }
    });

    Test("PowerShell : déroulé de l'installation avec cmdlets simulées (nom déjà pris)", async () =>
    {
        var inf = Path.Combine(Path.GetTempPath(), $"pp_{Guid.NewGuid():N}.inf");
        await File.WriteAllTextAsync(inf, "x");
        try
        {
            var p = new DiscoveredPrinter { IpAddress = "192.168.1.50", Brand = "HP", Model = "LaserJet Pro M404dn", SnmpResponded = true };
            var d = new DriverChoice("HP Universal Printing PCL 6", inf, DriverMatchKind.BrandUniversal, "");
            var opts = new InstallOptions { ReplaceExisting = false, SetAsDefault = true, MonochromeByDefault = true };
            var name = PowerShellScripts.BuildPrinterName(p, opts);
            var runner = new MockedRunner(pwsh, PrinterMocks);
            var result = await new PrinterInstaller(runner).InstallAsync(p, d, opts);
            Equal(true, result.Success, "succès : " + result.RawOutput);
            Equal($"{name} (2)", result.WindowsName, "suffixe car le nom existe déjà");
            var log = runner.LastOutput;
            Contains(log, "MOCK pnputil /add-driver " + inf, "pnputil appelé avec l'INF");
            Equal(false, log.Contains("/install"), "pas de /install (lent : balayage des périphériques)");
            Contains(log, "MOCK Add-PrinterPort IP_192.168.1.50 192.168.1.50 snmp=1", "port TCP/IP avec SNMP");
            Contains(log, $"MOCK Add-Printer {name} (2)|HP Universal Printing PCL 6|IP_192.168.1.50", "imprimante ajoutée");
            Contains(log, $"MOCK Set-PrintConfiguration {name} (2) duplex=TwoSidedLongEdge color=False", "réglages sur le bon nom");
            Contains(log, $"MOCK Win32_Printer Name='{name} (2)'", "WMI sur le bon nom");
            Equal(new[] { "Pilote", "Port", "Imprimante", "Réglages", "État", "Auto-ajout Windows", "Par défaut", "Page de test" }, result.Steps.Select(s => s.Step).ToArray(), "étapes");
            Equal(null, result.ShareUnc, "pas de partage par défaut");
            Equal(true, result.Steps.Single(s => s.Step == "Imprimante").IsWarning, "copie Windows signalée quand on ne remplace pas");
            Contains(log, "NcdAutoSetup\\Private AutoSetup=0", "auto-ajout Windows coupé");
        }
        finally { File.Delete(inf); }
    });

    Test("PowerShell : la copie WSD (sans IP) ajoutée par Windows est reconnue au modèle et remplacée", async () =>
    {
        var inf = Path.Combine(Path.GetTempPath(), $"pp_{Guid.NewGuid():N}.inf");
        await File.WriteAllTextAsync(inf, "x");
        try
        {
            var p = new DiscoveredPrinter { IpAddress = "192.168.100.190", Brand = "Brother", Model = "MFC-L3760CDW", SnmpResponded = true };
            var d = new DriverChoice("Brother MFC-L3760CDW series", inf, DriverMatchKind.ExactModel, "");
            var mocks = PrinterMocks.Replace("@{ Name = 'HP OfficeJet copie Windows'; PortName = '192.168.1.50' })",
                "@{ Name = 'HP OfficeJet copie Windows'; PortName = '192.168.1.50' }, @{ Name = 'Brother MFC-L3760CDW series'; DriverName = 'Microsoft IPP Class Driver'; PortName = 'WSD-1d2e3f' }, @{ Name = 'Brother HL-L2350DW'; PortName = 'WSD-999' })");
            var runner = new MockedRunner(pwsh, mocks);
            var result = await new PrinterInstaller(runner).InstallAsync(p, d, new InstallOptions(), "Brother Accueil");
            Equal(true, result.Success, "succès : " + result.RawOutput);
            Contains(runner.LastOutput, "MOCK Remove-Printer Brother MFC-L3760CDW series", "copie WSD du même modèle supprimée");
            Equal(false, runner.LastOutput.Contains("MOCK Remove-Printer Brother HL-L2350DW"), "autre modèle WSD intact");
        }
        finally { File.Delete(inf); }
    });

    Test("PowerShell : la copie installée toute seule par Windows sur la même IP est remplacée", async () =>
    {
        var inf = Path.Combine(Path.GetTempPath(), $"pp_{Guid.NewGuid():N}.inf");
        await File.WriteAllTextAsync(inf, "x");
        try
        {
            var p = new DiscoveredPrinter { IpAddress = "192.168.1.50", Brand = "HP", Model = "OfficeJet Pro 8120e", SnmpResponded = true };
            var d = new DriverChoice("HP Universal Printing PS (v8.2.0)", inf, DriverMatchKind.BrandUniversal, "");
            var opts = new InstallOptions { NameTemplate = "Imprimante du prince" };
            var runner = new MockedRunner(pwsh, PrinterMocks);
            var live = new List<string>();
            var result = await new PrinterInstaller(runner).InstallAsync(p, d, opts, null, new SyncProgress<StepProgress>(x => live.Add($"{x.Step}:{x.Status}")));
            Equal(true, result.Success, "succès : " + result.RawOutput);
            Equal("Imprimante du prince", result.WindowsName, "nom demandé, sans suffixe");
            Equal(true, live.IndexOf("Pilote:START") >= 0 && live.IndexOf("Pilote:START") < live.IndexOf("Pilote:OK"), "progression en direct (début puis fin d'étape)");
            Equal(false, result.Steps.Any(x => x.Step == "Pilote" && x.Detail == ""), "les débuts d'étape ne sont pas comptés comme résultats");
            Contains(runner.LastOutput, "MOCK Remove-Printer HP OfficeJet copie Windows", "copie Windows supprimée");
            Equal(false, runner.LastOutput.Contains("MOCK Remove-Printer HP LaserJet Pro M404dn"), "imprimante d'une autre IP intacte");
            Contains(result.Steps.Single(s => s.Step == "Imprimante").Detail ?? "", "remplace : HP OfficeJet copie Windows", "expliqué dans le journal");
            Equal(true, result.Steps.Any(s => s.Step == "Par défaut"), "mise par défaut (cochée d'office)");

            // Même installation, partagée sur le réseau, avec une imprimante que Windows voit « hors connexion »
            var runner2 = new MockedRunner(pwsh, PrinterMocks.Replace("[pscustomobject]$_ } }", "$o = [pscustomobject]$_; $o | Add-Member -NotePropertyName PrinterStatus -NotePropertyValue 'Offline' -Force; $o } }"));
            var shared = await new PrinterInstaller(runner2).InstallAsync(p, d, new InstallOptions { NameTemplate = "Compta: HP/1", ShareOnNetwork = true });
            Equal(true, shared.Success, "succès partage : " + shared.RawOutput);
            Contains(runner2.LastOutput, "MOCK Set-Printer Compta: HP/1 shared=True share=Compta HP 1", "partage avec un nom valide");
            Contains(runner2.LastOutput, "MOCK firewall", "pare-feu ouvert pour le partage");
            Equal(@"\\PC-CLIENT\Compta HP 1".Replace("PC-CLIENT", Environment.GetEnvironmentVariable("COMPUTERNAME") ?? ""), shared.ShareUnc, "chemin réseau retourné");
            Contains(runner2.LastOutput, "MOCK Set-CimInstance SNMPEnabled=False", "SNMP coupé si « Hors connexion »");
            Equal(true, shared.Steps.Single(x => x.Step == "État").IsWarning, "signalé dans le journal");
        }
        finally { File.Delete(inf); }
    });

    Test("PowerShell : imprimantes déjà installées sur le PC (pilote + version)", async () =>
    {
        const string mocks = """
            function Get-PrinterPort { @([pscustomobject]@{ Name = 'IP_192.168.1.41'; PrinterHostAddress = '192.168.1.41' },
                                         [pscustomobject]@{ Name = 'WSD-1234'; PrinterHostAddress = $null }) }
            function Get-PrinterDriver { @([pscustomobject]@{ Name = 'HP Universal Printing PS'; DriverVersion = [uint64]17451448556060704 },
                                           [pscustomobject]@{ Name = 'Microsoft PS Class Driver'; DriverVersion = [uint64]2814749767106560 }) }
            function Get-Printer { @([pscustomobject]@{ Name = 'Compta'; DriverName = 'HP Universal Printing PS'; PortName = 'IP_192.168.1.41' },
                                     [pscustomobject]@{ Name = 'Vieille'; DriverName = 'Microsoft PS Class Driver'; PortName = '192.168.1.50_1' },
                                     [pscustomobject]@{ Name = 'PDF'; DriverName = 'Microsoft Print To PDF'; PortName = 'PORTPROMPT:' },
                                     [pscustomobject]@{ Name = 'WSD'; DriverName = 'Microsoft PS Class Driver'; PortName = 'WSD-1234' }) }

            """;
        var list = await new PrinterInstaller(new MockedRunner(pwsh, mocks)).ListInstalledAsync();
        Equal(3, list.Count, "imprimantes avec une IP + copie WSD (pas le PDF)");
        Equal("wsd", list[2].Ip, "copie WSD repérée");
        Equal("Compta", list[0].Name, "nom");
        Equal("192.168.1.41", list[0].Ip, "IP via le port");
        Equal("62.0.0.0", list[0].DriverVersion?.Split('.')[0] + ".0.0.0", "version décodée");
        Equal("192.168.1.50", list[1].Ip, "IP lue dans le nom du port");
        Equal("Microsoft PS Class Driver", list[1].DriverName, "pilote générique repéré");
    });

    Test("PowerShell : pilote en échec -> arrêt propre", async () =>
    {
        var p = new DiscoveredPrinter { IpAddress = "10.0.0.9", Brand = "HP", Model = "X" };
        var d = new DriverChoice("Pilote Inexistant", @"Z:\nexiste\pas.inf", DriverMatchKind.BrandUniversal, "");
        var runner = new MockedRunner(pwsh, PrinterMocks);
        var result = await new PrinterInstaller(runner).InstallAsync(p, d, new InstallOptions());
        Equal(false, result.Success, "échec");
        Equal(1, result.Steps.Count, "s'arrête après le pilote");
        Contains(result.Steps[0].Detail ?? "", "INF introuvable", "message clair");
    });

    Test("PowerShell : dossier de scan avec cmdlets simulées (réseau public signalé)", async () =>
    {
        var runner = new MockedRunner(pwsh, ScanMocks);
        var o = new ScanFolderOptions { FolderPath = @"C:\Scans", Password = "Abcd-2345xyz", CreateDesktopShortcut = false };
        var r = await new ScanFolderInstaller(runner).CreateAsync(o);
        Equal(true, r.Success, "succès : " + r.RawOutput);
        Equal("PC-CLIENT", r.ComputerName, "nom du PC");
        Equal(@"\\PC-CLIENT\Scans", r.UncPath, "chemin UNC");
        var log = runner.LastOutput;
        Contains(log, "MOCK New-LocalUser scan pw=Abcd-2345xyz", "utilisateur créé avec le bon mot de passe");
        Contains(log, @"MOCK icacls C:\Scans /grant PC-CLIENT\scan:(OI)(CI)M", "droits NTFS");
        Contains(log, @"MOCK New-SmbShare Scans C:\Scans change=PC-CLIENT\scan full=BUILTIN\Administrateurs", "partage");
        Contains(log, "MOCK New-ItemProperty scan", "compte masqué");
        Equal(false, log.Contains("MOCK New-Item -Path HKLM"), "clé UserList existante non recréée (sinon ses valeurs seraient effacées)");
        Equal(true, r.Warnings.Any(w => w.Contains("Public")), "avertissement réseau public");
    });
}
else Console.WriteLine("(pwsh absent : tests PowerShell ignorés)");

UpdateTests.Register(Test, TestSync);

// ---------------------------------------------------------------- Exécution
var failed = 0;
var sw = Stopwatch.StartNew();
foreach (var (name, body) in tests)
{
    try
    {
        await body();
        Console.WriteLine($"  OK    {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  ÉCHEC {name}\n        {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} tests réussis en {sw.Elapsed.TotalSeconds:F1}s");
return failed == 0 ? 0 : 1;

static string? FindPwsh()
{
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        var f = Path.Combine(dir, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (File.Exists(f)) return f;
    }
    return null;
}

partial class Program
{
    // Cmdlets d'impression simulées : chaque appel est tracé par une ligne "MOCK ...".
    const string PrinterMocks = """
        function Get-PrinterDriver { [CmdletBinding()] param([string]$Name) $null }
        function Add-PrinterDriver { [CmdletBinding()] param([string]$Name) if ($Name -eq 'Pilote Inexistant') { throw 'introuvable' }; [Console]::Out.WriteLine("MOCK Add-PrinterDriver $Name") }
        function pnputil.exe { [Console]::Out.WriteLine("MOCK pnputil " + ($args -join ' ')); $global:LASTEXITCODE = 0; 'Pilote ajouté' }
        $script:ports = @(@{ Name = 'IP_192.168.1.99'; PrinterHostAddress = '192.168.1.99' }, @{ Name = '192.168.1.50'; PrinterHostAddress = '192.168.1.50' })
        function Get-PrinterPort { [CmdletBinding()] param([string]$Name)
            if ($Name) { $null } else { $script:ports | ForEach-Object { [pscustomobject]$_ } } }
        function Add-PrinterPort { [CmdletBinding()] param([string]$Name, [string]$PrinterHostAddress, [int]$SNMP, [string]$SNMPCommunity) [Console]::Out.WriteLine("MOCK Add-PrinterPort $Name $PrinterHostAddress snmp=$SNMP") }
        $script:printers = @(@{ Name = 'HP LaserJet Pro M404dn'; PortName = 'IP_192.168.1.99' }, @{ Name = 'HP OfficeJet copie Windows'; PortName = '192.168.1.50' })
        function Get-Printer { [CmdletBinding()] param([string]$Name)
            if ($Name) { $script:printers | Where-Object { $_.Name -eq $Name } | ForEach-Object { [pscustomobject]$_ } }
            else { $script:printers | ForEach-Object { [pscustomobject]$_ } } }
        function Remove-Printer { [CmdletBinding()] param([string]$Name) [Console]::Out.WriteLine("MOCK Remove-Printer $Name"); $script:printers = @($script:printers | Where-Object { $_.Name -ne $Name }) }
        function New-Item { [CmdletBinding()] param($Path, [switch]$Force) [Console]::Out.WriteLine("MOCK New-Item $Path") }
        function New-ItemProperty { [CmdletBinding()] param($Path, $Name, $Value, $PropertyType, [switch]$Force) [Console]::Out.WriteLine("MOCK New-ItemProperty $Path $Name=$Value") }
        function Add-Printer { [CmdletBinding()] param([string]$Name, [string]$DriverName, [string]$PortName, [string]$Location)
            [Console]::Out.WriteLine("MOCK Add-Printer $Name|$DriverName|$PortName"); $script:printers += @{ Name = $Name; PortName = $PortName } }
        function Set-PrintConfiguration { [CmdletBinding()] param([string]$PrinterName, [string]$DuplexingMode, [bool]$Color = $true)
            [Console]::Out.WriteLine("MOCK Set-PrintConfiguration $PrinterName duplex=$DuplexingMode color=$Color") }
        function Set-ItemProperty { [CmdletBinding()] param($Path, $Name, $Value, $Type) }
        function Get-CimInstance { [CmdletBinding()] param($ClassName, $Filter) [Console]::Out.WriteLine("MOCK $ClassName $Filter"); [pscustomobject]@{ Name = 'x' } }
        function Invoke-CimMethod { [CmdletBinding()] param($InputObject, $MethodName) [pscustomobject]@{ ReturnValue = 0 } }
        function Set-CimInstance { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject, $Property) process { [Console]::Out.WriteLine("MOCK Set-CimInstance SNMPEnabled=" + $Property.SNMPEnabled) } }
        function Set-Printer { [CmdletBinding()] param($Name, $Shared, $ShareName) [Console]::Out.WriteLine("MOCK Set-Printer $Name shared=$Shared share=$ShareName") }
        function Get-NetFirewallRule { [CmdletBinding()] param($Group) [pscustomobject]@{ Profile = 'Private' } }
        function Enable-NetFirewallRule { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject) process { [Console]::Out.WriteLine("MOCK firewall") } }
        function Start-Sleep { [CmdletBinding()] param($Seconds, $Milliseconds) }

        """;

    const string ScanMocks = """
        $env:COMPUTERNAME = 'PC-CLIENT'
        function Test-Path { [CmdletBinding()] param([Parameter(Position=0)]$Path, $LiteralPath) if ($Path -like 'HKLM:*') { $true } else { $false } }
        function New-Item { [CmdletBinding()] param($ItemType, $Path, [switch]$Force) [Console]::Out.WriteLine("MOCK New-Item -Path $Path") }
        function Get-LocalUser { [CmdletBinding()] param($Name) $null }
        function New-LocalUser { [CmdletBinding()] param($Name, [securestring]$Password, [switch]$PasswordNeverExpires, [switch]$UserMayNotChangePassword, [switch]$AccountNeverExpires, $Description)
            $plain = [System.Net.NetworkCredential]::new('', $Password).Password
            [Console]::Out.WriteLine("MOCK New-LocalUser $Name pw=$plain") }
        function New-ItemProperty { [CmdletBinding()] param($Path, $Name, $Value, $PropertyType, [switch]$Force) [Console]::Out.WriteLine("MOCK New-ItemProperty $Name") }
        function icacls.exe { [Console]::Out.WriteLine("MOCK icacls " + ($args -join ' ')); $global:LASTEXITCODE = 0 }
        function Get-Service { [CmdletBinding()] param($Name) [pscustomobject]@{ Status = 'Running' } }
        function New-Object { [CmdletBinding()] param([Parameter(Position=0)]$TypeName, [Parameter(Position=1)]$ArgumentList, $ComObject)
            $o = [pscustomobject]@{}
            $o | Add-Member -MemberType ScriptMethod -Name Translate -Value { param($t) [pscustomobject]@{ Value = 'BUILTIN\Administrateurs' } }
            $o }
        function Get-SmbShare { [CmdletBinding()] param($Name) $null }
        function New-SmbShare { [CmdletBinding()] param($Name, $Path, $ChangeAccess, $FullAccess) [Console]::Out.WriteLine("MOCK New-SmbShare $Name $Path change=$ChangeAccess full=$FullAccess") }
        function Get-NetFirewallRule { [CmdletBinding()] param($Group) [pscustomobject]@{ Profile = 'Private' } }
        function Enable-NetFirewallRule { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject) process { } }
        function Get-NetConnectionProfile { [CmdletBinding()] param() [pscustomobject]@{ Name = 'Reseau 3'; NetworkCategory = 'Public' } }
        function Get-SmbServerConfiguration { [pscustomobject]@{ EnableSMB1Protocol = $false } }

        """;
}
