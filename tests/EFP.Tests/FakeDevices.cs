using System.Net;
using System.Net.Sockets;
using System.Text;
using EFP.Core.Snmp;

namespace EFP.Tests;

/// <summary>Faux agent SNMP en UDP local, pour tester le scan sans vraie imprimante.</summary>
public sealed class FakeSnmpAgent : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly SortedDictionary<string, object> _values;

    /// <summary>Si vrai, se comporte comme un vieil agent SNMP v1 strict.</summary>
    public bool V1Only { get; init; }
    public int Port { get; }
    public int RequestsReceived;

    public FakeSnmpAgent(Dictionary<string, object> values)
    {
        _values = new SortedDictionary<string, object>(values, OidComparer.Instance);
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult req;
            try { req = await _udp.ReceiveAsync(_cts.Token); }
            catch { return; }
            Interlocked.Increment(ref RequestsReceived);
            var resp = Handle(req.Buffer);
            if (resp is not null) await _udp.SendAsync(resp, req.RemoteEndPoint);
        }
    }

    private byte[]? Handle(byte[] data)
    {
        var span = data.AsSpan();
        var msg = Ber.Read(span, 0);
        var parts = Ber.Children(span, msg);
        var version = (int)Ber.DecodeInteger(span, parts[0]);
        var community = Encoding.ASCII.GetString(data, parts[1].ValueOffset, parts[1].Length);
        if (community != "public") return null;
        if (V1Only && version != 0) return null; // les vieux agents ignorent simplement le v2c
        var pduType = parts[2].Tag;
        var pdu = Ber.Children(span, parts[2]);
        var reqId = Ber.DecodeInteger(span, pdu[0]);
        var oids = new List<string>();
        foreach (var vb in Ber.Children(span, pdu[3])) oids.Add(Ber.DecodeOid(span, Ber.Children(span, vb)[0]));

        var errorStatus = 0;
        var errorIndex = 0;
        var varbinds = new List<byte[]>();
        for (var i = 0; i < oids.Count; i++)
        {
            var oid = oids[i];
            if (pduType == Ber.GetNextRequest)
            {
                var next = _values.Keys.FirstOrDefault(k => OidComparer.Instance.Compare(k, oid) > 0);
                if (next is null) varbinds.Add(Ber.Tlv(Ber.Sequence, Ber.EncodeOid(oid), new byte[] { Ber.EndOfMibView, 0 }));
                else varbinds.Add(Ber.Tlv(Ber.Sequence, Ber.EncodeOid(next), Encode(_values[next])));
                continue;
            }
            if (_values.TryGetValue(oid, out var v))
                varbinds.Add(Ber.Tlv(Ber.Sequence, Ber.EncodeOid(oid), Encode(v)));
            else if (version == 0)
            {
                errorStatus = 2; // noSuchName
                errorIndex = i + 1;
                varbinds.Add(Ber.Tlv(Ber.Sequence, Ber.EncodeOid(oid), Ber.EncodeNull()));
            }
            else
                varbinds.Add(Ber.Tlv(Ber.Sequence, Ber.EncodeOid(oid), new byte[] { Ber.NoSuchObject, 0 }));
        }

        var respPdu = Ber.Tlv(Ber.GetResponse,
            Ber.EncodeInteger(reqId), Ber.EncodeInteger(errorStatus), Ber.EncodeInteger(errorIndex),
            Ber.Tlv(Ber.Sequence, varbinds.ToArray()));
        return Ber.Tlv(Ber.Sequence, Ber.EncodeInteger(version), Ber.EncodeOctetString(community), respPdu);
    }

    private static byte[] Encode(object v) => v switch
    {
        string s when s.StartsWith("oid:") => Ber.EncodeOid(s[4..]),
        string s => Ber.Tlv(Ber.OctetString, Encoding.UTF8.GetBytes(s)),
        byte[] b => Ber.Tlv(Ber.OctetString, b),
        int i => Ber.EncodeInteger(i),
        _ => Ber.EncodeNull(),
    };

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }

    private sealed class OidComparer : IComparer<string>
    {
        public static readonly OidComparer Instance = new();
        public int Compare(string? a, string? b)
        {
            var x = a!.Split('.').Select(ulong.Parse).ToArray();
            var y = b!.Split('.').Select(ulong.Parse).ToArray();
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return x.Length.CompareTo(y.Length);
        }
    }
}

/// <summary>Faux port TCP (9100) qui accepte les connexions.</summary>
public sealed class FakeTcpPort : IDisposable
{
    private readonly TcpListener _listener;
    public int Port { get; }

    public FakeTcpPort()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try { while (true) (await _listener.AcceptTcpClientAsync()).Dispose(); }
            catch { /* arrêt */ }
        });
    }

    public void Dispose() => _listener.Stop();
}
