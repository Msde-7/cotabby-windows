using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Hosting;
using Cotabby.Core.Suggestions;

namespace Cotabby.App.UI.Controls;

/// <summary>
/// Edits the languages suggestions are written in. Chosen languages are removable chips,
/// with an add box and one click suggestions below. The last language cannot be removed.
/// </summary>
internal sealed class LanguageEditor : StackPanel
{
    private readonly AppHost _host;
    private readonly WrapPanel _chosen = new();
    private readonly WrapPanel _suggestions = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly FrameworkElement _addBox;

    public LanguageEditor(AppHost host)
    {
        _host = host;
        _addBox = Ui.AddBox("Add a language, e.g. German", Add);
        Children.Add(_chosen);
        Children.Add(_addBox);
        Children.Add(_suggestions);
        Refresh();
    }

    public void Refresh()
    {
        var languages = _host.Settings.Languages;
        bool atLimit = languages.Count >= LanguageCatalog.MaxLanguages;

        _chosen.Children.Clear();
        foreach (var language in languages)
        {
            _chosen.Children.Add(Chips.Removable(language, languages.Count > 1 ? () => Remove(language) : null));
        }

        _addBox.IsEnabled = !atLimit;
        _suggestions.Children.Clear();
        if (atLimit) return;
        foreach (var option in LanguageCatalog.CommonLanguages)
        {
            if (languages.Contains(option.Name, StringComparer.OrdinalIgnoreCase)) continue;
            _suggestions.Children.Add(Chips.Addable(option.NativeLabel, () => Add(option.Name)));
        }
    }

    private void Add(string language) => Save([.. _host.Settings.Languages, language]);

    private void Remove(string language) => Save(_host.Settings.Languages.Where(l => l != language));

    private void Save(IEnumerable<string> languages)
    {
        _host.Settings.Languages = LanguageCatalog.Normalize(languages);
        _host.PersistSettings();
        Refresh();
    }
}
