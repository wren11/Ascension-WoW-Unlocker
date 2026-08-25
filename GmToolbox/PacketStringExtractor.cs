using System.Text;

namespace AscensionNetTool;

/// <summary>Extract printable C-strings / UTF-8 runs from raw packet payloads for DB logging.</summary>
static class PacketStringExtractor
{
    public sealed class Hit
    {
        public int Offset;
        public string Text = "";
    }

    public static List<Hit> Extract(ReadOnlySpan<byte> data, int minLen = 4, int maxHits = 32)
    {
        var hits = new List<Hit>();
        int i = 0;
        while (i < data.Length && hits.Count < maxHits)
        {
            // Skip opcode prefix if present-looking
            if (i == 0 && data.Length >= 4) { /* fall through */ }

            if (!IsStart(data[i])) { i++; continue; }
            int start = i;
            int j = i;
            int printable = 0;
            while (j < data.Length && j - start < 512)
            {
                byte b = data[j];
                if (b == 0) break;
                if (IsPrintable(b)) printable++;
                else if (IsUtf8Cont(b) || IsUtf8Lead(b)) { /* keep */ }
                else break;
                j++;
            }
            int len = j - start;
            bool terminated = j < data.Length && data[j] == 0;
            if (len >= minLen && printable * 10 >= len * 7 && (terminated || len >= 8))
            {
                string text = Encoding.UTF8.GetString(data.Slice(start, len)).Trim();
                if (text.Length >= minLen && !IsMostlyHex(text) && LooksUseful(text))
                    hits.Add(new Hit { Offset = start, Text = text });
                i = terminated ? j + 1 : j;
            }
            else i++;
        }
        return hits;
    }

    static bool IsStart(byte b) => (b >= 0x20 && b < 0x7F) || b >= 0xC0;
    static bool IsPrintable(byte b) => b >= 0x20 && b < 0x7F;
    static bool IsUtf8Cont(byte b) => (b & 0xC0) == 0x80;
    static bool IsUtf8Lead(byte b) => b >= 0xC0 && b < 0xF5;

    static bool IsMostlyHex(string s)
    {
        if (s.Length < 12) return false;
        int hex = 0;
        foreach (char c in s)
            if (Uri.IsHexDigit(c)) hex++;
        return hex * 10 >= s.Length * 9;
    }

    static bool LooksUseful(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Avoid pure numeric noise and tiny tokens
        if (s.Length <= 5 && s.All(char.IsDigit)) return false;
        int letters = s.Count(char.IsLetter);
        return letters >= 2 || s.Contains(' ') || s.Contains(':');
    }
}
