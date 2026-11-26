using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EFP.Core.Discovery;

public sealed record LocalSubnet(string InterfaceName, IPAddress Address, int PrefixLength)
{
    public string Cidr => $"{IpRange.NetworkAddress(Address, PrefixLength)}/{PrefixLength}";
    public override string ToString() => $"{InterfaceName} — {Address}/{PrefixLength}";
}

public static class IpRange
{
    /// <summary>Taille maximale scannée en une fois (évite de lancer un scan sur un /16 par erreur).</summary>
    public const int MaxHosts = 4096;

    /// <summary>
    /// Accepte : "192.168.1.25", "192.168.1.0/24", "192.168.1.10-192.168.1.60", "192.168.1.10-60",
    /// plusieurs entrées séparées par des virgules, points-virgules ou espaces.
    /// </summary>
    public static List<IPAddress> Parse(string input)
    {
        var result = new List<IPAddress>();
        var seen = new HashSet<uint>();
        foreach (var token in input.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ip in ParseToken(token.Trim()))
            {
                if (seen.Add(ToUInt(ip))) result.Add(ip);
                if (result.Count > MaxHosts)
                    throw new FormatException($"Plage trop grande (plus de {MaxHosts} adresses).");
            }
        }
        return result;
    }

    private static IEnumerable<IPAddress> ParseToken(string token)
    {
        if (token.Contains('/'))
        {
            var parts = token.Split('/');
            if (parts.Length != 2 || !TryParseV4(parts[0], out var ip) || !int.TryParse(parts[1], out var prefix) || prefix is < 8 or > 32)
                throw new FormatException($"Plage CIDR invalide : {token}");
            return Hosts(ip, prefix);
        }
        if (token.Contains('-'))
        {
            var parts = token.Split('-');
            if (parts.Length != 2 || !TryParseV4(parts[0], out var start))
                throw new FormatException($"Plage invalide : {token}");
            IPAddress end;
            if (byte.TryParse(parts[1], out var lastOctet))
            {
                var b = start.GetAddressBytes();
                b[3] = lastOctet;
                end = new IPAddress(b);
            }
            else if (!TryParseV4(parts[1], out end!))
                throw new FormatException($"Plage invalide : {token}");
            var s = ToUInt(start);
            var e = ToUInt(end);
            if (e < s) (s, e) = (e, s);
            if (e - s + 1 > MaxHosts) throw new FormatException($"Plage trop grande : {token}");
            return Enumerable.Range(0, (int)(e - s + 1)).Select(i => FromUInt(s + (uint)i));
        }
        if (TryParseV4(token, out var single)) return new[] { single };
        throw new FormatException($"Adresse invalide : {token}");
    }

    /// <summary>Adresses hôtes d'un réseau (sans adresse réseau ni broadcast pour les préfixes &lt; 31).</summary>
    public static IEnumerable<IPAddress> Hosts(IPAddress anyAddressInNetwork, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var net = ToUInt(anyAddressInNetwork) & mask;
        var count = prefix >= 32 ? 1UL : 1UL << (32 - prefix);
        if (count > MaxHosts) throw new FormatException($"Réseau /{prefix} trop grand ({count} adresses).");
        if (prefix >= 31)
        {
            for (ulong i = 0; i < count; i++) yield return FromUInt(net + (uint)i);
            yield break;
        }
        for (ulong i = 1; i < count - 1; i++) yield return FromUInt(net + (uint)i);
    }

    public static IPAddress NetworkAddress(IPAddress ip, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return FromUInt(ToUInt(ip) & mask);
    }

    /// <summary>Réseaux IPv4 des cartes actives (hors loopback, APIPA, VPN/tunnels évidents).</summary>
    public static List<LocalSubnet> GetLocalSubnets()
    {
        var list = new List<LocalSubnet>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return list; } // pile réseau inaccessible : saisie manuelle de la plage
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var name = nic.Name + " " + nic.Description;
            if (name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TAP-", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
                continue;
            UnicastIPAddressInformationCollection addresses;
            try { addresses = nic.GetIPProperties().UnicastAddresses; }
            catch (NetworkInformationException) { continue; }
            foreach (var ua in addresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                var prefix = ua.PrefixLength;
                if (prefix is <= 0 or > 32) prefix = 24;
                list.Add(new LocalSubnet(nic.Name, ua.Address, prefix));
            }
        }
        return list;
    }

    /// <summary>Plage à scanner par défaut pour un réseau local : le réseau entier s'il est petit, sinon le /24 autour du PC.</summary>
    public static string DefaultScanRange(LocalSubnet subnet)
    {
        var prefix = subnet.PrefixLength < 22 ? 24 : subnet.PrefixLength;
        return $"{NetworkAddress(subnet.Address, prefix)}/{prefix}";
    }

    public static bool TryParseV4(string s, out IPAddress ip)
    {
        if (IPAddress.TryParse(s, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && s.Count(c => c == '.') == 3)
        {
            ip = parsed;
            return true;
        }
        ip = IPAddress.None;
        return false;
    }

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress FromUInt(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
