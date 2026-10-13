namespace Cotabby.Core.Context;

/// <summary>The system clipboard as the prompt context sees it.</summary>
public interface IClipboardReader
{
    /// <summary>Counter that moves whenever the user's clipboard contents change.</summary>
    uint SequenceNumber { get; }

    /// <summary>The text on the clipboard, or null when there is none or it cannot be read.</summary>
    string? ReadText();
}
