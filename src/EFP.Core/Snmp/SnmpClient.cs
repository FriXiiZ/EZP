using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace EFP.Core.Snmp;

public enum SnmpValueKind { Integer, Text, Oid, IpAddress, Number, Null, Missing }

public sealed record SnmpValue(string Oid, SnmpValueKind Kind, string? Text, byte[] Raw, long Number = 0)
{
    public bool HasValue => Kind is not (SnmpValueKind.Null or SnmpValueKind.Missing);

    /// <summary>Formate la valeur brute comme adresse MAC si elle fait 6 octets.</summary>
    public string? AsMac() => Raw.Length == 6 && Raw.Any(b => b != 0)
        ? string.Join(":", Raw.Select(b => b.ToString("X2")))
        : null;
}

/// <summary>Client SNMP v1/v2c en UDP, sans dépendance externe.</summary>
public sealed class SnmpClient
{
    public string Community { get; init; } = "public";
    public int Port { get; init; } = 161;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(800);
    public int Retries { get; init; } = 1;

    /// <summary>0 = v1, 1 = v2c.</summary>
    public int Version { get; init; } = 1;

    public Task<IReadOnlyList<SnmpValue>?> GetAsync(IPAddress host, IReadOnlyList<string> oids, CancellationToken ct = default)
        => SendAsync(host, Ber.GetRequest, oids, ct);

    public Task<IReadOnlyList<SnmpValue>?> GetNextAsync(IPAddress host, IReadOnlyList<string> oids, CancellationToken ct = default)
        => SendAsync(host, Ber.GetNextRequest, oids, ct);

    public static byte[] BuildRequest(int version, string community, byte pduType, int requestId, IReadOnlyList<string> oids)
    {
        var varbinds = oids.Select(o => Ber.Tlv(Ber.Sequence, Ber.EncodeOid(o), Ber.EncodeNull())).ToArray();
        var pdu = Ber.Tlv(pduType,
            Ber.EncodeInteger(requestId),
            Ber.EncodeInteger(0),
            Ber.EncodeInteger(0),
            Ber.Tlv(Ber.Sequence, varbinds));
        return Ber.Tlv(Ber.Sequence, Ber.EncodeInteger(version), Ber.EncodeOctetString(community), pdu);
    }

    /// <summary>Décode une réponse. Retourne null si ce n'est pas la réponse attendue.</summary>
    public static (int RequestId, int ErrorStatus, List<SnmpValue> Values)? ParseResponse(byte[] data)
    {
        try
        {
            var span = data.AsSpan();
            var msg = Ber.Read(span, 0);
            if (msg.Tag != Ber.Sequence) return null;
            var parts = Ber.Children(span, msg);
            if (parts.Count < 3 || parts[2].Tag != Ber.GetResponse) return null;
            var pdu = Ber.Children(span, parts[2]);
            if (pdu.Count < 4) return null;
            var reqId = (int)Ber.DecodeInteger(span, pdu[0]);
            var err = (int)Ber.DecodeInteger(span, pdu[1]);
            var values = new List<SnmpValue>();
            foreach (var vb in Ber.Children(span, pdu[3]))
            {
                var pair = Ber.Children(span, vb);
                if (pair.Count < 2) continue;
                var oid = Ber.DecodeOid(span, pair[0]);
                var v = pair[1];
                var raw = span.Slice(v.ValueOffset, v.Length).ToArray();
                values.Add(v.Tag switch
                {
                    Ber.Integer => new SnmpValue(oid, SnmpValueKind.Integer, Ber.DecodeInteger(span, v).ToString(), raw, Ber.DecodeInteger(span, v)),
                    Ber.OctetString => new SnmpValue(oid, SnmpValueKind.Text, DecodeText(raw), raw),
                    Ber.ObjectId => new SnmpValue(oid, SnmpValueKind.Oid, Ber.DecodeOid(span, v), raw),
                    Ber.IpAddress => new SnmpValue(oid, SnmpValueKind.IpAddress, raw.Length == 4 ? new IPAddress(raw).ToString() : null, raw),
                    Ber.Counter32 or Ber.Gauge32 or Ber.TimeTicks or Ber.Counter64 =>
                        new SnmpValue(oid, SnmpValueKind.Number, Ber.DecodeUnsigned(span, v).ToString(), raw, (long)Ber.DecodeUnsigned(span, v)),
                    Ber.Null => new SnmpValue(oid, SnmpValueKind.Null, null, raw),
                    _ => new SnmpValue(oid, SnmpValueKind.Missing, null, raw), // noSuchObject / noSuchInstance / endOfMibView / inconnu
                });
            }
            return (reqId, err, values);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string DecodeText(byte[] raw)
    {
        // Les imprimantes renvoient de l'ASCII, parfois de l'UTF-8, parfois du Latin-1, souvent terminé par des \0.
        var len = raw.Length;
        while (len > 0 && raw[len - 1] == 0) len--;
        string s;
        try { s = new UTF8Encoding(false, true).GetString(raw, 0, len); }
        catch (DecoderFallbackException) { s = Encoding.Latin1.GetString(raw, 0, len); }
        return new string(s.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
    }

    private async Task<IReadOnlyList<SnmpValue>?> SendAsync(IPAddress host, byte pduType, IReadOnlyList<string> oids, CancellationToken ct)
    {
        var requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var packet = BuildRequest(Version, Community, pduType, requestId, oids);
        using var udp = new UdpClient(host.AddressFamily);
        var endpoint = new IPEndPoint(host, Port);

        for (var attempt = 0; attempt <= Retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await udp.SendAsync(packet, endpoint, ct).ConfigureAwait(false);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Timeout);
                while (true)
                {
                    var res = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    var parsed = ParseResponse(res.Buffer);
                    if (parsed is null || parsed.Value.RequestId != requestId) continue; // paquet parasite
                    if (parsed.Value.ErrorStatus != 0) return null; // v1 : noSuchName etc.
                    return parsed.Value.Values;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // délai dépassé -> nouvel essai
            }
            catch (SocketException)
            {
                return null; // port fermé (ICMP unreachable) ou réseau injoignable
            }
        }
        return null;
    }
}
