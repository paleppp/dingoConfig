using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace domain.Firmware;

/// <summary>
/// A Motorola S-record (.srec) firmware image as contiguous memory segments: what the OpenBLT
/// CAN bootloader is given to program. S1/S2/S3 data records (16/24/32-bit addresses) are read;
/// the others are skipped. Ported from CoffeeDingoConfig.
/// </summary>
public sealed class SRecordImage
{
    public sealed record Segment(uint Address, byte[] Data);

    public IReadOnlyList<Segment> Segments { get; }
    /// <summary>Lowest programmed address.</summary>
    public uint MinAddress { get; }
    /// <summary>Highest programmed address + 1.</summary>
    public uint EndAddress { get; }
    public int TotalBytes { get; }

    private SRecordImage(List<Segment> segments)
    {
        Segments = segments;
        MinAddress = segments.Count > 0 ? segments[0].Address : 0;
        EndAddress = segments.Count > 0 ? segments[^1].Address + (uint)segments[^1].Data.Length : 0;
        TotalBytes = segments.Sum(s => s.Data.Length);
    }

    /// <summary>
    /// The board the image is built for, from the firmware's "DINGO_BOARD=" marker without the
    /// vendor prefix, e.g. "dingoPDM v7". Null for an image without one.
    /// </summary>
    public string? BoardMarker() => BoardName(Marker(Segments.Select(s => s.Data), "DINGO_BOARD="));

    /// <summary>The firmware version from the "DINGO_FW=" marker, e.g. "0.6.1". Null without one.</summary>
    public string? VersionMarker() => Marker(Segments.Select(s => s.Data), "DINGO_FW=");

    /// <summary>"Dingo Electronics dingoPDM v7" -> "dingoPDM v7"</summary>
    public static string? BoardName(string? marker) =>
        marker is not null && marker.StartsWith("Dingo Electronics ") ? marker["Dingo Electronics ".Length..] : marker;

    /// <summary>The zero-terminated text after the key in the first block that has it.</summary>
    public static string? Marker(IEnumerable<byte[]> blocks, string key)
    {
        var k = Encoding.ASCII.GetBytes(key);
        foreach (var d in blocks)
        {
            var i = d.AsSpan().IndexOf(k);
            if (i < 0) continue;
            int start = i + k.Length, end = Array.IndexOf(d, (byte)0, start);
            return Encoding.ASCII.GetString(d, start, (end < 0 ? d.Length : end) - start);
        }
        return null;
    }

    /// <summary>"dingoPDM v7" is a "dingoPDM", "dingoPDM-Max v1" a "dingoPDM-Max", "CANBoard v2" a "CANBoard".</summary>
    public static bool BoardMatches(string marker, string boardName) =>
        Regex.Replace(marker, @"\s+v\d+$", "").Equals(boardName, StringComparison.OrdinalIgnoreCase);

    public static SRecordImage ParseText(string text) => Parse(text.Split('\n'));

    public static SRecordImage Parse(IEnumerable<string> lines)
    {
        var records = new List<(uint Address, byte[] Data)>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length < 10 || line[0] != 'S') continue;

            var addrBytes = line[1] switch { '1' => 2, '2' => 3, '3' => 4, _ => 0 };
            if (addrBytes == 0) continue; // not a data record

            var byteCount = Hex8(line, 2);
            var dataLen = byteCount - addrBytes - 1; // minus the address and the checksum
            var need = 4 + addrBytes * 2 + Math.Max(dataLen, 0) * 2 + 2;
            if (dataLen < 0 || line.Length < need)
                throw new InvalidDataException($"Truncated S-record line: {line[..Math.Min(line.Length, 20)]}...");

            var sum = byteCount;
            var pos = 4;
            uint addr = 0;
            for (var i = 0; i < addrBytes; i++)
            {
                var b = Hex8(line, pos);
                addr = (addr << 8) | (uint)b;
                sum += b;
                pos += 2;
            }

            var data = new byte[dataLen];
            for (var i = 0; i < dataLen; i++)
            {
                data[i] = (byte)Hex8(line, pos);
                sum += data[i];
                pos += 2;
            }

            // A corrupted file must not reach the flash
            if (((sum + Hex8(line, pos)) & 0xFF) != 0xFF)
                throw new InvalidDataException($"S-record checksum error at 0x{addr:X8}");

            if (dataLen > 0) records.Add((addr, data));
        }

        if (records.Count == 0)
            throw new InvalidDataException("No S1/S2/S3 data records: not an S-record file.");

        // One segment per contiguous run; records may come out of order
        records.Sort((a, b) => a.Address.CompareTo(b.Address));
        var segments = new List<Segment>();
        var start = records[0].Address;
        var expected = start;
        var current = new List<byte>();
        foreach (var (addr, data) in records)
        {
            if (addr != expected && current.Count > 0)
            {
                segments.Add(new Segment(start, current.ToArray()));
                current = [];
                start = addr;
            }
            else if (current.Count == 0)
            {
                start = addr;
            }
            current.AddRange(data);
            expected = addr + (uint)data.Length;
        }
        if (current.Count > 0) segments.Add(new Segment(start, current.ToArray()));

        return new SRecordImage(segments);
    }

    private static int Hex8(string s, int pos) =>
        int.Parse(s.AsSpan(pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
