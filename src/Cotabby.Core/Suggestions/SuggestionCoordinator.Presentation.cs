using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Overlay;
using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

public sealed partial class SuggestionCoordinator
{
    /// <param name="streamed">True for a partial that later partials and the final answer may grow.</param>
    private void Present(
        string requestId, FinalCompletion completion, FocusedField field, int latencyMs, long sequence,
        CancellationToken ct, TypoCorrection? correction = null, bool streamed = false)
    {
        // The final answer of a streamed request takes over the suggestion its partials built.
        if (!streamed && IsStreaming(requestId))
        {
            ReplaceStream(completion, field);
            return;
        }
        // A key handled after the work finished can no longer cancel it, so the sequence is checked too.
        if (ct.IsCancellationRequested || _session is not null || _keySequence != sequence) return;
        // Focus may have moved to another field in the same app while this was decided.
        if (_focusIdentity.Length > 0 && FocusIdentity(field) != _focusIdentity) return;

        var session = new SuggestionSession
        {
            RequestId = requestId,
            PredictedText = completion.Predicted,
            FirstOfferLength = completion.FirstOfferLength,
            ShowFollowingWords = _settings.ShowFollowingWords,
            AcceptPunctuationWithWord = _settings.AcceptPunctuationWithWord,
            Correction = correction,
        };
        string text = session.VisibleText;
        var preceding = field.PrecedingText;
        if (IsDismissed(field, text))
        {
            _logger.LogInformation("Request {ReqId} dropped because the user dismissed it here.", requestId);
            return;
        }
        if (_lastAcceptedTail is { } tail
            && AcceptanceChunker.IsStaleAcceptanceEcho(completion.Predicted, tail.Text, preceding, tail.PrecedingText))
        {
            _logger.LogInformation("Request {ReqId} dropped as an echo of the last accepted chunk.", requestId);
            return;
        }
        _lastAcceptedTail = null;

        _session = session;
        _sessionField = field;
        // A partial is remembered once it stops growing, when it is cleared.
        if (correction is null && !streamed) _anchors.Record(FocusIdentity(field), preceding, completion.Predicted);
        _sessionAcceptedAny = false;
        _sessionPrefetched = false;
        _expectedPreceding = preceding;
        _streamingRequestId = streamed ? requestId : null;
        PublishOffer();
        _overlay.Show(text, field.CaretRect, StyleFor(field, correction), field);
        if (correction is null) Metrics.RecordShown(latencyMs);
        _logger.LogInformation("Suggestion shown req={ReqId} latency={Ms}ms chars={Chars}", requestId, latencyMs, text.Length);
        _logger.LogDebug("Suggestion text req={ReqId} \"{Text}\"", requestId, LogText.Escape(text));

        _ = WatchSessionAsync(requestId);
        // A partial is still growing, so the words after it are not known yet.
        if (!streamed) PrefetchContinuation(session, atOffer: true);

        // A queued press only counts while the key is still owned. Later it would accept unseen text.
        bool acceptQueued = _queuedAccept && _ownership is { } owned && _clock.GetTimestamp() < owned.ExpiresAt;
        _ownership = null;
        _queuedAccept = false;
        if (acceptQueued && correction is null) Accept(all: false, isRepeat: true);
    }

    /// <summary>
    /// Drops the suggestion when the caret leaves the place it was offered at. A click elsewhere in
    /// the same app raises no key and often no focus event, so the field is checked while it shows.
    /// </summary>
    private async Task WatchSessionAsync(string requestId)
    {
        while (!_disposed)
        {
            await Task.Delay(SessionWatchMs).ConfigureAwait(false);
            var live = _focus.Refresh();
            var watching = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                bool current = _session?.RequestId == requestId;
                if (current && live?.HostIsPredicting == true)
                {
                    CancelSession("the app shows its own suggestion");
                    current = false;
                }
                else if (current && !IsStillAtOffer(live))
                {
                    CancelSession("the caret left the suggestion");
                    current = false;
                }
                watching.SetResult(current);
            });
            if (!await watching.Task.ConfigureAwait(false)) return;
        }
    }

    private bool IsStillAtOffer(FocusedField? live) =>
        live is not null && _sessionField is { } offeredIn && SameField(live, offeredIn)
        && FocusIdentity(live) == FocusIdentity(offeredIn)
        && !live.HasSelection
        && SuggestionSessionReconciler.ComparisonNormalized(live.TrailingText)
            == SuggestionSessionReconciler.ComparisonNormalized(offeredIn.TrailingText)
        && SuggestionSessionReconciler.FollowsExpected(
            _expectedPreceding, live.PrecedingText, PublishLagChars + _pendingInsertChars);

    private bool IsDismissed(FocusedField field, string text) =>
        _dismissals.Suppresses(DismissalScope(field), field.PrecedingText, field.TrailingText, text, Now());

    private OverlayStyle StyleFor(FocusedField field, TypoCorrection? correction) =>
        OverlayStylePolicy.StyleFor(_settings.DisplayMode, field, correction is not null);

    private void PublishOffer()
    {
        var settings = _settings;
        var process = _sessionField?.ProcessName;
        _offer = new Offer(
            AcceptWordKeyFor(process),
            ShortcutResolver.AcceptAll(process, settings.ShortcutOverrides, settings.AcceptAll));
    }

    private void ClearSession()
    {
        _session = null;
        _streamingRequestId = null;
        _sessionField = null;
        _offer = null;
        _overlay.Hide();
    }

    /// <param name="remember">
    /// Keep the suggestion so Backspace or retyping can bring it back, as upstream does with every
    /// suggestion it invalidates. A dismissed one is not kept.
    /// </param>
    private void CancelSession(string reason, bool remember = true)
    {
        if (_session is not null) _logger.LogInformation("Suggestion cleared ({Reason}).", reason);
        if (remember && _session is { Correction: null } dying && _sessionField is { } offeredIn)
        {
            _anchors.Record(FocusIdentity(offeredIn), _expectedPreceding, dying.PredictedText);
        }
        ResetAcceptKeyState();
        ClearSession();
        _prediction = null;
        _work.Cancel();
        // Typing through to the end clears the session without coming here, which keeps its continuation.
        CancelPrefetch();
    }

    private void ResetAcceptKeyState()
    {
        _doubleTap.Reset();
        _ownership = null;
        _queuedAccept = false;
    }
}
