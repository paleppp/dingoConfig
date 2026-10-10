using domain.Models;

namespace infrastructure.Adapters;

/// <summary>SLCAN (Lawicel) text of a standard 11 bit frame, the same as SlcanAdapter writes and reads</summary>
public static class SlcanCodec
{
    /// <summary>t, three id digits, the length, sixteen data digits and CR</summary>
    public const int MaxFrameBytes = 22;

    /// <summary>Writes the frame at offset. Returns the number of bytes written.</summary>
    public static int Encode(CanFrame frame, byte[] buffer, int offset)
    {
        var len = Math.Clamp(frame.Len, 0, 8);
        var n = offset;

        buffer[n++] = (byte)'t';
        buffer[n++] = Hex(frame.Id >> 8);
        buffer[n++] = Hex(frame.Id >> 4);
        buffer[n++] = Hex(frame.Id);
        buffer[n++] = Hex(len);

        for (var i = 0; i < len; i++)
        {
            var b = i < frame.Payload.Length ? frame.Payload[i] : 0;
            buffer[n++] = Hex(b >> 4);
            buffer[n++] = Hex(b);
        }

        buffer[n++] = (byte)'\r';
        return n - offset;
    }

    /// <summary>Reads a "t" line without its CR. Other lines (replies, extended frames) give false.</summary>
    public static bool TryDecode(string raw, out CanFrame frame)
    {
        frame = null!;
        if (raw.Length < 5 || raw[0] != 't') return false;

        var id = 0;
        for (var i = 1; i <= 3; i++)
        {
            var v = HexValue(raw[i]);
            if (v < 0) return false;
            id = (id << 4) | v;
        }

        var len = HexValue(raw[4]);
        if (len is < 0 or > 8) return false;

        // A timestamp may follow the data; it is not used
        if (raw.Length < 5 + len * 2) return false;

        var payload = new byte[len > 0 ? len : 8];
        for (var i = 0; i < len; i++)
        {
            var high = HexValue(raw[5 + i * 2]);
            var low = HexValue(raw[6 + i * 2]);
            if (high < 0 || low < 0) return false;
            payload[i] = (byte)((high << 4) | low);
        }

        frame = new CanFrame(id, len, payload);
        return true;
    }

    private static byte Hex(int value)
    {
        value &= 0xF;
        return (byte)(value < 10 ? '0' + value : 'A' + value - 10);
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1
    };
}
