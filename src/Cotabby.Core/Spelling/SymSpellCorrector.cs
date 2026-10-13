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
    private sealed class CacheEntry(SymSpell symSpell, WordPrefixIndex prefixes)
    {
        public SymSpell SymSpell { get; } = symSpell;
        public WordPrefixIndex Prefixes { get; } = prefixes;
        public ulong LastAccessSequence { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly int _cacheLimit;
    private readonly Func<SpellingDictionaryLanguage, string?> _resourceLoader;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();

    // Guarded by _lock.
    private readonly Dictionary<SpellingDictionaryLanguage, CacheEntry> _cache = [];
    private readonly HashSet<SpellingDictionaryLanguage> _loadingLanguages = [];
    private ulong _accessSequence;

    /// <param name="cacheLimit">How many language indexes stay in memory. Values below one are raised to one.</param>
    /// <param name="preloadLanguage">A language to start loading immediately, or null to stay cold until the first lookup.</param>
    /// <param name="resourceLoader">Returns a dictionary's text, or null when it is unavailable. Defaults to the bundled files.</param>
    public SymSpellCorrector(
        int cacheLimit = 2,
        SpellingDictionaryLanguage? preloadLanguage = null,
        Func<SpellingDictionaryLanguage, string?>? resourceLoader = null,
        ILogger<SymSpellCorrector>? logger = null)
    {
        _cacheLimit = Math.Max(1, cacheLimit);
        _resourceLoader = resourceLoader ?? SpellingDictionaryResources.Loader();
        _logger = logger ?? NullLogger<SymSpellCorrector>.Instance;

        if (preloadLanguage is { } language) CachedEntryOrRequestLoad(language);
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

    /// <summary>Exact-prefix completions for <paramref name="prefix"/>. Empty while the language is cold.</summary>
    public IReadOnlyList<WordPrefixIndex.Candidate> CompletionCandidates(string prefix, SpellingDictionaryLanguage language) =>
        CachedEntryOrRequestLoad(language)?.Prefixes.Candidates(prefix) ?? [];

    /// <inheritdoc />
    public bool IsKnownWord(string word, SpellingDictionaryLanguage language) =>
        CachedEntryOrRequestLoad(language)?.SymSpell.Contains(word.ToLowerInvariant()) ?? true;

    /// <summary>Builds and publishes a dictionary synchronously, without touching the bundled files.</summary>
    public void LoadDictionary(string contents, SpellingDictionaryLanguage language = SpellingDictionaryLanguage.English)
    {
        var entry = Build(contents);
        lock (_lock) Publish(language, entry);
    }

    /// <summary>The languages whose indexes are in memory, in code order.</summary>
    public IReadOnlyList<SpellingDictionaryLanguage> CachedLanguages
    {
        get
        {
            lock (_lock) return _cache.Keys.OrderBy(language => language.Code(), StringComparer.Ordinal).ToArray();
        }
    }

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
            if (!_loadingLanguages.Add(language)) return null;
        }

        Task.Run(() => Load(language));
        return null;
    }

    private void Load(SpellingDictionaryLanguage language)
    {
        try
        {
            if (_resourceLoader(language) is not { } contents)
            {
                _logger.LogError("SymSpell dictionary {Resource}.txt was not found.", language.ResourceName());
                return;
            }

            var entry = Build(contents);
            lock (_lock) Publish(language, entry);
            _logger.LogInformation(
                "SymSpell loaded {Count} {Language} words for correction.", entry.SymSpell.WordCount, language.DisplayName());
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock) _loadingLanguages.Remove(language);
        }
    }

    private CacheEntry Build(string contents)
    {
        var symSpell = new SymSpell();
        symSpell.LoadDictionary(contents, _cancellation.Token);
        return new CacheEntry(symSpell, new WordPrefixIndex(contents));
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
