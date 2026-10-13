using System.Windows;
using Cotabby.App.Settings;

namespace Cotabby.App.UI.Settings;

/// <summary>
/// One row of a settings card. Search matches on the title, subtitle and keywords,
/// so a row without a title is decoration and never shows up in results.
/// </summary>
internal sealed record SettingsRow(string Title, string Subtitle, FrameworkElement View)
{
    public string Keywords { get; init; } = "";
    public Func<AppSettings, bool>? Visible { get; init; }
    public Func<AppSettings, bool>? Enabled { get; init; }

    public SettingsRow Tagged(string keywords) => this with { Keywords = keywords };

    public SettingsRow VisibleWhen(Func<AppSettings, bool> visible) => this with { Visible = visible };

    public SettingsRow EnabledWhen(Func<AppSettings, bool> enabled) => this with { Enabled = enabled };

    public bool Matches(string query) =>
        Title.Length > 0
        && (Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Keywords.Contains(query, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A bordered group of rows under an optional header.</summary>
internal sealed record SettingsCard(string? Header, params SettingsRow[] Rows)
{
    public Func<AppSettings, bool>? Visible { get; init; }

    public SettingsCard VisibleWhen(Func<AppSettings, bool> visible) => this with { Visible = visible };
}

/// <summary>One sidebar entry and the cards it shows. The glyph is a Segoe Fluent Icons code point.</summary>
internal sealed record SettingsPane(string Title, string Glyph, string Summary, params SettingsCard[] Cards);
