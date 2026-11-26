using System.Text;

namespace EFP.Core.Snmp;

/// <summary>Encodage / décodage BER minimal, suffisant pour SNMP v1/v2c (Get / GetNext).</summary>
internal static class Ber
{
    public const byte Integer = 0x02;
    public const byte OctetString = 0x04;
    public const byte Null = 0x05;
    public const byte ObjectId = 0x06;
    public const byte Sequence = 0x30;
    public const byte IpAddress = 0x40;
    public const byte Counter32 = 0x41;
    public const byte Gauge32 = 0x42;
    public const byte TimeTicks = 0x43;
    public const byte Opaque = 0x44;
    public const byte Counter64 = 0x46;
    public const byte NoSuchObject = 0x80;
    public const byte NoSuchInstance = 0x81;
    public const byte EndOfMibView = 0x82;

    public const byte GetRequest = 0xA0;
    public const byte GetNextRequest = 0xA1;
    public const byte GetResponse = 0xA2;

    // ---------- Encodage ----------

    public static byte[] Tlv(byte tag, byte[] value)
    {
        var len = EncodeLength(value.Length);
        var buf = new byte[1 + len.Length + value.Length];
        buf[0] = tag;
        len.CopyTo(buf, 1);
        value.CopyTo(buf, 1 + len.Length);
        return buf;
    }

    public static byte[] Tlv(byte tag, params byte[][] children) => Tlv(tag, Concat(children));

    public static byte[] EncodeLength(int length)
    {
        if (length < 0x80) return new[] { (byte)length };
        var bytes = new List<byte>();
        for (var l = length; l > 0; l >>= 8) bytes.Insert(0, (byte)(l & 0xFF));
        bytes.Insert(0, (byte)(0x80 | bytes.Count));
        return bytes.ToArray();
    }

    public static byte[] EncodeInteger(long value)
    {
        // Complément à deux, forme minimale
        var bytes = new List<byte>();
        var v = value;
        do
        {
            bytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
        } while (!(v == 0 && (bytes[0] & 0x80) == 0) && !(v == -1 && (bytes[0] & 0x80) != 0));
        return Tlv(Integer, bytes.ToArray());
    }

    public static byte[] EncodeOctetString(string s) => Tlv(OctetString, Encoding.ASCII.GetBytes(s));

    public static byte[] EncodeNull() => new byte[] { Null, 0x00 };

    public static byte[] EncodeOid(string oid)
    {
        var arcs = oid.Trim('.').Split('.').Select(ulong.Parse).ToArray();
        if (arcs.Length < 2) throw new ArgumentException($"OID invalide : {oid}");
        var body = new List<byte>();
        AppendBase128(body, arcs[0] * 40 + arcs[1]);
        for (var i = 2; i < arcs.Length; i++) AppendBase128(body, arcs[i]);
        return Tlv(ObjectId, body.ToArray());
    }

    private static void AppendBase128(List<byte> buf, ulong value)
    {
        var stack = new Stack<byte>();
        stack.Push((byte)(value & 0x7F));
        value >>= 7;
        while (value > 0)
        {
            stack.Push((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }
        buf.AddRange(stack);
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var buf = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(buf, o); o += p.Length; }
        return buf;
    }

    // ---------- Décodage ----------

    public readonly record struct Node(byte Tag, int ValueOffset, int Length)
    {
        public int End => ValueOffset + Length;
    }

    public static Node Read(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 2 > data.Length) throw new FormatException("BER tronqué");
        var tag = data[offset];
        int len = data[offset + 1];
        var pos = offset + 2;
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            if (n is 0 or > 4 || pos + n > data.Length) throw new FormatException("Longueur BER invalide");
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | data[pos + i];
            pos += n;
        }
        if (len < 0 || pos + len > data.Length) throw new FormatException("BER tronqué");
        return new Node(tag, pos, len);
    }

    public static List<Node> Children(ReadOnlySpan<byte> data, Node parent)
    {
        var list = new List<Node>();
        var pos = parent.ValueOffset;
        while (pos < parent.End)
        {
            var child = Read(data, pos);
            if (child.End > parent.End) throw new FormatException("Enfant BER hors limites");
            list.Add(child);
            pos = child.End;
        }
        return list;
    }

    public static long DecodeInteger(ReadOnlySpan<byte> data, Node n)
    {
        if (n.Length == 0) return 0;
        long v = (sbyte)data[n.ValueOffset]; // signe
        for (var i = 1; i < n.Length; i++) v = (v << 8) | data[n.ValueOffset + i];
        return v;
    }

    public static ulong DecodeUnsigned(ReadOnlySpan<byte> data, Node n)
    {
        ulong v = 0;
        for (var i = 0; i < n.Length; i++) v = (v << 8) | data[n.ValueOffset + i];
        return v;
    }

    public static string DecodeOid(ReadOnlySpan<byte> data, Node n)
    {
        var arcs = new List<ulong>();
        ulong cur = 0;
        for (var i = 0; i < n.Length; i++)
        {
            var b = data[n.ValueOffset + i];
            cur = (cur << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                if (arcs.Count == 0)
                {
                    var first = Math.Min(cur / 40, 2);
                    arcs.Add(first);
                    arcs.Add(cur - first * 40);
                }
                else arcs.Add(cur);
                cur = 0;
            }
        }
        return string.Join('.', arcs);
    }
}
