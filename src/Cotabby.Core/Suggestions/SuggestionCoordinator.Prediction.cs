using System.Diagnostics;
using Cotabby.Core.Focus;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

public sealed partial class SuggestionCoordinator
{
    /// <summary>Gives up on a prediction typing moved past and asks again for the live text.</summary>
    private void RestartPrediction(TypingPrediction prediction, string reason) => Post(() =>
    {
        if (_prediction != prediction) return;
        _logger.LogDebug("Prediction restarted: {Reason}.", reason);
        SchedulePrediction(typed: null);
    });

    /// <param name="requiresTextChange">Wait until the text differs from the baseline, as after a correction.</param>
    private void SchedulePrediction(char? typed, bool requiresTextChange = false)
    {
        CancelPrewarm();
        _prediction = null;
        var requestId = NewRequestId("req_");
        var settings = _settings;
        var baseline = _publishBaseline;
        int delta = _expectedDelta;
        bool awaitsChange = _publishAwaitsChange;
        long sequence = _keySequence;
        long submittedAt = _clock.GetTimestamp();
        // The debounce runs alongside the wait for the host to show the key, not after it.
        _ = _work.Submit(
            ct => PredictAsync(
                requestId, settings, new PublishWait(baseline, delta, typed, requiresTextChange, awaitsChange),
                sequence, submittedAt, ct),
            TimeSpan.Zero);
    }

    private async Task PredictAsync(
        string requestId, SuggestionSettings settings, PublishWait wait, long sequence, long submittedAt,
        CancellationToken ct)
    {
        TypingPrediction? prediction = null;
        PartialStream? stream = null;
        bool presented = false;
        try
        {
            var (field, gaveUp) = await WaitForHostPublishAsync(wait, ct).ConfigureAwait(false);
            if (gaveUp)
            {
                // A replacement the host never showed must not be corrected again from the old
                // text. The next real key asks again, and a queued accept is released now.
                _logger.LogDebug("Request {ReqId} abandoned: the edit never showed in the field.", requestId);
                Post(() =>
                {
                    if (_keySequence != sequence) return;
                    ResetAcceptKeyState();
                    CancelPrefetch();
                });
                return;
            }
            // Whatever is left of the quiet time after a key, once the host has shown it. A key in
            // the meantime cancels this work, so a fast typist still starts no generation per key.
            var quiet = _work.Debounce - _clock.GetElapsedTime(submittedAt);
            if (quiet > TimeSpan.Zero) await Task.Delay(quiet, _clock, ct).ConfigureAwait(false);
            RecordHistory(field);
            if (field is null)
            {
                _logger.LogDebug("Request {ReqId} skipped: no supported field has focus.", requestId);
                return;
            }
            Post(() =>
            {
                if (_keySequence == sequence) SetPublishBaseline(field);
                _ime.Observe(field.ImeLanguageId);
                AdoptIdentity(field);
            });
            if (field.ImeLanguageId != 0 && _ime.IsComposing)
            {
                _logger.LogDebug("Request {ReqId} skipped: the input method is composing.", requestId);
                return;
            }

            TrackScreen(field, settings);
            if (SuggestionAvailability.DisabledReason(settings, field) is { } reason)
            {
                _logger.LogDebug("Request {ReqId} skipped: {Reason}", requestId, reason);
                return;
            }
            if (field.HostIsPredicting)
            {
                _logger.LogDebug("Request {ReqId} skipped: the app shows its own suggestion.", requestId);
                return;
            }
            if (!SuggestionAvailability.HasTextToContinue(field))
            {
                _logger.LogDebug("Request {ReqId} skipped: nothing to continue.", requestId);
                return;
            }

            string preceding = field.PrecedingText;
            if (await HandleTypoAsync(requestId, settings, field, sequence, ct).ConfigureAwait(false)) return;
            // A completion inside a token would duplicate or splice what follows the caret.
            if (!SuggestionRequestFactory.ShouldGenerate(preceding, field.TrailingText, settings.SuggestWithinWords))
            {
                _logger.LogDebug("Request {ReqId} skipped: the caret is inside a word or token.", requestId);
                return;
            }
            if (await TryRestoreAnchorAsync(requestId, settings, field, sequence, ct).ConfigureAwait(false))
            {
                _logger.LogDebug("Request {ReqId} answered by a remembered suggestion.", requestId);
                return;
            }
            if (!_engine.IsReady)
            {
                _logger.LogDebug("Request {ReqId} skipped: the engine is not ready.", requestId);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            string text;
            if (await TakePrefetchedAsync(field, ct).ConfigureAwait(false) is { } prefetched)
            {
                text = prefetched;
                _logger.LogDebug("Request {ReqId} answered by the prefetched continuation.", requestId);
            }
            else
            {
                // An endpoint is billed per request, so it gets no lookahead.
                if (settings.PredictAheadWhileTyping && !settings.UsesEndpoint)
                {
                    // Set from the pool. Each new request clears it on the UI context first, so a
                    // request cancelled meanwhile must not put its own back over the newer one's.
                    var created = new TypingPrediction(field, sequence, ct);
                    if (Interlocked.CompareExchange(ref _prediction, created, null) is null)
                    {
                        if (ct.IsCancellationRequested) Interlocked.CompareExchange(ref _prediction, null, created);
                        else prediction = created;
                    }
                }
                if (settings.StreamWhileGenerating) stream = new PartialStream(requestId, field, settings, sequence, stopwatch, ct);
                var predicting = prediction;
                var streaming = stream;
                Action<string>? onPartial = predicting is null && streaming is null ? null : partial =>
                {
                    if (predicting is not null && !predicting.Receive(partial, final: false))
                    {
                        RestartPrediction(predicting, "the model left what was typed");
                    }
                    if (streaming is not null) QueuePartial(streaming, partial);
                };
                var request = BuildRequest(field, requestId, settings);
                _logger.LogDebug(
                    "Request {ReqId} context: screen={Screen} clipboard={Clipboard} history={History} surface={Surface}",
                    requestId, request.ScreenContext.Length, request.ClipboardContext.Length,
                    request.HistoryExamples.Count, request.SurfaceLines.Count);
                text = await GenerateAsync(request, ct, onPartial).ConfigureAwait(false);
                // A cancelled request says nothing about how fast the engine is.
                if (!ct.IsCancellationRequested) UpdateDebounce(settings, (int)stopwatch.ElapsedMilliseconds);
            }
            int latencyMs = (int)stopwatch.ElapsedMilliseconds;
            ct.ThrowIfCancellationRequested();
            if (prediction is not null && !prediction.Receive(text, final: true))
            {
                RestartPrediction(prediction, "the prediction does not cover what was typed");
                return;
            }
            if (text.Length == 0 && CaretWordContext.UnfinishedWord(preceding) is null) return;

            if (await AwaitDeliveryAsync(requestId, field, prediction, sequence, ct).ConfigureAwait(false) is not { } delivery)
            {
                return;
            }
            var (live, typedAhead, shownAt) = delivery;
            if (typedAhead.Length > 0)
            {
                text = text[typedAhead.Length..];
                if (string.IsNullOrWhiteSpace(text))
                {
                    RestartPrediction(prediction!, "typing used up the prediction");
                    return;
                }
                if (!SuggestionRequestFactory.ShouldGenerate(live.PrecedingText, live.TrailingText, settings.SuggestWithinWords)) return;
                // The request checked its answer against the text after the caret, but not what is
                // left of it once the typed keys are taken off.
                if (TrailingDuplicationFilter.DuplicatesTrailingText(text, live.TrailingText)) return;
                _logger.LogDebug("Request {ReqId} kept through {Count} typed characters.", requestId, typedAhead.Length);
            }
            // An unusable result mid-word may still offer a local word ending.
            if (CompletionFinalizer.Finalize(text, live, settings, _engine.SpacingIsExact, _typos) is not { } visible)
            {
                _logger.LogInformation("Request {ReqId} dropped by the final presentation checks.", requestId);
                return;
            }

            // Suggestions may have been paused, or battery saver turned on, while this generated.
            if (PausedReason() is { } paused)
            {
                _logger.LogInformation("Request {ReqId} dropped: {Reason}", requestId, paused);
                return;
            }

            presented = true;
            Post(() =>
            {
                Present(requestId, visible, live, latencyMs, shownAt, ct);
                // A key kept after the field was read changed the text this answer was checked against.
                if (_keySequence != shownAt && _session is null && !ct.IsCancellationRequested) SchedulePrediction(typed: null);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request {ReqId} failed.", requestId);
        }
        finally
        {
            if (stream is not null)
            {
                stream.Finished = true;
                // A streamed suggestion the final answer did not confirm goes away with it.
                if (!presented) Post(() => EndStream(requestId, "the final answer was dropped"));
            }
            // A finished request keeps no keys. Keys it kept for an answer that was then dropped
            // still need a request of their own.
            if (prediction?.Close() is { Length: > 0 } && !presented && !ct.IsCancellationRequested)
            {
                RestartPrediction(prediction, "the answer was dropped after keys were kept");
            }
            else if (prediction is not null)
            {
                Post(() =>
                {
                    if (_prediction == prediction) _prediction = null;
                });
            }
        }
    }

    /// <summary>
    /// Takes on a new window title or placeholder of the focused field, which can change with no
    /// focus event, as when a chat app switches conversations in the same composer. What was
    /// offered or remembered for the old conversation is dropped, as upstream does.
    /// </summary>
    private void AdoptIdentity(FocusedField field)
    {
        string identity = FocusIdentity(field);
        if (_focusIdentity.Length == 0 || identity == _focusIdentity) return;
        // Only the same field in the same app. Another field changes through a focus event.
        if (!_focusIdentity.StartsWith($"{field.ProcessId}:{field.FieldId}:", StringComparison.Ordinal)) return;
        _logger.LogInformation("The focused field's title or placeholder changed.");
        _focusIdentity = identity;
        if (_session is not null)
        {
            ResetAcceptKeyState();
            ClearSession();
        }
        CancelPrefetch();
        _anchors.Clear();
    }

    /// <summary>
    /// Shows again a recent suggestion the text before the caret is on the way to, with no
    /// generation, after the same checks a fresh answer passes against the field as it is now.
    /// False leaves the request to generate as usual.
    /// </summary>
    private async Task<bool> TryRestoreAnchorAsync(
        string requestId, SuggestionSettings settings, FocusedField field, long sequence, CancellationToken ct)
    {
        string identity = FocusIdentity(field);
        if (_anchors.Remainder(identity, field.PrecedingText) is not { Length: > 0 } remainder) return false;
        if (TrailingDuplicationFilter.DuplicatesTrailingText(remainder, field.TrailingText)) return false;
        // Mid-word, a restore waits for a pause like a fresh answer, so it goes the usual way.
        if (_cadence.RemainingDelay(identity, field.PrecedingText, Now()) > 0) return false;
        if (CompletionFinalizer.Finalize(remainder, field, settings, _engine.SpacingIsExact, _typos) is not { } completion)
        {
            return false;
        }
        if (PausedReason() is not null) return false;
        // Like upstream, a dismissed restore is generated afresh. The dismissals live on the UI context.
        var dismissed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => dismissed.SetResult(IsDismissed(field, completion.Visible)));
        if (await dismissed.Task.WaitAsync(ct).ConfigureAwait(false)) return false;
        Post(() => Present(requestId, completion, field, latencyMs: 0, sequence, ct));
        return true;
    }

    /// <param name="awaitsChange">
    /// The keys since <paramref name="field"/> change the text in ways a count cannot follow, so the
    /// next read waits for the text, field or caret to differ from it.
    /// </param>
    private void SetPublishBaseline(FocusedField? field, int delta = 0, bool awaitsChange = false)
    {
        _publishBaseline = field;
        _expectedDelta = delta;
        _publishAwaitsChange = awaitsChange && field is not null;
    }

    /// <summary>Asks for a suggestion against the field as it is now, with no typed keys to wait for.</summary>
    private void PredictFromFreshRead()
    {
        SetPublishBaseline(null);
        SchedulePrediction(typed: null);
    }

    /// <summary>What a request waits for before it reads the field.</summary>
    /// <param name="RequiresTextChange">Wait until the text differs from the baseline, as after a correction, or give up.</param>
    /// <param name="AwaitsChange">Wait until the text, field or caret differs from the baseline, or read it anyway.</param>
    private readonly record struct PublishWait(
        FocusedField? Baseline, int Delta, char? Typed, bool RequiresTextChange, bool AwaitsChange);

    private static string NewRequestId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private SuggestionRequest BuildRequest(FocusedField field, string requestId, SuggestionSettings settings) =>
        SuggestionRequestFactory.Build(
            field, requestId, settings, settings.ClipboardContext ? _clipboard?.For(field) : null,
            History?.HistoryExamples(field, settings.UsesEndpoint),
            settings.ScreenContext ? _screen?.For(field) : null);

    /// <summary>Paces the next request by how long the engine just took.</summary>
    private void UpdateDebounce(SuggestionSettings settings, int? lastLatencyMs) =>
        _work.Debounce = TimeSpan.FromMilliseconds(
            DebouncePolicy.Milliseconds(lastLatencyMs, settings.DebounceMs, settings.UsesEndpoint));

    /// <summary>
    /// Runs the typo gate on the word just committed. Returns true when the gate
    /// replaced the completion with a suppression, an offer or an automatic fix.
    /// </summary>
    private async Task<bool> HandleTypoAsync(
        string requestId, SuggestionSettings settings, FocusedField field, long sequence, CancellationToken ct)
    {
        if (_typos is null) return false;
        string preceding = field.PrecedingText;
        var advice = _typos.Advise(preceding, settings.SpellingDictionaries, settings.Typos);
        var decision = advice.Decision;
        switch (decision.Action)
        {
            case TypoGateAction.Proceed:
                return false;

            case TypoGateAction.ApplyCorrection when advice.Replacement is not null:
                if (_lastAutoFixedText == preceding) return true;
                _lastAutoFixedText = preceding;
                ct.ThrowIfCancellationRequested();
                await ApplyCorrectionAsync(
                    new TypoCorrection(decision.Word!, decision.CorrectedWord!), requiresTrailingSpace: true,
                    keySequence: sequence).ConfigureAwait(false);
                return true;

            case TypoGateAction.OfferCorrection:
                var correction = new TypoCorrection(decision.Word!, decision.CorrectedWord!);
                Post(() => Present(
                    requestId, new FinalCompletion(decision.CorrectedWord!), field, latencyMs: 0, sequence, ct, correction));
                return true;

            default:
                return true;
        }
    }

    /// <returns>The field, and GaveUp when an edit that had to show never did.</returns>
    private async Task<(FocusedField? Field, bool GaveUp)> WaitForHostPublishAsync(PublishWait wait, CancellationToken ct)
    {
        int waited = 0;
        int interval = HostPublishFirstPollMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var field = _focus.Refresh();
            if (field is null) return (null, false);
            if (HasPublished(field, wait, waited)) return (field, false);
            // Upstream abandons a replacement the host did not show rather than read the old text.
            if (waited >= HostPublishCeilingMs) return (field, wait.RequiresTextChange && wait.Baseline is not null);
            await Task.Delay(interval, ct).ConfigureAwait(false);
            waited += interval;
            interval = HostPublishPollMs;
        }
    }

    private static bool HasPublished(FocusedField field, PublishWait wait, int waited)
    {
        var (baseline, delta, typed, requiresTextChange, awaitsChange) = wait;
        var preceding = field.PrecedingText;
        bool textChanged = baseline is not null
            && SuggestionSessionReconciler.ComparisonNormalized(preceding)
                != SuggestionSessionReconciler.ComparisonNormalized(baseline.PrecedingText);
        if (requiresTextChange && baseline is not null) return textChanged;
        if (awaitsChange && baseline is not null)
        {
            // Like upstream, the read is taken once the text, the field or the caret moved on from
            // the snapshot at the key. A key typed after that must also have arrived.
            // Forward Delete changes only the text after the caret.
            bool moved = !SameField(field, baseline) || field.CaretOffset != baseline.CaretOffset
                || field.HasSelection != baseline.HasSelection
                || SuggestionSessionReconciler.ComparisonNormalized(field.TrailingText)
                    != SuggestionSessionReconciler.ComparisonNormalized(baseline.TrailingText);
            return (textChanged || moved) && (typed is not { } t || EndsWithTyped(preceding, t));
        }
        if (baseline is null || baseline.ProcessId != field.ProcessId)
        {
            // With nothing to count against, the typed character itself is the evidence. A dead key
            // can make the host insert an accented form of it, which then waits out the ceiling.
            return typed is { } c ? EndsWithTyped(preceding, c) : waited > 0;
        }

        int expected = baseline.PrecedingText.Length + delta;
        return delta switch
        {
            > 0 => preceding.Length >= expected,
            < 0 => preceding.Length <= expected,
            _ => true,
        };
    }

    private static bool EndsWithTyped(string preceding, char c) =>
        preceding.EndsWith(c) || (c == ' ' && preceding.EndsWith('\u00A0'));

    /// <summary>
    /// Waits for a pause in typing and for the host to show what was typed during generation, then
    /// reads the field the answer is shown against. Null drops the answer, after asking again when
    /// the typing it kept could not be confirmed.
    /// </summary>
    private async Task<(FocusedField Live, string TypedAhead, long Sequence)?> AwaitDeliveryAsync(
        string requestId, FocusedField field, TypingPrediction? prediction, long requestSequence, CancellationToken ct)
    {
        string? pausedFor = null;
        while (true)
        {
            var (typed, sequence) = prediction?.Progress ?? ("", requestSequence);
            string expected = field.PrecedingText + typed;
            // Mid-word, a result waits for a pause a little longer than the recent gap between
            // letters, so it does not flash up between two keys. Cancellation still owns the wait.
            if (pausedFor != expected)
            {
                pausedFor = expected;
                double pause = _cadence.RemainingDelay(FocusIdentity(field), expected, Now());
                if (pause > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pause), ct).ConfigureAwait(false);
                    continue;
                }
            }

            // Generation took a while. Show the suggestion only if the text it continues is still there,
            // in the same field, with the same text after the caret and nothing selected.
            var live = _focus.Refresh(readHostText: true);
            ct.ThrowIfCancellationRequested();
            if (live is not null && !live.HostIsPredicting && TypingPrediction.IsSameCaretPlace(field, live)
                && SuggestionSessionReconciler.ComparisonNormalized(live.PrecedingText)
                    == SuggestionSessionReconciler.ComparisonNormalized(expected))
            {
                if (prediction is null || prediction.Progress == (typed, sequence)) return (live, typed, sequence);
                continue;
            }
            if (prediction is null || typed.Length == 0 || live?.HostIsPredicting == true)
            {
                _logger.LogInformation("Request {ReqId} dropped because the field changed during generation.", requestId);
                return null;
            }
            if (live is null || !prediction.Accepts(live) || prediction.Expiration(live) <= Now())
            {
                RestartPrediction(prediction, "the field did not show what was typed");
                return null;
            }
            await Task.Delay(TypedAheadPollMs, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> GenerateAsync(SuggestionRequest request, CancellationToken ct, Action<string>? onPartial = null)
    {
        using var watchdog = new CancellationTokenSource(GenerationWatchdog);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
        try
        {
            return await _engine.GenerateAsync(request, linked.Token, onPartial).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) _logger.LogWarning("Request {ReqId} hit the watchdog.", request.RequestId);
            return string.Empty;
        }
    }
}
