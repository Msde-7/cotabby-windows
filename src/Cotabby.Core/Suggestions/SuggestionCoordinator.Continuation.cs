using Cotabby.Core.Focus;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

public sealed partial class SuggestionCoordinator
{
    /// <summary>
    /// A generation for the text a suggestion leaves once used, started before it is. The text may
    /// still be generating; empty means it produced nothing.
    /// </summary>
    private sealed record PrefetchedContinuation(
        string FocusIdentity, string PrecedingText, Task<string> Text, CancellationTokenSource Cancel);

    /// <summary>
    /// Starts evaluating the prompt of a field that just gained focus. The engine gives it up as soon
    /// as a real request arrives, and a key or another focus change cancels it here.
    /// </summary>
    private void PrewarmField(FocusedField? field)
    {
        CancelPrewarm();
        var settings = _settings;
        if (Prewarm is not { } prewarm || field is null || settings.UsesEndpoint || !IsAvailableIn(field)
            || field.IsSecure || field.ImeLanguageId != 0 || !_engine.IsReady
            || !SuggestionRequestFactory.ShouldGenerate(field.PrecedingText, field.TrailingText, settings.SuggestWithinWords))
        {
            return;
        }
        // The first screen text read for this field asks for a suggestion with that text ahead of
        // the field's, so the prompt is evaluated again from there. That request warms it instead.
        if (settings.ScreenContext && _screen is { } screen && screen.For(field) is null) return;
        var cts = _prewarm = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await prewarm(BuildRequest(field, NewRequestId("warm_"), settings), cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Prewarming the focused field failed.");
            }
        });
    }

    private void CancelPrewarm()
    {
        _prewarm?.Cancel();
        _prewarm = null;
    }

    /// <summary>
    /// Generates what follows a suggestion before the user reaches its end, so the next one is
    /// ready the moment it is needed. Three cases qualify: a suggestion nearly typed through, a word
    /// ending on its own, and a spelling fix, where the next words are predicted as if the ending or
    /// fix were already in. It runs beside the current work, which it must not cancel, and only fills
    /// <see cref="_prefetched"/>, which nothing shows until the live text matches it.
    /// </summary>
    /// <param name="atOffer">True when called as the suggestion first shows.</param>
    private void PrefetchContinuation(SuggestionSession session, bool atOffer = false)
    {
        if (_sessionPrefetched || _sessionField is not { } offeredIn || session.IsExhausted || !_engine.IsReady) return;
        var settings = _settings;
        if (ContinuationTarget(session, settings, atOffer) is not { } preceding) return;
        if (!SuggestionRequestFactory.ShouldGenerate(preceding, offeredIn.TrailingText, settings.SuggestWithinWords)) return;
        _sessionPrefetched = true;
        StartPrefetch(offeredIn, preceding, settings, session.RequestId);
    }

    /// <summary>
    /// Starts generating what follows the last chunk as soon as it is accepted, against the text
    /// the host is expected to show, as upstream does. The read after the insertion only confirms
    /// that text before the answer is used. A continuation already running for it is kept.
    /// </summary>
    private void PrefetchAfterAccept(FocusedField offeredIn, string preceding, string requestId)
    {
        var settings = _settings;
        if (!_engine.IsReady) return;
        if (Volatile.Read(ref _prefetched) is { } running && running.FocusIdentity == FocusIdentity(offeredIn))
        {
            string target = SuggestionSessionReconciler.ComparisonNormalized(running.PrecedingText);
            string wanted = SuggestionSessionReconciler.ComparisonNormalized(preceding);
            // The space added after the last word is allowed for when the continuation is taken.
            if (wanted == target || wanted == target + " ") return;
        }
        if (!SuggestionRequestFactory.ShouldGenerate(preceding, offeredIn.TrailingText, settings.SuggestWithinWords)) return;
        StartPrefetch(offeredIn, preceding, settings, requestId);
    }

    /// <summary>Generates for <paramref name="preceding"/> before the caret of <paramref name="offeredIn"/>, replacing any earlier prefetch.</summary>
    private void StartPrefetch(FocusedField offeredIn, string preceding, SuggestionSettings settings, string forRequest)
    {
        var field = offeredIn with { Text = preceding + offeredIn.TrailingText, CaretOffset = preceding.Length };
        string identity = FocusIdentity(field);
        CancelPrefetch();
        var cts = _prefetch = new CancellationTokenSource();
        var text = Task.Run(async () =>
        {
            try
            {
                var request = BuildRequest(field, NewRequestId("pre_"), settings);
                string generated = await GenerateAsync(request, cts.Token).ConfigureAwait(false);
                if (generated.Length > 0) _logger.LogDebug("Prefetched the continuation of {ReqId}.", forRequest);
                return cts.IsCancellationRequested ? "" : generated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefetching the continuation of {ReqId} failed.", forRequest);
                return "";
            }
        });
        Volatile.Write(ref _prefetched, new PrefetchedContinuation(identity, preceding, text, cts));
    }

    /// <summary>
    /// The text before the caret once the suggestion is used, built from the session rather than the
    /// field, which may not have published the last keys yet. Null when nothing should be prefetched.
    /// </summary>
    private string? ContinuationTarget(SuggestionSession session, SuggestionSettings settings, bool atOffer)
    {
        // Like upstream, the words after a fix or a lone word ending are new work the user has not
        // seen, which an endpoint, billed per request, is not asked for. What follows a suggestion
        // nearly typed through is.
        if (session.Correction is { } correction)
        {
            if (settings.UsesEndpoint) return null;
            return TypoCorrectionReplacementPlanner.Plan(
                    _expectedPreceding, correction.Word, correction.CorrectedWord, requiresTrailingSpace: false) is { } fix
                ? _expectedPreceding[..^fix.DeletingUtf16Count] + fix.ReplacementText
                : null;
        }
        if (session.PredictedText.Length <= ContinuationPrefetchChars) return _expectedPreceding + session.PredictedText;
        // A lone word ending: the words after it are a guess the model has not made yet.
        bool wordEnding = atOffer && !settings.UsesEndpoint
            && !session.PredictedText.Any(char.IsWhiteSpace)
            && CaretWordContext.UnfinishedWord(_expectedPreceding) is not null;
        return wordEnding ? _expectedPreceding + session.PredictedText : null;
    }

    /// <summary>
    /// The prefetched continuation when it was generated for this very text, waiting for it if it is
    /// still running. Used once. Null when there is none for this text or it produced nothing.
    /// </summary>
    private async Task<string?> TakePrefetchedAsync(FocusedField field, CancellationToken ct)
    {
        var prefetched = Volatile.Read(ref _prefetched);
        if (prefetched is null) return null;
        string expected = SuggestionSessionReconciler.ComparisonNormalized(prefetched.PrecedingText);
        string live = SuggestionSessionReconciler.ComparisonNormalized(field.PrecedingText);
        // The space added after accepting, or typed by the user, ends the same word. It is the one
        // difference allowed, and it takes the place of the continuation's own leading space.
        bool joinedBySpace = live == expected + " " && !expected.EndsWith(' ');
        if (prefetched.FocusIdentity != FocusIdentity(field) || (live != expected && !joinedBySpace))
        {
            // The field moved past the text it was made for. The engine runs one generation at a
            // time, so a leftover one would hold up the request that replaces it.
            if (Interlocked.CompareExchange(ref _prefetched, null, prefetched) == prefetched) prefetched.Cancel.Cancel();
            return null;
        }
        // It is taken only once used, so a request cancelled while it waits leaves it for the next.
        string text = await prefetched.Text.WaitAsync(ct).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _prefetched, null, prefetched) != prefetched) return null;
        if (joinedBySpace) text = text.StartsWith(' ') ? text[1..] : "";
        return text.Length == 0 ? null : text;
    }

    private void CancelPrefetch()
    {
        _prefetch?.Cancel();
        _prefetch = null;
        Volatile.Write(ref _prefetched, null);
    }
}
