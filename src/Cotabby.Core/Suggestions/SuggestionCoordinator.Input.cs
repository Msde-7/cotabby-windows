using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

public sealed partial class SuggestionCoordinator
{
    private void OnFocusChanged(object? sender, FocusedField? field)
    {
        Post(() =>
        {
            RecordHistory(field);
            int pid = field?.ProcessId ?? 0;
            string previousIdentity = _focusIdentity;
            _focusIdentity = field is null ? "" : FocusIdentity(field);
            if (previousIdentity != _focusIdentity)
            {
                _ime.Reset();
                PrewarmField(field);
            }
            _ime.Observe(field?.ImeLanguageId ?? 0);
            TrackScreen(field);
            bool sameHost = pid != 0 && (_focusedPid == 0 || pid == _focusedPid);
            _focusedPid = pid;
            // A host that names no field, or names the same one again, is the same place. Another
            // named field in the same app is a different place, so work for the old one stops.
            string fieldId = field?.FieldId ?? "";
            bool sameField = fieldId.Length == 0 || previousIdentity.Length == 0 || previousIdentity == _focusIdentity;
            if (sameHost && sameField && (_sessionField is null || _sessionField.FieldId == fieldId)) return;
            SetPublishBaseline(null);
            CancelSession("focus moved");
            CancelPrefetch();
        });
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        var ev = args.Event;
        if (!ev.IsKeyDown || args.Suppress) return;

        if (InlineCaptureActive?.Invoke() == true)
        {
            Post(() => CancelSession("inline capture active"));
            return;
        }

        if (_offer is { } offer)
        {
            bool word = offer.AcceptWord.Matches(ev);
            bool all = !word && offer.AcceptAll.Matches(ev);
            if (word || all)
            {
                // Suppressing here is the only way to eat the key before the host sees it.
                args.Suppress = true;
                // The accept is queued before the work is cancelled. Cancelling can end a streamed
                // request at once, and its cleanup must not reach the UI context ahead of the accept.
                PostAccept(all, ev.IsRepeat);
                _work.Cancel();
                return;
            }
        }
        else if (_ownership is { } ownership
                 && ownership.AcceptWord.Matches(ev)
                 && _clock.GetTimestamp() < ownership.ExpiresAt)
        {
            args.Suppress = true;
            Post(() => _queuedAccept = _ownership is not null);
            return;
        }

        if (SynchronizationContext.Current == _ui) HandleEvent(ev);
        else _ui.Post(_handleEvent, ev);
    }

    // Kept out of OnKeyEvent so its locals are not captured, which would allocate on every hook callback.
    private void PostAccept(bool all, bool isRepeat) => Post(() => Accept(all, isRepeat));

    private void HandleEvent(KeyboardEvent ev)
    {
        if (ev.Kind == KeyKind.Other)
        {
            // Any key between two accept presses breaks the pair. Modifiers alone do not.
            if (!KeyClassifier.IsModifier(ev.VirtualKey)) _doubleTap.Reset();
            return;
        }

        ResetAcceptKeyState();

        if (PausedReason() is { } reason)
        {
            _logger.LogDebug("Key ignored: {Reason}", reason);
            CancelSession(reason);
            return;
        }

        TrackForHostPublish(ev);
        if (HoldsForComposition(ev))
        {
            _logger.LogDebug("Key held for the input method (language {Language}, composing {Composing}).", _ime.LanguageId, _ime.IsComposing);
            return;
        }
        _cadence.Record(_focusIdentity, ev.Kind == KeyKind.Character ? ev.Character.ToString() : "", Now());

        if (_session is { } session)
        {
            if (ev.Kind == KeyKind.Escape && _sessionField is { } dismissedIn)
            {
                _dismissals.Record(
                    DismissalScope(dismissedIn), dismissedIn.PrecedingText, dismissedIn.TrailingText,
                    session.VisibleText, Now());
            }

            var result = SuggestionSessionReconciler.Apply(session, ev);
            switch (result.Outcome)
            {
                case SuggestionSessionReconciler.Outcome.Ignore:
                    return;
                case SuggestionSessionReconciler.Outcome.Advance when result.Next is { VisibleText.Length: > 0 } next:
                    _session = next;
                    _streamingRequestId = null;
                    _expectedPreceding += ev.Character;
                    _overlay.Update(next.VisibleText);
                    _ = ReanchorAsync(next.RequestId, HostPublishPollMs);
                    PrefetchContinuation(next);
                    return;
                case SuggestionSessionReconciler.Outcome.Advance:
                    // Typed through to the end, so a prefetched continuation may be about to answer.
                    ClearSession();
                    _work.Cancel();
                    break;
                default:
                    CancelSession("typing invalidated the suggestion");
                    CancelPrefetch();
                    break;
            }
        }
        else if (KeepsPrediction(ev))
        {
            _logger.LogDebug("Key kept by the running prediction.");
            return;
        }
        else if (SuggestionAvailability.ClearsSuggestion(ev))
        {
            _work.Cancel();
        }

        if (SuggestionAvailability.SchedulesPrediction(ev))
        {
            SchedulePrediction(ev.Kind == KeyKind.Character ? ev.Character : null);
        }
        else
        {
            _logger.LogDebug("Key {Kind} asks for no suggestion.", ev.Kind);
        }
    }

    private void TrackForHostPublish(KeyboardEvent ev)
    {
        _keySequence++;
        if (ev.HasNonShiftModifier || ev.Kind is not (KeyKind.Character or KeyKind.Backspace))
        {
            // Enter, navigation and shortcuts change the text in ways a count cannot predict.
            SetPublishBaseline(null);
        }
        else if (ev.Kind == KeyKind.Character)
        {
            _expectedDelta++;
        }
        else if (ev.Kind == KeyKind.Backspace)
        {
            _expectedDelta--;
        }
    }

    /// <summary>
    /// Holds Cotabby back while an input method composes text the app has not received yet. A
    /// suggestion would continue text that is about to change, and accepting one would land in
    /// the middle of the composition. Committing the composition asks for a suggestion again.
    /// </summary>
    private bool HoldsForComposition(KeyboardEvent ev)
    {
        bool committed = _ime.Key(ev);
        if (_ime.IsComposing)
        {
            if (_session is not null) CancelSession("the input method is composing");
            _prediction = null;
            _work.Cancel();
            return true;
        }
        if (!committed) return false;
        // The commit replaces the composition in the field, which a key count cannot follow.
        PredictFromFreshRead();
        return true;
    }

    /// <summary>
    /// A typed letter the running generation agrees with keeps it going instead of starting over,
    /// so the suggestion is ready when the user pauses.
    /// </summary>
    private bool KeepsPrediction(KeyboardEvent ev) =>
        ev.Kind == KeyKind.Character && !ev.HasNonShiftModifier
        && _prediction is { } prediction && prediction.Append(ev.Character.ToString(), Now(), _keySequence);

    /// <summary>Keeps screen capture running for a field suggestions may appear in, and stops it everywhere else.</summary>
    private void TrackScreen(FocusedField? field, SuggestionSettings? settings = null)
    {
        if (_screen is not { } screen) return;
        settings ??= _settings;
        if (field is null || !settings.ScreenContext || field.IsSecure || !IsAvailableIn(field))
        {
            screen.Stop();
            return;
        }
        screen.Track(field, settings.UsesEndpoint);
    }

    /// <summary>
    /// A field focused with text already in it gets its first suggestion once the screen has been
    /// read, so that suggestion can use it. Typing in the meantime already asked for one.
    /// </summary>
    private void OnFirstScreenExcerpt(FocusedField field) => Post(() =>
    {
        if (_disposed || _session is not null || _prediction is not null || !_work.IsIdle) return;
        if (FocusIdentity(field) != _focusIdentity) return;
        PredictFromFreshRead();
    });
}
