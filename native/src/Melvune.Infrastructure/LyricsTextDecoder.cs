using System.Text;

namespace Melvune.Infrastructure;

public static class LyricsTextDecoder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 512000) throw new InvalidDataException("Lyrics are too large.");
        string result;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) result = new UTF32Encoding(false, false, true).GetString(bytes[4..]);
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) result = new UTF32Encoding(true, false, true).GetString(bytes[4..]);
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) result = new UnicodeEncoding(false, false, true).GetString(bytes[2..]);
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) result = new UnicodeEncoding(true, false, true).GetString(bytes[2..]);
        else if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) result = Utf8.GetString(bytes[3..]);
        else
        {
            try { result = Utf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                var legacy = CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                    ?? throw new InvalidDataException("Lyrics encoding unavailable.");
                result = legacy.GetString(bytes);
            }
        }
        if (result.Contains('\0')) throw new InvalidDataException("Invalid lyrics text.");
        return result;
    }
}
