using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Log formatting shared by the coordinator and the inserter. Insertion bugs are diagnosed by
/// comparing what each side logged, so both must render strings identically.
/// </summary>
public static class LogText
{
    /// <summary>
    /// Escapes control characters and newlines so a single-line log entry shows the whole string.
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

    /// <summary>UTF-16 hex dump of <paramref name="s"/>, which exposes doubled characters and invisible whitespace.</summary>
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
