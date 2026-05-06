namespace HistoricalData.Utils;

public static class BigEndian
{
    public static int ReadInt32(ReadOnlySpan<byte> buffer, int offset)
    {
        return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
    {
        return (uint)ReadInt32(buffer, offset);
    }

    public static float ReadSingle(ReadOnlySpan<byte> buffer, int offset)
    {
        var temp = buffer.Slice(offset, 4);
        if (BitConverter.IsLittleEndian)
        {
            Span<byte> reversed = stackalloc byte[4];
            reversed[0] = temp[3];
            reversed[1] = temp[2];
            reversed[2] = temp[1];
            reversed[3] = temp[0];
            return BitConverter.ToSingle(reversed);
        }

        return BitConverter.ToSingle(temp);
    }

    public static double ReadDouble(ReadOnlySpan<byte> buffer, int offset)
    {
        var temp = buffer.Slice(offset, 8);
        if (BitConverter.IsLittleEndian)
        {
            Span<byte> reversed = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
            {
                reversed[i] = temp[7 - i];
            }
            return BitConverter.ToDouble(reversed);
        }

        return BitConverter.ToDouble(temp);
    }
}
