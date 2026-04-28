namespace Tamma.Api.Auth;

/// <summary>
/// RFC 4648 §6 base32 encoder/decoder (no padding, uppercase alphabet on
/// encode, case-insensitive on decode). Used by Story 28-7 to embed the
/// 16-byte tenant id into the API-key prefix as a fixed-width 26-character
/// segment.
///
/// <para>Why base32 and not base64url: base32 is case-insensitive on the
/// wire so operators copying keys from a terminal don't need to fight with
/// shells that lowercase input. The 26-char fixed length also makes prefix
/// parsing trivial (no padding to strip and no variable-length surprise
/// when a UUID happens to round to a different boundary).</para>
/// </summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Encodes <paramref name="bytes"/> to RFC-4648 base32 with no padding.
    /// 16 bytes of input → 26 characters of output (the parsing path
    /// depends on this exact width).
    /// </summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        // 5 bits per char; ceiling(8 * len / 5).
        var charCount = (bytes.Length * 8 + 4) / 5;
        var output = new char[charCount];
        var bitBuffer = 0;
        var bitsLeft = 0;
        var outputIndex = 0;

        foreach (var b in bytes)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                var idx = (bitBuffer >> bitsLeft) & 0x1F;
                output[outputIndex++] = Alphabet[idx];
            }
        }

        if (bitsLeft > 0)
        {
            var idx = (bitBuffer << (5 - bitsLeft)) & 0x1F;
            output[outputIndex++] = Alphabet[idx];
        }

        return new string(output, 0, outputIndex);
    }

    /// <summary>
    /// Decodes a (possibly mixed-case) base32 string to bytes. Returns
    /// <see langword="null"/> on invalid characters or on inputs that do
    /// not byte-align cleanly (i.e. the final partial group has stray bits).
    /// </summary>
    public static byte[]? TryDecode(string s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();

        var byteCount = s.Length * 5 / 8;
        var output = new byte[byteCount];
        var bitBuffer = 0;
        var bitsLeft = 0;
        var outputIndex = 0;

        foreach (var c in s)
        {
            int idx;
            if (c >= 'A' && c <= 'Z') idx = c - 'A';
            else if (c >= 'a' && c <= 'z') idx = c - 'a';
            else if (c >= '2' && c <= '7') idx = c - '2' + 26;
            else return null;

            bitBuffer = (bitBuffer << 5) | idx;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output[outputIndex++] = (byte)((bitBuffer >> bitsLeft) & 0xFF);
            }
        }

        // Any leftover bits should be zero-valued padding. If not, the input
        // was truncated mid-group and we reject it.
        if (bitsLeft >= 5) return null;
        var leftover = bitBuffer & ((1 << bitsLeft) - 1);
        if (leftover != 0) return null;

        return output;
    }
}
