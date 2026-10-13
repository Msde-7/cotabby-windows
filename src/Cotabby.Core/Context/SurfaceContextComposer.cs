using Cotabby.Core.Text;
using System.Text;

namespace Cotabby.Core.Context;

/// <summary>Sanitized facts about the writing surface. Windows exposes no page address, so there is no domain.</summary>
public sealed record SurfaceContext(
    AppSurfaceClass SurfaceClass,
    string ApplicationName,
    string? WindowTitle,
    string? FieldPlaceholder);

/// <summary>
/// Turns focus metadata into bounded surface facts and renders them as one compact prompt line.
/// </summary>
/// <remarks>
/// Omission beats noise. Code editors and terminals get no surface section, because app metadata
/// biases a small base model toward code. An unrecognized app with nothing else to say is omitted too.
/// </remarks>
public static class SurfaceContextComposer
{
    private const int MaxTitleLength = 80;
    private const int MaxPlaceholderLength = 60;

    private static readonly string[] TitleSeparators = [" - ", " — ", " – "];

    public static SurfaceContext? Compose(
        AppSurfaceClass surfaceClass, string applicationName, string? windowTitle, string? fieldPlaceholder)
    {
        if (surfaceClass is AppSurfaceClass.CodeEditor or AppSurfaceClass.Terminal) return null;

        string cleanedApplicationName = CollapseWhitespace(applicationName);
        if (cleanedApplicationName.Length == 0) return null;
        string? title = SanitizedTitle(windowTitle, cleanedApplicationName);
        string? placeholder = NonEmptyCleaned(fieldPlaceholder) is { } cleaned
            ? new GraphemeString(cleaned).Prefix(MaxPlaceholderLength)
            : null;

        // A generic app with no title or placeholder has nothing useful to say.
        if (surfaceClass == AppSurfaceClass.Other && title is null && placeholder is null) return null;

        return new SurfaceContext(surfaceClass, cleanedApplicationName, title, placeholder);
    }

    /// <summary>One preface line in stable order. Format, app, title, then field.</summary>
    public static IReadOnlyList<string> PrefaceLines(SurfaceContext surface)
    {
        if (Format(surface.SurfaceClass) is not { } format) return [];
        var fields = new List<string> { $"Format: {format}", $"App: {surface.ApplicationName}" };
        if (surface.WindowTitle is { } title) fields.Add($"Title: {title}");
        if (surface.FieldPlaceholder is { } placeholder) fields.Add($"Field: {placeholder}");
        return [string.Join("; ", fields) + "."];
    }

    /// <summary>
    /// Strips the app name suffix many apps append (<c>Inbox - Thunderbird</c>), collapses
    /// whitespace and caps the length.
    /// </summary>
    public static string? SanitizedTitle(string? rawTitle, string applicationName)
    {
        if (NonEmptyCleaned(rawTitle) is not { } title) return null;
        foreach (string separator in TitleSeparators)
        {
            string suffix = separator + applicationName;
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length];
                break;
            }
        }
        title = ScalarSets.TrimWhitespaceAndNewlines(title);
        return title.Length == 0 ? null : new GraphemeString(title).Prefix(MaxTitleLength);
    }

    private static string? Format(AppSurfaceClass surfaceClass) => surfaceClass switch
    {
        AppSurfaceClass.Email => "email",
        AppSurfaceClass.Chat => "chat",
        AppSurfaceClass.Browser => "web text",
        AppSurfaceClass.Other => "text",
        _ => null,
    };

    /// <summary>Drops control characters and double quotes, then collapses whitespace.</summary>
    private static string? NonEmptyCleaned(string? text)
    {
        if (text is null) return null;
        var kept = new StringBuilder(text.Length);
        foreach (var scalar in text.EnumerateRunes())
        {
            if (!ScalarSets.IsControl(scalar) && scalar.Value != '"') kept.Append(scalar.ToString());
        }
        string cleaned = CollapseWhitespace(kept.ToString());
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string CollapseWhitespace(string text) => string.Join(' ', GraphemeString.Words(text));
}
