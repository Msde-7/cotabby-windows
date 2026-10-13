namespace Cotabby.Core.Support;

/// <summary>
/// No-repeat n-gram guard used inside the decode loop. With n = 3 the prefix is the last two
/// generated tokens, and every token that followed that same pair earlier in the generation is
/// blocked, so a 3-gram can never repeat verbatim.
/// </summary>
public static class RepetitionGuard
{
    public static HashSet<int> BlockedTokens(IReadOnlyList<int> history, int ngramSize = 3)
    {
        var blocked = new HashSet<int>();
        if (ngramSize < 2) return blocked;
        int prefixLen = ngramSize - 1;
        if (history.Count < prefixLen) return blocked;

        for (int start = 0; start + prefixLen < history.Count; start++)
        {
            bool match = true;
            for (int i = 0; i < prefixLen; i++)
            {
                if (history[start + i] != history[history.Count - prefixLen + i])
                {
                    match = false;
                    break;
                }
            }
            if (match) blocked.Add(history[start + prefixLen]);
        }
        return blocked;
    }
}
