using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

public sealed partial class SuggestionCoordinator
{
    private void Accept(bool all, bool isRepeat)
    {
        if (_session is not { } session || _sessionField is not { } field)
        {
            // The press raced the accept that exhausted the suggestion, so it belongs to the continuation.
            _queuedAccept = !all && _ownership is not null;
            return;
        }

        var settings = _settings;
        _streamingRequestId = null;
        if (session.Correction is { } correction)
        {
            ClearSession();
            // Keep the key for a moment so a quick second press accepts the next suggestion
            // instead of reaching the host as a Tab.
            OwnAcceptKeyBriefly(field);
            string offeredFor = _expectedPreceding;
            Chain(() => ApplyCorrectionAsync(correction, requiresTrailingSpace: false, (field, offeredFor)));
            return;
        }

        bool doubleTapApplies = settings.DoubleTapAcceptsAll
            && !isRepeat
            && !ShortcutResolver.OverridesAcceptAll(field.ProcessName, settings.ShortcutOverrides);
        double now = Now();
        if (!all && doubleTapApplies && _doubleTap.ConsumeDoubleTap(session.RequestId, now))
        {
            all = true;
        }
        _doubleTap.Reset();

        string visible = session.VisibleText;
        string chunk = all
            ? visible
            : settings.Granularity == AcceptanceGranularity.Phrase
                ? AcceptanceChunker.NextPhrase(visible, settings.AcceptPunctuationWithWord)
                : AcceptanceChunker.NextChunk(visible, settings.AcceptPunctuationWithWord);
        if (chunk.Length == 0) chunk = visible;
        // The space after a word may sit in the part of the prediction not shown yet.
        if (settings.AddSpaceAfterAccept) chunk = AcceptanceChunker.ConsumingTrailingSpace(chunk, session.PredictedText);

        var next = session.Advancing(chunk.Length);
        bool exhausted = next.IsExhausted;
        bool firstChunk = !_sessionAcceptedAny;

        string expected = _expectedPreceding;
        string insertion = AcceptanceChunker.InsertionChunk(chunk, expected);
        if (exhausted && settings.AddSpaceAfterAccept) insertion = AcceptanceChunker.AppendingTrailingSpace(insertion);
        var accepted = new AcceptedChunk(
            session.RequestId, field, expected, chunk, insertion, exhausted, firstChunk, _pendingInsertChars, _keySequence);
        _expectedPreceding = expected + insertion;
        _pendingInsertChars += insertion.Length;
        // The next key is counted against a read taken before this insertion.
        if (_publishBaseline is not null) _expectedDelta += insertion.Length;

        if (exhausted)
        {
            ClearSession();
            // Keep owning the accept key so a fast follow-up press is queued for the
            // continuation instead of leaking into the host. Only when a continuation will be asked for.
            if (SuggestionRequestFactory.ShouldGenerate(_expectedPreceding, settings.SuggestWithinWords))
            {
                OwnAcceptKeyBriefly(field);
            }
        }
        else
        {
            _session = next;
            _sessionAcceptedAny = true;
            _overlay.Update(next.VisibleText);
            if (!all && doubleTapApplies) _doubleTap.RecordWordAccept(session.RequestId, now);
        }

        Chain(() => InsertAcceptedAsync(accepted));
    }

    /// <summary>Runs edits of the field one after another on the thread pool.</summary>
    private void Chain(Func<Task> edit) =>
        _insertChain = _insertChain.ContinueWith(_ => edit(), TaskScheduler.Default).Unwrap();

    private void OwnAcceptKeyBriefly(FocusedField field) =>
        _ownership = new AcceptKeyOwnership(
            AcceptWordKeyFor(field.ProcessName),
            _clock.GetTimestamp() + (long)(PostExhaustionWindow.TotalSeconds * _clock.TimestampFrequency));

    /// <summary>One accept, decided on the UI context and carried out on the thread pool.</summary>
    private sealed record AcceptedChunk(
        string RequestId, FocusedField OfferedIn, string ExpectedPreceding, string Chunk, string Insertion,
        bool Exhausted, bool FirstChunk, int PendingBefore, long KeySequence);

    private async Task InsertAcceptedAsync(AcceptedChunk accepted)
    {
        try
        {
            // The text goes where the caret is, so it is only sent if the caret is still where the
            // suggestion was shown.
            var field = _focus.Refresh();
            // Chunks accepted just before this one may not have reached the field yet.
            if (field is null || !SameField(field, accepted.OfferedIn)
                || !SuggestionSessionReconciler.FollowsExpected(
                    accepted.ExpectedPreceding, field.PrecedingText, PublishLagChars + accepted.PendingBefore))
            {
                Post(() => CancelSession("the caret left the suggestion before the accept"));
                return;
            }

            if (accepted.Insertion.Length > 0
                && !await _inserter.InsertAsync(field, accepted.Insertion, CancellationToken.None).ConfigureAwait(false))
            {
                Post(() => CancelSession("insertion failed"));
                return;
            }
            Metrics.RecordAccepted(accepted.Chunk, accepted.FirstChunk);
            _logger.LogInformation(
                "Accepted req={ReqId} chars={Chars} exhausted={Exhausted}",
                accepted.RequestId, accepted.Insertion.Length, accepted.Exhausted);
            _logger.LogDebug(
                "Accepted text req={ReqId} \"{Inserted}\"", accepted.RequestId, LogText.Escape(accepted.Insertion));

            if (accepted.Exhausted)
            {
                Post(() =>
                {
                    _lastAcceptedTail = new AcceptedTail(accepted.Chunk, field.PrecedingText);
                    // A key typed since then already counted this insertion and asked for its own prediction.
                    if (_keySequence != accepted.KeySequence) return;
                    SetPublishBaseline(field, accepted.Insertion.Length);
                    SchedulePrediction(typed: null);
                });
                return;
            }

            await ReanchorAsync(accepted.RequestId, PostInsertSettleMs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Accept failed for {ReqId}.", accepted.RequestId);
            Post(() => CancelSession("accept failed"));
        }
        finally
        {
            Post(() => _pendingInsertChars -= accepted.Insertion.Length);
        }
    }

    /// <summary>
    /// Swaps the typo for its fix. The edit is planned against the live text, and once it starts
    /// it is not cancelled, because a cancelled edit would leave the word deleted. An offered fix is
    /// only applied in the field it was offered in, still holding the text it was offered for.
    /// </summary>
    private async Task ApplyCorrectionAsync(
        TypoCorrection correction, bool requiresTrailingSpace, (FocusedField Field, string Preceding)? offered = null)
    {
        try
        {
            if (_focus.Refresh() is not { } field || field.HasSelection) return;
            if (offered is { } at && (!SameField(field, at.Field)
                || !SuggestionSessionReconciler.FollowsExpected(at.Preceding, field.PrecedingText, PublishLagChars)))
            {
                return;
            }
            var plan = TypoCorrectionReplacementPlanner.Plan(
                field.PrecedingText, correction.Word, correction.CorrectedWord, requiresTrailingSpace);
            if (plan is not { } fix) return;
            await _inserter.ReplaceAsync(field, fix.DeletingUtf16Count, fix.ReplacementText, CancellationToken.None)
                .ConfigureAwait(false);
            // The edit publishes asynchronously. Predicting before it lands could offer the same fix again.
            Post(() =>
            {
                SetPublishBaseline(field);
                SchedulePrediction(typed: null, requiresTextChange: true);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying a correction failed.");
        }
    }

    /// <summary>Moves the ghost to the caret once the host has published an insert or a typed key.</summary>
    private async Task ReanchorAsync(string requestId, int settleMs)
    {
        await Task.Delay(settleMs).ConfigureAwait(false);
        var after = _focus.Refresh(readHostText: true);
        RecordHistory(after);
        if (after is null || after.CaretRect.IsEmpty) return;
        Post(() =>
        {
            if (_session is not { } live || live.RequestId != requestId) return;
            _sessionField = after;
            _overlay.Show(live.VisibleText, after.CaretRect, StyleFor(after, live.Correction), after);
        });
    }
}
