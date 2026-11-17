using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Spelling;

/// <summary>
/// Owns the language-specific <see cref="SymSpell"/> indexes and answers synchronous lookups for
/// the typo gate. Building an index is expensive, so a dictionary loads on a background task the
/// first time its language is asked for. Until it is ready, lookups report nothing.
///
/// Thread safe. The lock guards cache publication, loading state and LRU bookkeeping. An index is
/// immutable once cached, so readers query it after releasing the lock.
/// </summary>
public sealed class SymSpellCorrector : ISpellingLexicon, IDisposable
{
    /// <summary>
    /// How long a dictionary that failed to load is left alone. Without it every lookup, which
    /// means every keystroke, would queue another doomed load.
    /// </summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(10);

    private sealed class CacheEntry(SymSpell symSpell, WordPrefixIndex prefixes)
    {
        public SymSpell SymSpell { get; } = symSpell;
        public WordPrefixIndex Prefixes { get; } = prefixes;
        public ulong LastAccessSequence { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly Func<SpellingDictionaryLanguage, string?> _resourceLoader;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _cacheLimit;

    // Guarded by _lock.
    private readonly Dictionary<SpellingDictionaryLanguage, CacheEntry> _cache = [];
    private readonly HashSet<SpellingDictionaryLanguage> _loadingLanguages = [];
    private readonly Dictionary<SpellingDictionaryLanguage, long> _failedUntil = [];
    private ulong _accessSequence;

    /// <param name="resourceLoader">Returns a dictionary's text, or null when it is unavailable. Defaults to the bundled files.</param>
    /// <param name="cacheLimit">
    /// How many language indexes stay in memory. Defaults to every bundled dictionary: settings
    /// can enable all of them, and the language resolver switches between enabled languages word by
    /// word, so a smaller cache would rebuild an evicted index, which takes seconds, over and over.
    /// Indexes load on first use, so memory still only grows with the languages actually typed.
    /// </param>
    public SymSpellCorrector(
        Func<SpellingDictionaryLanguage, string?>? resourceLoader = null,
        ILogger<SymSpellCorrector>? logger = null,
        int? cacheLimit = null)
    {
        _resourceLoader = resourceLoader ?? SpellingDictionaryResources.Loader();
        _logger = logger ?? NullLogger<SymSpellCorrector>.Instance;
        _cacheLimit = Math.Max(1, cacheLimit ?? SpellingDictionaryCatalog.AllLanguages.Count);
    }

    /// <summary>
    /// The best single-word correction for <paramref name="word"/>, recased to match it. Null when
    /// the index is not ready, the word is in the dictionary, or nothing is within edit distance.
    /// The lookup ignores case because the dictionaries are lowercase.
    /// </summary>
    public string? BestCorrection(string word, SpellingDictionaryLanguage language = SpellingDictionaryLanguage.English)
    {
        var lowered = word.ToLowerInvariant();
        if (CachedEntryOrRequestLoad(language)?.SymSpell.BestSuggestion(lowered) is not { Distance: > 0 } suggestion
            || suggestion.Term.ToLowerInvariant() == lowered)
        {
            return null;
        }
        return TypoCaseTransfer.Apply(word, suggestion.Term);
    }

    /// <inheritdoc />
    public bool IsKnownWord(string word, SpellingDictionaryLanguage language) =>
        CachedEntryOrRequestLoad(language)?.SymSpell.Contains(word.ToLowerInvariant()) ?? true;

    /// <summary>
    /// Dictionary words that start with <paramref name="prefix"/>, for the local word-ending
    /// fallback. Empty until the language's dictionary has loaded.
    /// </summary>
    public IReadOnlyList<WordPrefixIndex.Candidate> CompletionCandidates(
        string prefix, SpellingDictionaryLanguage language) =>
        CachedEntryOrRequestLoad(language)?.Prefixes.Candidates(prefix) ?? [];

    /// <summary>Cancels dictionary loads that are still running.</summary>
    public void Dispose()
    {
        _cancellation.Cancel();
    }

    private CacheEntry? CachedEntryOrRequestLoad(SpellingDictionaryLanguage language)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(language, out var entry))
            {
                entry.LastAccessSequence = ++_accessSequence;
                return entry;
            }
            if (_failedUntil.TryGetValue(language, out long retryAt))
            {
                if (Environment.TickCount64 < retryAt) return null;
                _failedUntil.Remove(language);
            }
            if (!_loadingLanguages.Add(language)) return null;
        }

        Task.Run(() => Load(language));
        return null;
    }

    private void Load(SpellingDictionaryLanguage language)
    {
        bool failed = false;
        try
        {
            if (_resourceLoader(language) is not { } contents)
            {
                _logger.LogError("SymSpell dictionary {Resource}.txt was not found.", language.ResourceName());
                failed = true;
                return;
            }

            var symSpell = new SymSpell();
            symSpell.LoadDictionary(contents, _cancellation.Token);
            var prefixes = new WordPrefixIndex(contents);
            lock (_lock) Publish(language, new CacheEntry(symSpell, prefixes));
            _logger.LogInformation(
                "SymSpell loaded {Count} {Language} words for correction.", symSpell.WordCount, language.DisplayName());
        }
        catch (OperationCanceledException)
        {
            // A cancelled load is not a failure. The claim is released so a later lookup may retry.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SymSpell dictionary {Resource}.txt failed to load.", language.ResourceName());
            failed = true;
        }
        finally
        {
            lock (_lock)
            {
                // Recorded under the same lock that releases the claim, so no lookup can slip in
                // between and queue another load.
                if (failed) _failedUntil[language] = Environment.TickCount64 + (long)FailureBackoff.TotalMilliseconds;
                _loadingLanguages.Remove(language);
            }
        }
    }

    /// <summary>
    /// Must be called with the lock held. The new index is the most recent, so eviction removes
    /// the least recently used older language and memory stays bounded for multilingual users.
    /// </summary>
    private void Publish(SpellingDictionaryLanguage language, CacheEntry entry)
    {
        entry.LastAccessSequence = ++_accessSequence;
        _cache[language] = entry;
        while (_cache.Count > _cacheLimit)
        {
            _cache.Remove(_cache.MinBy(cached => cached.Value.LastAccessSequence).Key);
        }
    }
}
