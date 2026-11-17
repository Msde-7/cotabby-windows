namespace Cotabby.Core.Insertion;

/// <summary>The keys that finish an edit the host only partly applied.</summary>
/// <param name="Backspaces">Backspaces to send first.</param>
/// <param name="Text">Text to type after them.</param>
public readonly record struct EditRepairPlan(int Backspaces, string Text);

/// <summary>
/// Works out what a host dropped from an edit of synthetic keys. Some hosts, Windows 11 Notepad
/// among them, now and then lose a key from input that arrives faster than a person types. Only the
/// outcomes that can be told apart for certain are repaired: some backspaces missing, the text
/// missing, or the end of the text missing. Anything else is left as it is, since a wrong repair
/// would damage the user's text.
/// </summary>
public static class EditRepair
{
    /// <param name="before">The text before the caret just before the edit.</param>
    /// <param name="deleteCount">UTF-16 units the edit deleted before the caret.</param>
    /// <param name="text">The text the edit typed.</param>
    /// <param name="live">The text before the caret after the edit.</param>
    /// <returns>The keys still needed, or null when the edit landed or its outcome is unclear.</returns>
    public static EditRepairPlan? Plan(string before, int deleteCount, string text, string live)
    {
        before = Normalized(before);
        live = Normalized(live);
        text = Normalized(text);
        if (deleteCount < 0 || deleteCount > before.Length) return null;
        string kept = before[..^deleteCount];
        if (live == kept + text) return null;

        // Every backspace landed, and the text did not, or only its start did.
        if (live.StartsWith(kept, StringComparison.Ordinal) && live.Length < kept.Length + text.Length
            && text.StartsWith(live[kept.Length..], StringComparison.Ordinal))
        {
            return new EditRepairPlan(0, text[(live.Length - kept.Length)..]);
        }

        // Some backspaces were lost. Whatever of the text landed is taken back and typed again.
        for (int lost = 1; lost <= deleteCount; lost++)
        {
            string partlyDeleted = before[..^(deleteCount - lost)];
            if (live == partlyDeleted + text) return new EditRepairPlan(text.Length + lost, text);
            if (live == partlyDeleted) return new EditRepairPlan(lost, text);
        }
        return null;
    }

    /// <summary>Chromium shows a typed space at the end of a line as U+00A0 for a while.</summary>
    private static string Normalized(string text) => text.Replace(' ', ' ').Replace("\r", "");
}
