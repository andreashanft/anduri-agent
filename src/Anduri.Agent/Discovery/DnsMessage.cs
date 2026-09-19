using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Anduri.Agent.Discovery;

public static class DnsType
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Any = 255;
}

public sealed record DnsQuestion(string Name, ushort Type, bool UnicastResponse);

public sealed record DnsRecord(string Name, ushort Type, bool CacheFlush, uint Ttl, byte[] Data)
{
    public static DnsRecord Ptr(string name, string target, uint ttl) => new(name, DnsType.Ptr, false, ttl, DnsMessage.EncodeName(target));

    public static DnsRecord Srv(string name, string target, int port, uint ttl)
    {
        var targetBytes = DnsMessage.EncodeName(target);
        var data = new byte[6 + targetBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)port);
        targetBytes.CopyTo(data, 6);
        return new DnsRecord(name, DnsType.Srv, true, ttl, data);
    }

    public static DnsRecord Txt(string name, IEnumerable<string> strings, uint ttl)
    {
        using var stream = new MemoryStream();
        foreach (var text in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 255)
                bytes = bytes[..255];
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        return new DnsRecord(name, DnsType.Txt, true, ttl, stream.ToArray());
    }

    public static DnsRecord A(string name, IPAddress address, uint ttl) => new(name, DnsType.A, true, ttl, address.GetAddressBytes());
}

/// <summary>Just enough of the DNS wire format (RFC 1035) for an mDNS responder.</summary>
public sealed record DnsMessage(ushort Id, bool IsResponse, IReadOnlyList<DnsQuestion> Questions, IReadOnlyList<DnsRecord> Answers, IReadOnlyList<DnsRecord> Additionals)
{
    private const ushort ResponseFlags = 0x8400; // QR + AA

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, Id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], IsResponse ? ResponseFlags : (ushort)0);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)Answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)Additionals.Count);
        stream.Write(header);

        Span<byte> buffer = stackalloc byte[10];
        foreach (var question in Questions)
        {
            stream.Write(EncodeName(question.Name));
            BinaryPrimitives.WriteUInt16BigEndian(buffer, question.Type);
            BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)(question.UnicastResponse ? 0x8001 : 0x0001));
            stream.Write(buffer[..4]);
        }

        foreach (var record in Answers.Concat(Additionals))
        {
            stream.Write(EncodeName(record.Name));
            BinaryPrimitives.WriteUInt16BigEndian(buffer, record.Type);
            BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)(record.CacheFlush ? 0x8001 : 0x0001));
            BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], record.Ttl);
            BinaryPrimitives.WriteUInt16BigEndian(buffer[8..], (ushort)record.Data.Length);
            stream.Write(buffer);
            stream.Write(record.Data);
        }

        return stream.ToArray();
    }

    /// <exception cref="FormatException">The packet is truncated or malformed.</exception>
    public static DnsMessage Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12)
            throw new FormatException("DNS packet shorter than its header.");

        var id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
        int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);

        var offset = 12;
        var questions = new List<DnsQuestion>();
        for (var i = 0; i < questionCount; i++)
        {
            var name = DecodeName(packet, ref offset);
            EnsureAvailable(packet, offset, 4);
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var @class = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4;
            questions.Add(new DnsQuestion(name, type, (@class & 0x8000) != 0));
        }

        var answers = ReadRecords(packet, ref offset, answerCount);
        ReadRecords(packet, ref offset, authorityCount);
        var additionals = ReadRecords(packet, ref offset, additionalCount);
        return new DnsMessage(id, (flags & 0x8000) != 0, questions, answers, additionals);
    }

    public static byte[] EncodeName(string name)
    {
        using var stream = new MemoryStream();
        foreach (var label in SplitLabels(name))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
                bytes = bytes[..63];
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        return stream.ToArray();
    }

    /// <summary>Decodes a name at <paramref name="offset"/>, following compression pointers.</summary>
    public static string DecodeName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            EnsureAvailable(packet, position, 1);
            var length = packet[position];
            if (length == 0)
            {
                if (!jumped)
                    offset = position + 1;
                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(packet, position, 2);
                var pointer = BinaryPrimitives.ReadUInt16BigEndian(packet[position..]) & 0x3FFF;
                if (!jumped)
                    offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }

            EnsureAvailable(packet, position + 1, length);
            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }

        throw new FormatException("DNS name has too many labels or a pointer loop.");
    }

    /// <summary>Reads a PTR or SRV target name from record data.</summary>
    public static string DecodeNameFromData(byte[] data, int start = 0)
    {
        var offset = start;
        return DecodeName(data, ref offset);
    }

    public static IReadOnlyList<string> DecodeTxt(byte[] data)
    {
        var result = new List<string>();
        for (var i = 0; i < data.Length;)
        {
            var length = data[i];
            if (i + 1 + length > data.Length)
                break;
            result.Add(Encoding.UTF8.GetString(data, i + 1, length));
            i += 1 + length;
        }
        return result;
    }

    // Instance names may contain dots ("Homer’s PC 2.0"), so labels are split only at the known service suffix.
    private static IEnumerable<string> SplitLabels(string name)
    {
        var trimmed = name.TrimEnd('.');
        const string suffix = "._anduri._tcp.local";
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length)
        {
            yield return trimmed[..^suffix.Length];
            foreach (var label in suffix[1..].Split('.'))
                yield return label;
            yield break;
        }

        foreach (var label in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
            yield return label;
    }

    private static List<DnsRecord> ReadRecords(ReadOnlySpan<byte> packet, ref int offset, int count)
    {
        var records = new List<DnsRecord>();
        for (var i = 0; i < count; i++)
        {
            var name = DecodeName(packet, ref offset);
            EnsureAvailable(packet, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var @class = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            EnsureAvailable(packet, offset, length);

            // Names inside PTR/SRV data may be compressed against the whole packet; expand them so data stands alone.
            byte[] data;
            if (type == DnsType.Ptr)
            {
                var dataOffset = offset;
                data = EncodeName(DecodeName(packet, ref dataOffset));
            }
            else if (type == DnsType.Srv && length >= 7)
            {
                var dataOffset = offset + 6;
                var target = EncodeName(DecodeName(packet, ref dataOffset));
                data = [.. packet.Slice(offset, 6), .. target];
            }
            else
            {
                data = packet.Slice(offset, length).ToArray();
            }

            offset += length;
            records.Add(new DnsRecord(name, type, (@class & 0x8000) != 0, ttl, data));
        }

        return records;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> packet, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > packet.Length)
            throw new FormatException("DNS packet is truncated.");
    }
}
