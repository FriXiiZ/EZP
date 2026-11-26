using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using EFP.Core.Snmp;

namespace EFP.Core.Discovery;

public static class PrinterOids
{
    public const string SysDescr = "1.3.6.1.2.1.1.1.0";
    public const string SysName = "1.3.6.1.2.1.1.5.0";
    public const string HrDeviceType1 = "1.3.6.1.2.1.25.3.2.1.2.1";
    public const string HrDeviceDescr1 = "1.3.6.1.2.1.25.3.2.1.3.1";
    public const string PrtGeneralPrinterName = "1.3.6.1.2.1.43.5.1.1.16.1";
    public const string PrtGeneralSerialNumber = "1.3.6.1.2.1.43.5.1.1.17.1";
    public const string PwgDeviceId1284 = "1.3.6.1.4.1.2699.1.2.1.2.1.1.3.1";
    public const string HpDeviceId1284 = "1.3.6.1.4.1.11.2.3.9.1.1.7.0";
    public const string IfPhysAddress = "1.3.6.1.2.1.2.2.1.6";

    /// <summary>Valeur de hrDeviceType indiquant une imprimante.</summary>
    public const string HrDevicePrinter = "1.3.6.1.2.1.25.3.1.5";

    public static readonly string[] Identity =
    {
        SysDescr, SysName, HrDeviceType1, HrDeviceDescr1,
        PrtGeneralPrinterName, PrtGeneralSerialNumber, PwgDeviceId1284, HpDeviceId1284,
    };
}

public sealed class ScannerOptions
{
    public int[] ProbePorts { get; init; } = { 9100, 631, 515, 80, 443 };
    public TimeSpan TcpTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
    public int MaxParallel { get; init; } = 64;
    public string Community { get; init; } = "public";
    public int SnmpPort { get; init; } = 161;
    public TimeSpan SnmpTimeout { get; init; } = TimeSpan.FromMilliseconds(900);
    public bool ResolveHostNames { get; init; } = true;
}

public sealed record ScanProgress(int Done, int Total, int Found);

public sealed class PrinterScanner
{
    private readonly ScannerOptions _opt;

    public PrinterScanner(ScannerOptions? options = null) => _opt = options ?? new ScannerOptions();

    /// <summary>Scanne une liste d'adresses. <paramref name="onFound"/> est appelé dès qu'une imprimante est identifiée.</summary>
    public async Task<List<DiscoveredPrinter>> ScanAsync(
        IReadOnlyList<IPAddress> addresses,
        Action<DiscoveredPrinter>? onFound = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var found = new List<DiscoveredPrinter>();
        var done = 0;
        using var gate = new SemaphoreSlim(_opt.MaxParallel);
        var tasks = addresses.Select(async ip =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var printer = await ProbeAsync(ip, requirePrinterEvidence: true, ct).ConfigureAwait(false);
                if (printer is not null)
                {
                    lock (found) found.Add(printer);
                    onFound?.Invoke(printer);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // un hôte qui répond n'importe quoi ne doit pas arrêter le scan
            }
            finally
            {
                gate.Release();
                // Sous verrou pour que la progression rapportée ne recule jamais
                lock (found)
                {
                    done++;
                    progress?.Report(new ScanProgress(done, addresses.Count, found.Count));
                }
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found.OrderBy(p => IpRange.ToUInt(IPAddress.Parse(p.IpAddress))).ToList();
    }

    /// <summary>
    /// Interroge un hôte. Avec <paramref name="requirePrinterEvidence"/> = false (ajout manuel),
    /// l'hôte est retourné dès qu'il répond à quelque chose.
    /// </summary>
    public async Task<DiscoveredPrinter?> ProbeAsync(IPAddress ip, bool requirePrinterEvidence, CancellationToken ct = default)
    {
        var portsTask = ProbePortsAsync(ip, ct);
        var snmpTask = QuerySnmpAsync(ip, ct);
        await Task.WhenAll(portsTask, snmpTask).ConfigureAwait(false);

        var openPorts = portsTask.Result;
        var snmp = snmpTask.Result;

        var printer = new DiscoveredPrinter { IpAddress = ip.ToString() };
        printer.OpenPorts.AddRange(openPorts);
        if (snmp is not null) PrinterIdentifier.Apply(printer, snmp);

        var snmpSaysPrinter = snmp is not null && PrinterIdentifier.LooksLikePrinter(snmp);
        var isPrinter = snmpSaysPrinter || printer.HasRawPort;

        if (requirePrinterEvidence && !isPrinter) return null;
        if (!requirePrinterEvidence && snmp is null && openPorts.Count == 0) return null;

        if (snmp is not null && printer.MacAddress is null)
            printer.MacAddress = await ReadMacAsync(ip, ct).ConfigureAwait(false);

        if (_opt.ResolveHostNames)
            printer.HostName = await ReverseDnsAsync(ip).ConfigureAwait(false);

        return printer;
    }

    private async Task<List<int>> ProbePortsAsync(IPAddress ip, CancellationToken ct)
    {
        var checks = _opt.ProbePorts.Select(async port => (port, open: await IsTcpOpenAsync(ip, port, ct).ConfigureAwait(false)));
        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return results.Where(r => r.open).Select(r => r.port).OrderBy(p => p).ToList();
    }

    private async Task<bool> IsTcpOpenAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var client = new TcpClient(ip.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.TcpTimeout);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    private SnmpClient Snmp(int version) => new()
    {
        Community = _opt.Community,
        Port = _opt.SnmpPort,
        Timeout = _opt.SnmpTimeout,
        Version = version,
        Retries = 1,
    };

    private async Task<Dictionary<string, SnmpValue>?> QuerySnmpAsync(IPAddress ip, CancellationToken ct)
    {
        // v2c : une seule requête, les OID absents reviennent en noSuchObject.
        var v2 = await Snmp(1).GetAsync(ip, PrinterOids.Identity, ct).ConfigureAwait(false);
        if (v2 is not null)
            return v2.Where(v => v.HasValue).ToDictionary(v => v.Oid, v => v);

        // v1 : un OID absent fait échouer toute la requête -> on interroge un par un.
        var v1 = Snmp(0);
        var first = await v1.GetAsync(ip, new[] { PrinterOids.SysDescr }, ct).ConfigureAwait(false);
        if (first is null) return null;
        var dict = first.Where(v => v.HasValue).ToDictionary(v => v.Oid, v => v);
        foreach (var oid in PrinterOids.Identity.Skip(1))
        {
            var r = await v1.GetAsync(ip, new[] { oid }, ct).ConfigureAwait(false);
            if (r is { Count: > 0 } && r[0].HasValue) dict[oid] = r[0];
        }
        return dict;
    }

    private async Task<string?> ReadMacAsync(IPAddress ip, CancellationToken ct)
    {
        // Parcourt ifPhysAddress jusqu'à trouver une MAC non nulle (la 1re interface est souvent la loopback).
        var client = Snmp(1);
        var oid = PrinterOids.IfPhysAddress;
        for (var i = 0; i < 6; i++)
        {
            var r = await client.GetNextAsync(ip, new[] { oid }, ct).ConfigureAwait(false);
            if (r is not { Count: > 0 } || !r[0].HasValue) return null;
            if (!r[0].Oid.StartsWith(PrinterOids.IfPhysAddress + ".", StringComparison.Ordinal)) return null;
            var mac = r[0].AsMac();
            if (mac is not null) return mac;
            oid = r[0].Oid;
        }
        return null;
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress ip)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            var winner = await Task.WhenAny(lookup, Task.Delay(1200)).ConfigureAwait(false);
            if (winner != lookup) return null;
            var name = lookup.Result.HostName;
            return string.IsNullOrWhiteSpace(name) || name == ip.ToString() ? null : name;
        }
        catch { return null; }
    }
}

public static class PrinterIdentifier
{
    private static readonly Regex UselessSysDescr = new(
        @"ethernet|print(ing)? (system|server)|firmware|\brom\b|jetdirect|network|linux|vxworks|thinprint|embedded|version|ver\.|\d+\.\d+\.\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikePrinter(IReadOnlyDictionary<string, SnmpValue> snmp)
    {
        if (snmp.TryGetValue(PrinterOids.HrDeviceType1, out var type) && type.Text == PrinterOids.HrDevicePrinter) return true;
        if (snmp.ContainsKey(PrinterOids.PrtGeneralSerialNumber) || snmp.ContainsKey(PrinterOids.PrtGeneralPrinterName)) return true;
        if (snmp.ContainsKey(PrinterOids.PwgDeviceId1284) || snmp.ContainsKey(PrinterOids.HpDeviceId1284)) return true;
        return false;
    }

    public static void Apply(DiscoveredPrinter p, IReadOnlyDictionary<string, SnmpValue> snmp)
    {
        string? Get(string oid) => snmp.TryGetValue(oid, out var v) && !string.IsNullOrWhiteSpace(v.Text) ? v.Text : null;

        p.SnmpResponded = true;
        p.SysDescr = Get(PrinterOids.SysDescr);
        p.SysName = Get(PrinterOids.SysName);
        p.DeviceId1284 = Get(PrinterOids.PwgDeviceId1284) ?? Get(PrinterOids.HpDeviceId1284);

        var id = DeviceId1284.Parse(p.DeviceId1284);
        id.TryGetValue("MFG", out var mfg);
        id.TryGetValue("MDL", out var mdl);
        id.TryGetValue("SN", out var sn1284);

        var hrDescr = Get(PrinterOids.HrDeviceDescr1);
        var prtName = Get(PrinterOids.PrtGeneralPrinterName);

        p.Brand = Brands.Detect(mfg, hrDescr, mdl, prtName, p.SysDescr, p.SysName);

        // Choix du meilleur libellé de modèle
        string? model = mdl ?? hrDescr ?? prtName;
        if (model is null && p.SysDescr is not null && p.SysDescr.Length <= 60 && !UselessSysDescr.IsMatch(p.SysDescr))
            model = p.SysDescr;
        p.Model = ModelCleaner.Clean(model, p.Brand);

        var serial = Get(PrinterOids.PrtGeneralSerialNumber) ?? sn1284;
        p.SerialNumber = string.IsNullOrWhiteSpace(serial) ? null : serial.Trim();
    }
}
