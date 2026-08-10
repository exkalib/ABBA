using System.Globalization;

namespace NRftWManagerUI.Core;

internal sealed class AobPattern
{
    private AobPattern(byte?[] bytes)
    {
        Bytes = bytes;
    }

    public byte?[] Bytes { get; }

    public static AobPattern Parse(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte?[tokens.Length];

        for (var index = 0; index < tokens.Length; index++)
        {
            bytes[index] = tokens[index] is "?" or "??"
                ? null
                : byte.Parse(tokens[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return new AobPattern(bytes);
    }

    public bool IsMatch(ReadOnlySpan<byte> bytes, int start)
    {
        if (start + Bytes.Length > bytes.Length)
        {
            return false;
        }

        for (var index = 0; index < Bytes.Length; index++)
        {
            if (Bytes[index].HasValue && bytes[start + index] != Bytes[index]!.Value)
            {
                return false;
            }
        }

        return true;
    }
}
