using System.Diagnostics;
using Cotabby.Core.Focus;
using Cotabby.Core.Text;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

public sealed partial class SuggestionCoordinator
{
    /// <summary>
    /// Partials of one streamed request. They arrive on the decode thread at token rate, so only
    /// the newest waits and at most one drain is queued on the UI context at a time.
    /// </summary>
    private sealed class PartialStream(
        string requestId, FocusedField field, SuggestionSettings settings, long sequence, Stopwatch elapsed,
        CancellationToken ct)
    {
        public string RequestId { get; } = requestId;
        public FocusedField Field { get; } = field;
        public SuggestionSettings Settings { get; } = settings;
        public long Sequence { get; } = sequence;
        public Stopwatch Elapsed { get; } = elapsed;
        public CancellationToken Ct { get; } = ct;
        public readonly object Gate = new();
        public string? Pending;
        public bool DrainQueued;
        public volatile bool Finished;

        /// <summary>What this stream shows, read by the checks off the UI context.</summary>
        public volatile string Rendered = "";
    }

    /// <summary>
    /// Takes the newest partial off the decode thread. Its checks run on the thread pool, like a
    /// final answer's: they consult the Windows spell checker, a COM object that must stay on the
    /// pool's threads, where every other check uses it. Only the render reaches the UI context.
    /// </summary>
    private void QueuePartial(PartialStream stream, string partial)
    {
        lock (stream.Gate)
        {
            stream.Pending = partial;
            if (stream.DrainQueued) return;
            stream.DrainQueued = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(_ => DrainPartial(stream), null);
    }

    private void DrainPartial(PartialStream stream)
    {
        string? partial;
        lock (stream.Gate)
        {
            partial = stream.Pending;
            stream.Pending = null;
            stream.DrainQueued = false;
        }
        if (partial is null || stream.Finished || stream.Ct.IsCancellationRequested) return;
        try
        {
            string rendered = stream.Rendered;
            string candidate = StreamedGhostTextPolicy.CompletedBufferedPrediction(partial, GraphemeString.CountOf(rendered));
            if (string.IsNullOrWhiteSpace(candidate)) return;
            if (CompletionFinalizer.Finalize(candidate, stream.Field, stream.Settings, _engine.SpacingIsExact, _typos)
                is not { } completion)
            {
                return;
            }
            // Upstream trims the unfinished word from the checked text, where the shown count is
            // measured. The trim above keeps the checks off a fragment, but the leading space they
            // decide can shift the raw text by one, so the checked text is trimmed again.
            completion = Trimmed(completion, GraphemeString.CountOf(rendered));
            if (!StreamedGhostTextPolicy.IsRenderableExtension(completion.Predicted, rendered.Length > 0 ? rendered : null)) return;
            Post(() => ShowPartial(stream, partial, completion));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A streamed partial of {ReqId} was skipped.", stream.RequestId);
        }
    }

    private static FinalCompletion Trimmed(FinalCompletion completion, int shownCount)
    {
        string predicted = StreamedGhostTextPolicy.CompletedBufferedPrediction(completion.Predicted, shownCount);
        if (predicted.Length == completion.Predicted.Length) return completion;
        return new FinalCompletion(
            completion.Visible.Length <= predicted.Length ? completion.Visible : predicted, predicted);
    }

    /// <summary>
    /// Shows a checked partial as a real, acceptable suggestion, so the user can take the first words
    /// the moment they appear. It only ever extends what shows.
    /// </summary>
    private void ShowPartial(PartialStream stream, string partial, FinalCompletion completion)
    {
        if (stream.Finished || stream.Ct.IsCancellationRequested || _keySequence != stream.Sequence) return;
        bool growing = IsStreaming(stream.RequestId);
        if (_session is not null && !growing) return;
        // A newer partial may have been shown while this one was checked.
        if (!StreamedGhostTextPolicy.IsRenderableExtension(completion.Predicted, growing ? stream.Rendered : null)) return;
        var field = stream.Field;
        string identity = FocusIdentity(field);
        if (_focusIdentity.Length > 0 && identity != _focusIdentity) return;
        if (PausedReason() is not null) return;
        // Mid-word, nothing flashes up between two keys, as with a final answer. The partial is tried
        // again after the pause unless a newer one has arrived by then.
        double pause = _cadence.RemainingDelay(identity, field.PrecedingText, Now());
        if (pause > 0)
        {
            _ = RetryPartialAsync(stream, partial, pause);
            return;
        }

        if (!growing)
        {
            Present(stream.RequestId, completion, field, (int)stream.Elapsed.ElapsedMilliseconds, stream.Sequence, stream.Ct,
                streamed: true);
            if (_streamingRequestId == stream.RequestId) stream.Rendered = completion.Predicted;
            return;
        }
        if (IsDismissed(field, completion.Visible)) return;
        var grown = Grown(_session!, completion);
        _session = grown;
        stream.Rendered = completion.Predicted;
        _overlay.Update(grown.VisibleText);
        _logger.LogDebug("Streamed suggestion req={ReqId} grew to {Chars} chars.", stream.RequestId, grown.VisibleText.Length);
    }

    private async Task RetryPartialAsync(PartialStream stream, string partial, double pauseSeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(pauseSeconds), _clock, stream.Ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (stream.Gate)
        {
            if (stream.Pending is not null || stream.DrainQueued) return;
        }
        QueuePartial(stream, partial);
    }

    /// <summary>Clears a streamed suggestion that is still growing, once its request ends without a final answer.</summary>
    private void EndStream(string requestId, string reason)
    {
        if (_streamingRequestId != requestId) return;
        _streamingRequestId = null;
        if (_session?.RequestId != requestId) return;
        _logger.LogInformation("Suggestion cleared ({Reason}).", reason);
        ClearSession();
    }

    /// <summary>
    /// Swaps a streamed suggestion for the final answer, which was checked against the same text.
    /// A final answer that no longer begins with what shows leaves the suggestion as it is.
    /// </summary>
    private void ReplaceStream(FinalCompletion completion, FocusedField field)
    {
        _streamingRequestId = null;
        if (_session is not { } current) return;
        if (!completion.Predicted.StartsWith(current.VisibleText, StringComparison.Ordinal)
            || IsDismissed(field, completion.Visible))
        {
            return;
        }
        var final = Grown(current, completion);
        _session = final;
        _overlay.Update(final.VisibleText);
        _logger.LogDebug("Final answer req={ReqId} replaced the streamed suggestion ({Chars} chars).", final.RequestId, final.VisibleText.Length);
        PrefetchContinuation(final, atOffer: true);
    }

    private bool IsStreaming(string requestId) => _streamingRequestId == requestId && _session?.RequestId == requestId;

    private static SuggestionSession Grown(SuggestionSession session, FinalCompletion completion) =>
        session with { PredictedText = completion.Predicted, FirstOfferLength = completion.FirstOfferLength };
}
