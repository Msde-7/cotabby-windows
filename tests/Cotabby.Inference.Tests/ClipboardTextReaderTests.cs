using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using Cotabby.Win32.Clipboard;
using WpfClipboard = System.Windows.Clipboard;

namespace Cotabby.Inference.Tests;

/// <summary>
/// Runs against the real clipboard. The text that was there is put back at the end, and the
/// test does nothing when the clipboard holds something other than text.
/// </summary>
public class ClipboardTextReaderTests
{
    private readonly ClipboardTextReader _reader = new();

    [Fact]
    public void ReadsCopiedTextAndIgnoresItsOwnWrites()
    {
        // The test rewrites the real clipboard, so it only runs when asked for.
        if (Environment.GetEnvironmentVariable("COTABBY_CLIPBOARD_TEST") != "1") return;

        OnStaThread(() =>
        {
            string? original = WpfClipboard.ContainsText() ? WpfClipboard.GetText() : null;
            if (original is null && WpfClipboard.GetDataObject()?.GetFormats().Length > 0) return;
            try
            {
                RoundTrip();
            }
            finally
            {
                if (original is null) WpfClipboard.Clear();
                else WpfClipboard.SetDataObject(original, copy: true);
            }
        });
    }

    private void RoundTrip()
    {
        uint before = _reader.SequenceNumber;
        string copied = "cotabby copy " + Guid.NewGuid();
        Copy(copied);
        AssertReads(copied);
        uint afterCopy = _reader.SequenceNumber;
        Assert.NotEqual(before, afterCopy);

        // A paste that puts the old contents back leaves no trace.
        _reader.BeginOwnWrite();
        Copy("cotabby suggestion");
        Assert.Null(_reader.ReadText());
        Assert.Equal(afterCopy, _reader.SequenceNumber);
        Copy(copied);
        _reader.EndOwnWrite(restored: true);
        Assert.Equal(afterCopy, _reader.SequenceNumber);
        AssertReads(copied);

        // A paste that could not put them back hides its own text until the next copy.
        _reader.BeginOwnWrite();
        Copy("cotabby suggestion");
        _reader.EndOwnWrite(restored: false);
        Assert.Equal(afterCopy, _reader.SequenceNumber);
        Assert.Null(_reader.ReadText());

        Copy(new string('a', ClipboardTextReader.MaxChars + 100));
        AssertReads(new string('a', ClipboardTextReader.MaxChars));
        Assert.NotEqual(afterCopy, _reader.SequenceNumber);
    }

    /// <summary>Clipboard history is told to skip the text, so the test leaves nothing behind there.</summary>
    private static void Copy(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        WpfClipboard.SetDataObject(data, copy: true);
    }

    /// <summary>Another process can hold the clipboard open right after a copy, so a read may need a retry.</summary>
    private void AssertReads(string expected)
    {
        var clock = Stopwatch.StartNew();
        string? text;
        while ((text = _reader.ReadText()) != expected && clock.ElapsedMilliseconds < 2000) Thread.Sleep(10);
        Assert.Equal(expected, text);
    }

    private static void OnStaThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
