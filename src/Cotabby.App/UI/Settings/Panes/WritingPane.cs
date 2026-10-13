using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;

namespace Cotabby.App.UI.Settings.Panes;

internal static class WritingPane
{
    /// <summary>The Length option that turns on the custom word range. No preset uses this id.</summary>
    private const string CustomLength = "";

    /// <summary>The same cap the macOS app puts on extended context.</summary>
    private const int MaxExtendedContextLength = 1200;

    private static readonly (string Value, string Label)[] Lengths =
    [
        .. WordCountPreset.All.Select(preset => (preset.Id, $"{preset.Title} · {preset.Label}")),
        (CustomLength, "Custom range"),
    ];

    public static SettingsPane Build(Rows r)
    {
        var context = r.Context;
        var languages = new LanguageEditor(context.Host);
        context.OnRefresh(languages.Refresh);

        return new SettingsPane("Writing", "\uE70F", "Length, profile, and corrections",
            new SettingsCard("Length",
                r.Radios("Length",
                    "How many words Cotabby aims for per suggestion. Shorter is snappier; "
                    + "longer covers more thoughts but takes longer to generate.",
                    Lengths,
                    s => s.UseCustomWordRange ? CustomLength : s.CompletionLengthPreset,
                    (s, v) =>
                    {
                        s.UseCustomWordRange = v == CustomLength;
                        if (!s.UseCustomWordRange) s.CompletionLengthPreset = v;
                    })
                    .Tagged("words short long count verbose brief size concise min max range custom"),
                r.Number("Minimum", "The fewest words in a suggestion.",
                    WordCountPreset.MinimumWords, WordCountPreset.MaximumWords,
                    s => s.CustomLowWords, (s, v) => SetCustomRange(s, v, s.CustomHighWords))
                    .VisibleWhen(s => s.UseCustomWordRange),
                r.Number("Maximum", "The most words in a suggestion.",
                    WordCountPreset.MinimumWords, WordCountPreset.MaximumWords,
                    s => s.CustomHighWords, (s, v) => SetCustomRange(s, Math.Min(s.CustomLowWords, v), v))
                    .VisibleWhen(s => s.UseCustomWordRange),
                r.Toggle("Allow Multi-line Suggestions",
                    "Allow continuations that span more than one line. Off keeps suggestions to a single line.",
                    s => s.AllowMultiLine, (s, v) => s.AllowMultiLine = v)
                    .Tagged("multiline newline wrap paragraph line break"),
                r.Toggle("Suggest Within Words",
                    "Offer a suggestion while a word is still being typed. Off waits until the word is finished.",
                    s => s.SuggestWithinWords, (s, v) => s.SuggestWithinWords = v)
                    .Tagged("mid word partial unfinished typing boundary")),
            new SettingsCard("Acceptance",
                r.Segmented("Acceptance Mode",
                    "What the Accept Word key takes per press. Word inserts one word at a time; "
                    + "Phrase inserts up to the next sentence break.",
                    [(AcceptanceGranularity.Word, "Word"), (AcceptanceGranularity.Phrase, "Phrase")],
                    s => s.AcceptanceMode, (s, v) => s.AcceptanceMode = v)
                    .Tagged("tap behavior how to accept"),
                r.Toggle("Accept Punctuation With Word",
                    "When you accept a word, also accept the punctuation that follows it "
                    + "(commas, periods) so you don't have to type it.",
                    s => s.AcceptPunctuationWithWord, (s, v) => s.AcceptPunctuationWithWord = v)
                    .Tagged("comma period trailing"),
                r.Toggle("Add Space After Accepting",
                    "When accepting a suggestion finishes a word, also add a space so you can keep typing. "
                    + "Skipped when it already ends in punctuation or a space.",
                    s => s.AddSpaceAfterAccept, (s, v) => s.AddSpaceAfterAccept = v)
                    .Tagged("spacebar trailing whitespace gap separator")),
            new SettingsCard("Profile",
                r.Text("Name", "Your name and languages are passed to the AI to help personalize your completions.",
                    "What should Cotabby call you?", s => s.UserName, (s, v) => s.UserName = v.Trim())
                    .Tagged("persona profile you user first name identity"),
                r.Stacked("Languages",
                    "Languages suggestions should be written in. Support depends on your selected model, "
                    + "so some languages may not work.",
                    languages)
                    .Tagged("locale translate multilingual english spanish french german japanese chinese bilingual")),
            new SettingsCard("Corrections",
                r.Toggle("Hide Suggestions on Typo",
                    "Pauses normal completions when a finished word looks misspelled.",
                    s => s.SuppressCompletionsOnTypo, (s, v) => s.SuppressCompletionsOnTypo = v)
                    .Tagged("misspell spelling suppress error mistake"),
                r.Toggle("Offer Corrections on Typo",
                    "After you finish a word, shows a green replacement you can apply with your accept key.",
                    s => s.OfferTypoCorrections, (s, v) => s.OfferTypoCorrections = v)
                    .Tagged("correct fix spelling autocorrect spell check mistake rewrite")
                    .EnabledWhen(s => s.SuppressCompletionsOnTypo),
                r.Toggle("Automatically Fix Typos",
                    "After you press Space, replaces a misspelled word without requiring your accept key.",
                    s => s.AutoFixTypos, (s, v) => s.AutoFixTypos = v)
                    .Tagged("automatic autocorrect spelling replace instant")
                    .EnabledWhen(s => s.SuppressCompletionsOnTypo && s.OfferTypoCorrections),
                r.Stacked("Spelling Dictionaries",
                    "Choose which bundled dictionaries Cotabby may use for corrections. "
                    + "With several enabled, Cotabby selects one from the surrounding text.",
                    DictionaryChips(context))
                    .Tagged("dictionary language multilingual english german spanish french hebrew italian russian")),
            new SettingsCard("Context",
                r.Toggle("Include Clipboard Context",
                    "Let suggestions reference whatever you most recently copied.",
                    s => s.ClipboardContext, (s, v) => s.ClipboardContext = v)
                    .Tagged("paste copy"),
                r.Toggle("Include App Context",
                    "Include the app and window name in suggestions. With an endpoint selected, "
                    + "this context is sent to that server.",
                    s => s.IncludeAppContext, (s, v) => s.IncludeAppContext = v)
                    .Tagged("application window title surface"),
                r.Stacked("Extended Context",
                    "Notes about you that the model keeps in mind, such as a glossary, names or style notes. "
                    + "They are sent with every suggestion, so long notes slow completions down.",
                    ExtendedContext(r))
                    .Tagged("glossary reference notes jargon instructions memory background vocabulary")));
    }

    private static void SetCustomRange(AppSettings settings, int low, int high) =>
        (settings.CustomLowWords, settings.CustomHighWords) = WordCountPreset.Clamp(low, high);

    private static WrapPanel DictionaryChips(SettingsContext context)
    {
        var panel = new WrapPanel();
        context.OnRefresh(() =>
        {
            panel.Children.Clear();
            var enabled = context.Settings.SpellingDictionaries;
            foreach (var language in SpellingDictionaryCatalog.AllLanguages)
            {
                string code = language.Code();
                string label = language.DisplayName();
                panel.Children.Add(Chips.Checkable(label, enabled.Contains(code), () => context.Change(s =>
                {
                    // Corrections need a dictionary, so the last one stays on.
                    if (!s.SpellingDictionaries.Remove(code)) s.SpellingDictionaries.Add(code);
                    else if (s.SpellingDictionaries.Count == 0) s.SpellingDictionaries.Add(code);
                })));
            }
        });
        return panel;
    }

    private static StackPanel ExtendedContext(Rows r)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 120,
            MaxLength = MaxExtendedContextLength,
        }.Styled("Input");
        var count = Ui.Text("", "Caption");
        box.TextChanged += (_, _) => count.Text = $"{box.Text.Length} / {MaxExtendedContextLength} characters";
        r.Bind(box, s => s.ExtendedContext, (s, v) => s.ExtendedContext = v);

        var clear = Ui.Button("Clear", () => r.Context.Change(s => s.ExtendedContext = ""));
        DockPanel.SetDock(clear, Dock.Right);
        count.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel
        {
            Children = { box, new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { clear, count } } },
        };
    }
}
