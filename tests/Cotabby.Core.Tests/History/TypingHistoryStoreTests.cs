using System.Diagnostics;
using System.Security.Cryptography;
using Cotabby.Core.Focus;
using Cotabby.Core.History;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Tests.Suggestions;

namespace Cotabby.Core.Tests.History;

/// <summary>Keeps vault keys in memory so tests never touch the user's profile.</summary>
internal sealed class InMemoryKeyStore : ITypingHistoryKeyStore
{
    private readonly Lock _lock = new();
    private byte[]? _key;
    private bool _deletionFails;

    public bool HasKey { get { lock (_lock) return _key is not null; } }

    /// <summary>Makes <see cref="DeleteKey"/> fail, the way a locked or denied key store would.</summary>
    public void SetDeletionFails(bool fails) { lock (_lock) _deletionFails = fails; }

    public byte[]? ExistingKey() { lock (_lock) return _key; }

    public byte[] CreateKey() { lock (_lock) return _key = RandomNumberGenerator.GetBytes(32); }

    public void DeleteKey()
    {
        lock (_lock)
        {
            if (_deletionFails) throw new IOException("The key store refused the deletion.");
            _key = null;
        }
    }
}

/// <summary>
/// Holds the first key lookup, which happens inside the first archive write, until released, so a
/// test can quit while a background save is still writing. Counts lookups, one per write.
/// </summary>
internal sealed class GatedKeyStore : ITypingHistoryKeyStore
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _gate = new(0);
    private byte[]? _key;
    private int _lookups;
    private bool _holding;

    public int KeyLookups { get { lock (_lock) return _lookups; } }
    public bool IsHoldingAWrite { get { lock (_lock) return _holding; } }
    public void Release() => _gate.Release();

    public byte[]? ExistingKey()
    {
        bool isFirst;
        lock (_lock)
        {
            _lookups++;
            isFirst = _holding = _lookups == 1;
        }
        if (isFirst)
        {
            _gate.Wait();
            lock (_lock) _holding = false;
        }
        lock (_lock) return _key;
    }

    public byte[] CreateKey() { lock (_lock) return _key = RandomNumberGenerator.GetBytes(32); }

    public void DeleteKey() { lock (_lock) _key = null; }
}

public sealed class TypingHistoryStoreTests : IDisposable
{
    private const string App = "com.example.TestApp";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"typing-history-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private TypingHistoryVault MakeVault(ITypingHistoryKeyStore? keyStore = null) =>
        new(Path.Combine(_directory, "TypingHistory.sealed"), keyStore ?? new InMemoryKeyStore());

    private TypingHistoryStore MakeStore(TypingHistoryVault? vault = null, TimeSpan? saveDelay = null) =>
        new(vault ?? MakeVault(), loadsArchive: false, saveDelay: saveDelay);

    private string WriteExport(params Dictionary<string, string>[] rows)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "user_inputs.json");
        File.WriteAllBytes(path, CotypistExportImporterTests.Export(rows));
        return path;
    }

    private static Dictionary<string, string>[] SignOffRows(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new Dictionary<string, string>
        {
            ["appBundleIdentifier"] = "com.microsoft.Outlook",
            ["textUpToCursor"] = $"Topic {index}: the Imperum POC with Sentinel is going well.\nThanks!\nBest regards, Senad",
        })];

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The condition never held.");
            await Task.Delay(10);
        }
    }

    private static TypingHistoryRecord Record(string text) => new()
    {
        Id = Guid.NewGuid(),
        BundleIdentifier = "mail",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Text = text,
        Source = TypingHistorySource.Recorded,
    };

    private static FocusedField Focus(
        string text, string element = "field", string app = "mail", bool isSecure = false,
        string className = "", string windowTitle = "") =>
        Fields.Text(text, process: app) with
        {
            FieldId = element,
            IsSecure = isSecure,
            ClassName = className,
            WindowTitle = windowTitle,
        };

    private static FocusedField Context(string precedingText, string app = App, string element = "field") =>
        Fields.Text(precedingText, process: app) with { FieldId = element };

    private static SuggestionRequest Request(string precedingText) =>
        SuggestionRequestFactory.Build(Fields.Text(precedingText), "request", SuggestionSettings.Default);

    // Vault

    [Fact]
    public void VaultRoundTripsAndTheFileHoldsNoPlaintext()
    {
        var vault = MakeVault();
        var record = Record("A very private sentence about the Imperum POC.");

        vault.Save([record]);

        Assert.Equal([record], vault.Load());
        string bytes = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(vault.FilePath));
        Assert.DoesNotContain("private sentence", bytes);
    }

    [Fact]
    public void VaultWithoutItsKeyReportsCorruptionInsteadOfEmpty()
    {
        var keyStore = new InMemoryKeyStore();
        var vault = MakeVault(keyStore);
        vault.Save([]);
        keyStore.DeleteKey();

        Assert.Throws<TypingHistoryVaultException>(vault.Load);
    }

    [Fact]
    public void VaultWithAnotherKeyReportsCorruption()
    {
        var vault = MakeVault();
        vault.Save([Record("Sealed with the first key, opened with another.")]);

        Assert.Throws<TypingHistoryVaultException>(() => new TypingHistoryVault(vault.FilePath, new GarbageKeyStore()).Load());
    }

    private sealed class GarbageKeyStore : ITypingHistoryKeyStore
    {
        public byte[]? ExistingKey() => new byte[32];
        public byte[] CreateKey() => new byte[32];
        public void DeleteKey() { }
    }

    // Preferences

    [Fact]
    public void PreferencesDefaultOffAndFollowTheAppSettings()
    {
        var store = MakeStore();
        Assert.Equal(TypingHistoryPreferences.Defaults, store.Preferences);

        store.ApplyPreferences(new TypingHistoryPreferences(true, true, ["WhatsApp"]));

        Assert.True(store.Preferences.IsUsingHistory);
        Assert.True(store.Preferences.IsRecording);
        Assert.Equal(["WhatsApp"], store.Preferences.ExcludedApps);

        store.ApplyPreferences(TypingHistoryPreferences.Defaults);
        Assert.Equal(TypingHistoryPreferences.Defaults, store.Preferences);
    }

    // Import and use

    [Fact]
    public async Task ImportMakesHistoryUsableAndReimportAddsNothing()
    {
        var store = MakeStore();
        store.SetUsingHistory(true);
        string path = WriteExport(SignOffRows(6));

        await store.ImportCotypistExportAsync(path);
        Assert.Equal(6, store.RecordCount);
        await store.ImportCotypistExportAsync(path);
        Assert.Equal(6, store.RecordCount);
        Assert.Equal("Imported 0 entries (6 were already in your history).", store.LastImportMessage);

        var request = Request("Thanks!\nBest regards, ");
        await WaitUntil(() => store.PhraseContinuation(request, usesEndpoint: false) is not null);
        Assert.Equal("Senad", store.PhraseContinuation(request, usesEndpoint: false));

        var context = Context(
            "Topic update: the Imperum POC with Sentinel is going well, the connectors look fine and stable today ",
            app: "com.microsoft.Outlook");
        Assert.NotEmpty(store.HistoryExamples(context, usesEndpoint: false));
    }

    [Fact]
    public async Task HistoryIsNeverOfferedToTheEndpointOrWhenTurnedOff()
    {
        var store = MakeStore();
        store.SetUsingHistory(true);
        await store.ImportCotypistExportAsync(WriteExport(SignOffRows(6)));
        var request = Request("Thanks!\nBest regards, ");
        await WaitUntil(() => store.PhraseContinuation(request, usesEndpoint: false) is not null);
        var context = Context(
            "Topic update: the Imperum POC with Sentinel is going well, the connectors look fine and stable today ",
            app: "com.microsoft.Outlook");

        Assert.Null(store.PhraseContinuation(request, usesEndpoint: true));
        Assert.Empty(store.HistoryExamples(context, usesEndpoint: true));

        store.SetUsingHistory(false);
        Assert.Null(store.PhraseContinuation(request, usesEndpoint: false));
        Assert.Empty(store.HistoryExamples(context, usesEndpoint: false));
    }

    [Fact]
    public async Task ImportPersistsEncryptedAndReloads()
    {
        var vault = MakeVault();
        var store = MakeStore(vault);
        await store.ImportCotypistExportAsync(WriteExport(SignOffRows(3)));
        store.Flush();

        var reloaded = MakeStore(vault);
        await reloaded.LoadArchiveAsync();
        Assert.Equal(3, reloaded.RecordCount);
    }

    [Fact]
    public async Task AnArchiveThatCannotBeOpenedPausesRecording()
    {
        var keyStore = new InMemoryKeyStore();
        var vault = MakeVault(keyStore);
        vault.Save([Record("Writing whose key has gone missing.")]);
        keyStore.DeleteKey();

        var store = new TypingHistoryStore(vault, new TypingHistoryPreferences(false, true, []));
        await WaitUntil(() => store.Status != TypingHistoryStatus.Loading);
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review."), () => true);
        store.Flush();

        Assert.Equal(TypingHistoryStatus.Unavailable, store.Status);
        Assert.NotNull(store.UnavailableMessage);
        Assert.True(File.Exists(vault.FilePath));
    }

    [Fact]
    public async Task UnrecognizedFileReportsAnErrorAndAddsNothing()
    {
        var store = MakeStore();
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "other.json");
        File.WriteAllText(path, "{\"x\":1}");

        await store.ImportCotypistExportAsync(path);

        Assert.Equal(0, store.RecordCount);
        Assert.Equal(new CotypistImportException().Message, store.LastImportMessage);
    }

    // Recording

    [Fact]
    public void RecordingCapturesAFieldOnceFocusMovesOn()
    {
        var store = MakeStore();
        store.SetRecording(true);

        store.Observe(Focus("Hi Arnaud, the Imperum POC"), () => true);
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review."), () => true);
        // The field being typed in is not committed yet.
        Assert.Equal(0, store.RecordCount);

        store.Observe(Focus("", element: "other"), () => true);
        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void RecordingSkipsSecureFieldsExcludedAppsAndDisallowedContexts()
    {
        var store = MakeStore();
        store.SetRecording(true);
        store.SetExcluded("WhatsApp", excluded: true);

        store.Observe(Focus("correct horse battery staple password", isSecure: true), () => true);
        store.Observe(Focus("", element: "x"), () => true);
        store.Observe(Focus("a long private chat message in WhatsApp", app: "WhatsApp"), () => true);
        store.Observe(Focus("", element: "y"), () => true);
        store.Observe(Focus("text typed while Cotabby is paused for now"), () => false);
        store.Observe(Focus("", element: "z"), () => true);

        Assert.Equal(0, store.RecordCount);
    }

    [Fact]
    public void RecordingOffRecordsNothing()
    {
        var store = MakeStore();

        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review."), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(0, store.RecordCount);
    }

    [Fact]
    public void AFieldThatBlinksUnsupportedResumesTheSameRecord()
    {
        var store = MakeStore();
        store.SetRecording(true);

        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready"), () => true);
        store.Observe(null, () => true);
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review."), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public async Task DeleteAllRemovesRecordsAndTheFile()
    {
        var vault = MakeVault();
        var store = MakeStore(vault);
        await store.ImportCotypistExportAsync(WriteExport(SignOffRows(3)));
        store.Flush();
        Assert.True(File.Exists(vault.FilePath));

        store.DeleteAll();

        Assert.Equal(0, store.RecordCount);
        Assert.False(File.Exists(vault.FilePath));
    }

    // Review fixes

    [Fact]
    public void ASaveCapturedBeforeDeleteAllIsNeverWritten()
    {
        var vault = MakeVault();
        var writer = new TypingHistoryWriter(vault);

        writer.Destroy(generation: 1);
        writer.Save([Record("Deleted history must stay deleted.")], generation: 0, sequence: 1);

        Assert.False(File.Exists(vault.FilePath));
    }

    [Fact]
    public void AnOlderSnapshotCannotOverwriteANewerOne()
    {
        var vault = MakeVault();
        var writer = new TypingHistoryWriter(vault);

        writer.Save([Record("newer snapshot text")], generation: 0, sequence: 2);
        writer.Save([Record("older snapshot text")], generation: 0, sequence: 1);

        Assert.Equal(["newer snapshot text"], vault.Load().Select(record => record.Text));
    }

    [Fact]
    public void ReturningToAFieldContinuesItsRecord()
    {
        var store = MakeStore();
        store.SetRecording(true);

        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready", element: "body"), () => true);
        store.Observe(Focus("", element: "search"), () => true);
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review.", element: "body"), () => true);
        store.Observe(Focus("", element: "search"), () => true);

        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void AReusedElementWithDifferentTextStartsANewRecord()
    {
        var store = MakeStore();
        store.SetRecording(true);

        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review.", element: "body"), () => true);
        store.Observe(Focus("", element: "other"), () => true);
        store.Observe(Focus("A completely different message about lunch plans today.", element: "body"), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(2, store.RecordCount);
    }

    [Fact]
    public void TerminalsAreNeverRecorded()
    {
        var store = MakeStore();
        store.SetRecording(true);

        store.Observe(Focus("export OPENAI_API_KEY and run the deploy script", element: "t", app: "WindowsTerminal"),
            () => true);
        store.Observe(Focus("git push origin main and then open the pull request", element: "vs", app: "Code",
            className: "xterm-helper-textarea"), () => true);
        store.Observe(Focus("", element: "x"), () => true);

        Assert.Equal(0, store.RecordCount);
    }

    // Storage lifecycle

    [Fact]
    public void QuittingWithoutHistoryCreatesNoArchiveOrKey()
    {
        var keyStore = new InMemoryKeyStore();
        var vault = MakeVault(keyStore);
        var store = MakeStore(vault);
        store.SetRecording(true);
        store.Observe(Focus("short"), () => true);

        store.Flush();

        Assert.False(File.Exists(vault.FilePath));
        Assert.False(keyStore.HasKey);
    }

    [Fact]
    public async Task QuittingAfterDeleteAllDoesNotRecreateTheArchive()
    {
        var keyStore = new InMemoryKeyStore();
        var vault = MakeVault(keyStore);
        var store = MakeStore(vault);
        await store.ImportCotypistExportAsync(WriteExport(SignOffRows(3)));
        store.Flush();

        store.DeleteAll();
        store.Flush();

        Assert.False(File.Exists(vault.FilePath));
        Assert.False(keyStore.HasKey);
    }

    [Fact]
    public async Task QuittingWhileABackgroundSaveIsStillWritingWritesAgain()
    {
        var keyStore = new GatedKeyStore();
        var store = MakeStore(MakeVault(keyStore), saveDelay: TimeSpan.Zero);
        store.SetRecording(true);
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready", element: "body"), () => true);
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review.", element: "body"), () => true);
        await WaitUntil(() => keyStore.IsHoldingAWrite);
        // The background write finishes only after the quit has started waiting for it.
        _ = Task.Delay(200).ContinueWith(_ => keyStore.Release(), TaskScheduler.Default);

        store.Flush();

        // The quit must not trust a write that hasn't finished.
        Assert.Equal(2, keyStore.KeyLookups);
    }

    [Fact]
    public async Task AFailedDeleteAllKeepsShowingTheHistoryAndCanBeRetried()
    {
        var keyStore = new InMemoryKeyStore();
        var store = MakeStore(MakeVault(keyStore));
        await store.ImportCotypistExportAsync(WriteExport(SignOffRows(3)));
        store.Flush();
        keyStore.SetDeletionFails(true);

        store.DeleteAll();

        // Nothing was deleted, so nothing should look deleted.
        Assert.Equal(3, store.RecordCount);
        Assert.NotNull(store.DeletionError);

        keyStore.SetDeletionFails(false);
        store.DeleteAll();

        Assert.Equal(0, store.RecordCount);
        Assert.Null(store.DeletionError);
        Assert.False(keyStore.HasKey);
    }

    [Fact]
    public void ExcludingAnAppMidFieldAlsoDropsTheAlreadySavedPart()
    {
        var vault = MakeVault();
        var store = MakeStore(vault);
        store.SetRecording(true);
        store.Observe(Focus("a long private chat message in WhatsApp", app: "WhatsApp"), () => true);
        store.Flush();
        // Saving copies the field being typed in.
        Assert.Equal(1, store.RecordCount);

        store.SetExcluded("WhatsApp", excluded: true);
        store.Flush();

        Assert.Equal(0, store.RecordCount);
        Assert.Empty(vault.Load());
    }

    // Field identity

    private static List<string> StoredTexts(TypingHistoryStore store, TypingHistoryVault vault)
    {
        store.Flush();
        return [.. vault.Load().Select(record => record.Text).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void EachSentChatMessageIsKeptWhenTheComposerClears()
    {
        var vault = MakeVault();
        var store = MakeStore(vault);
        store.SetRecording(true);

        store.Observe(Focus("", element: "composer"), () => true);
        store.Observe(Focus("Hey, are we still on for lunch tomorrow?", element: "composer"), () => true);
        store.Observe(Focus("", element: "composer"), () => true);
        store.Observe(Focus("Great, I will book the usual place at noon.", element: "composer"), () => true);
        store.Observe(Focus("", element: "composer"), () => true);
        store.Observe(Focus("", element: "search"), () => true);

        Assert.Equal(
            ["Great, I will book the usual place at noon.", "Hey, are we still on for lunch tomorrow?"],
            StoredTexts(store, vault));
    }

    [Fact]
    public void AnEmptyFieldReusingAnElementNeverOverwritesEarlierWriting()
    {
        var vault = MakeVault();
        var store = MakeStore(vault);
        store.SetRecording(true);

        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review.", element: "body"), () => true);
        store.Observe(Focus("", element: "other"), () => true);
        // A new compose window that got the old body's element id, empty at first.
        store.Observe(Focus("", element: "body"), () => true);
        store.Observe(Focus("Lunch at noon tomorrow works for me, see you.", element: "body"), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(
            ["Hi Arnaud, the Imperum POC is ready for review.", "Lunch at noon tomorrow works for me, see you."],
            StoredTexts(store, vault));
    }

    [Fact]
    public void AFieldThatBrieflyReadsEmptyKeepsOneRecord()
    {
        var vault = MakeVault();
        var store = MakeStore(vault);
        store.SetRecording(true);

        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready", element: "body"), () => true);
        store.Observe(Focus("", element: "body"), () => true);
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready for review.", element: "body"), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(["Hi Arnaud, the Imperum POC is ready for review."], StoredTexts(store, vault));
    }

    [Fact]
    public void DeletingAFieldsTextKeyByKeyStillDiscardsIt()
    {
        var store = MakeStore();
        store.SetRecording(true);
        const string text = "Hi Arnaud, the Imperum POC is ready for review.";
        store.Observe(Focus(text, element: "body"), () => true);
        store.Flush();
        Assert.Equal(1, store.RecordCount);

        for (int length = text.Length - 1; length >= 0; length--)
        {
            store.Observe(Focus(text[..length], element: "body"), () => true);
        }
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(0, store.RecordCount);
    }

    private static string Words(string stem, int count) =>
        string.Join(' ', Enumerable.Range(0, count).Select(i => $"{stem}{i}"));

    private static string WindowOf(string text) =>
        text[Math.Max(0, text.Length - TypingHistoryStore.CaptureWindowChars)..];

    [Fact]
    public void ALongDocumentsSlidingCaptureWindowStaysOneRecord()
    {
        var store = MakeStore();
        store.SetRecording(true);
        // Focus reading keeps a fixed window before the caret, so in a long document every
        // keystroke drops a character from the front of the window as it adds one at the caret.
        string window = WindowOf(Words("word", 3_000));
        store.Observe(Focus(window, element: "doc"), () => true);
        foreach (char character in " and a few more words typed at the end")
        {
            window = WindowOf(window + character);
            store.Observe(Focus(window, element: "doc"), () => true);
        }
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void AnotherLongDocumentShownInTheSameViewKeepsBothRecords()
    {
        var store = MakeStore();
        store.SetRecording(true);
        string first = WindowOf(Words("alpha", 3_000));
        string second = WindowOf(Words("beta", 3_000));

        store.Observe(Focus(first, element: "doc", windowTitle: "First.txt"), () => true);
        store.Observe(Focus(second, element: "doc", windowTitle: "Second.txt"), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(2, store.RecordCount);
    }

    [Fact]
    public void ACaretJumpInALongDocumentKeepsOneRecord()
    {
        var store = MakeStore();
        store.SetRecording(true);
        string document = Words("word", 6_000);
        string nearEnd = WindowOf(document);
        string nearMiddle = WindowOf(document[..(document.Length / 2)]);

        store.Observe(Focus(nearEnd, element: "doc", windowTitle: "Notes.txt"), () => true);
        store.Observe(Focus(nearMiddle, element: "doc", windowTitle: "Notes.txt"), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void AWindowCutShortOfASurrogatePairStillCountsAsALongDocument()
    {
        var store = MakeStore();
        store.SetRecording(true);
        string document = Words("word", 6_000);
        string nearEnd = WindowOf(document);
        // Focus reading drops the low half of a pair cut at the window start, so the window is one shorter.
        string nearMiddle = WindowOf(document[..(document.Length / 2)])[1..];

        store.Observe(Focus(nearEnd, element: "doc", windowTitle: "Notes.txt"), () => true);
        store.Observe(Focus(nearMiddle, element: "doc", windowTitle: "Notes.txt"), () => true);
        store.Observe(Focus("", element: "other"), () => true);

        Assert.Equal(1, store.RecordCount);
    }

    [Fact]
    public void SharesContentFollowsASlidingWindowButNotAnotherDocument()
    {
        string current = WindowOf(Words("word", 3_000));

        Assert.True(TypingHistoryStore.SharesContent(current, WindowOf(current + " and more")));
        Assert.False(TypingHistoryStore.SharesContent(current, WindowOf(Words("other", 3_000))));
    }

    [Fact]
    public void KeepsMostOfTellsEditsFromReplacements()
    {
        const string draft = "Hi Arnaud, the Imperum POC is ready for review.";

        Assert.True(TypingHistoryStore.KeepsMostOf(draft, draft + " Thanks"), "Typing at the end");
        Assert.True(TypingHistoryStore.KeepsMostOf(draft, "Hello Arnaud, the Imperum POC is ready for review."),
            "Fixing the first word");
        Assert.True(TypingHistoryStore.KeepsMostOf(draft, draft[..^5]), "Deleting a few characters");
        Assert.False(TypingHistoryStore.KeepsMostOf(draft, ""), "A sent message cleared from the composer");
        Assert.False(TypingHistoryStore.KeepsMostOf(draft, "Lunch at noon tomorrow works for me."), "Different text");
    }

    // Example cache

    [Fact]
    public async Task CachedExamplesAreRecheckedAsTheFieldGrowsWithinABlock()
    {
        var store = MakeStore();
        store.SetUsingHistory(true);
        await store.ImportCotypistExportAsync(WriteExport(new Dictionary<string, string>
        {
            ["appBundleIdentifier"] = App,
            ["textUpToCursor"] =
                "we will review the Imperum connector plan with the SOC team on Monday morning, then ship it.",
        }));
        // 16 words: the query block for both lookups below.
        const string opening =
            "Draft notes for Friday about unrelated budget items we will review the Imperum connector plan with ";
        var earlier = Context(opening);
        // Five more words, still inside the same 8-word block, and now the field holds the past text.
        var later = Context(opening + "the SOC team on Monday");
        Assert.Equal(
            TypingHistoryQuery.StableText(earlier.PrecedingText), TypingHistoryQuery.StableText(later.PrecedingText));

        await WaitUntil(() => store.HistoryExamples(earlier, usesEndpoint: false).Count > 0);

        // The field now contains that writing. Showing it would make the model echo the draft.
        Assert.Empty(store.HistoryExamples(later, usesEndpoint: false));
    }

    [Fact]
    public async Task AnExampleHiddenByTheFieldsLatestWordsComesBackWhenTheWordingMovesOn()
    {
        var store = MakeStore();
        store.SetUsingHistory(true);
        await store.ImportCotypistExportAsync(WriteExport(new Dictionary<string, string>
        {
            ["appBundleIdentifier"] = App,
            ["textUpToCursor"] =
                "we will review the Imperum connector plan with the SOC team on Monday morning, then ship it.",
        }));
        var probe = Context("Imperum connector plan review notes for the steering group ", element: "probe");
        await WaitUntil(() => store.HistoryExamples(probe, usesEndpoint: false).Count > 0);
        // The field's latest words are inside the earlier passage, so it is withheld for now.
        const string echoing = "Budget notes for Friday and several other items: please review the Imperum connector plan with "
            + "the SOC team on Monday";
        Assert.Empty(store.HistoryExamples(Context(echoing), usesEndpoint: false));

        // Two more words, same 8-word block: the wording has moved on, so the passage is usable again.
        var movedOn = Context(echoing + " evening instead");
        Assert.Equal(TypingHistoryQuery.StableText(echoing), TypingHistoryQuery.StableText(movedOn.PrecedingText));
        Assert.NotEmpty(store.HistoryExamples(movedOn, usesEndpoint: false));
    }

    [Fact]
    public void UnchangedTextDoesNotEvaluateTheSettingsGate()
    {
        var store = MakeStore();
        store.SetRecording(true);
        int gateCalls = 0;

        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready", element: "body"), () => { gateCalls++; return true; });
        store.Observe(Focus("Hi Arnaud, the Imperum POC is ready", element: "body"), () => { gateCalls++; return true; });

        Assert.Equal(1, gateCalls);
    }
}
