using System.Globalization;
using System.Text.RegularExpressions;

namespace FnMusic.Core;

public sealed record LyricLine(double Seconds, string Text);
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool IsTimed)
{
    public static Lyrics Empty { get; } = new([], false);
    public static Lyrics Parse(string text, double delayMilliseconds = 0)
    {
        if (text.Length > 512000 || !double.IsFinite(delayMilliseconds)) throw new ArgumentException("Invalid lyrics");
        var stamps = new Regex(@"\[(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var offsetMatch = Regex.Match(text, @"\[offset:([+-]?\d{1,8})\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        double offset = offsetMatch.Success ? double.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var timed = new List<LyricLine>(); var plain = new List<LyricLine>();
        long expandedCharacters = 0;
        foreach (string row in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var matches = stamps.Matches(row);
            if (matches.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(row) && !Regex.IsMatch(row.Trim(), @"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase))
                    plain.Add(new(0, row.Trim()));
                continue;
            }
            string words = stamps.Replace(row, "").Trim();
            foreach (Match match in matches)
            {
                expandedCharacters += words.Length + 1;
                if (expandedCharacters > 2_000_000) throw new ArgumentException("Expanded lyrics exceed limit");
                double seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (match.Groups[3].Success) seconds += double.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture);
                timed.Add(new(Math.Max(0, seconds + (delayMilliseconds - offset) / 1000), words));
                if (timed.Count > 10000) throw new ArgumentException("Too many lyric lines");
            }
        }
        // 同时戳合并，保留翻译/双语行，避免高亮只显示其中一句。
        return timed.Count > 0
            ? new(timed.OrderBy(line => line.Seconds).GroupBy(line => line.Seconds).Select(group => new LyricLine(group.Key, string.Join("\n", group.Select(line => line.Text)))).ToArray(), true)
            : new(plain.ToArray(), false);
    }
    public int FindLine(double seconds)
    {
        if (!IsTimed || !double.IsFinite(seconds)) return -1;
        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (Lines[middle].Seconds <= seconds) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found;
    }
}
