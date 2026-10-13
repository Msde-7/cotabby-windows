using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Log-formatting helpers for the detailed-monitoring pipeline. Insertion
/// bugs are diagnosed by joining what the coordinator snapshotted against
/// what the inserter sent, byte-for-byte — both sides must render strings
/// identically for that diff to be trustworthy, so the formatting lives here
/// rather than as per-class copies.
/// </summary>
public static class LogText
{
    /// <summary>
    /// Escape control chars and newlines so a single-line log entry shows the
    /// FULL string verbatim.
    /// </summary>
    public static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append($"\\x{(int)c:X2}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>UTF-16 hex dump of <paramref name="s"/>. Lets us spot doubled
    /// chars, invisible whitespace, or off-by-one differences between snapshot
    /// and inserter at a glance.</summary>
    public static string Hex(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length * 5);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(((int)s[i]).ToString("X4"));
        }
        return sb.ToString();
    }
}
