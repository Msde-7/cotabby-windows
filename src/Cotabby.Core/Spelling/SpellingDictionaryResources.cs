using System.Text;

namespace Cotabby.Core.Spelling;

/// <summary>Locates the frequency dictionaries that ship next to the assembly.</summary>
public static class SpellingDictionaryResources
{
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Resources", "Spelling");

    /// <summary>
    /// A loader that reads a language's dictionary from <paramref name="directory"/>, or from
    /// <see cref="DefaultDirectory"/> when none is given. It returns null for a missing or unreadable file.
    /// </summary>
    public static Func<SpellingDictionaryLanguage, string?> Loader(string? directory = null)
    {
        directory ??= DefaultDirectory;
        return language =>
        {
            try
            {
                return File.ReadAllText(Path.Combine(directory, language.ResourceName() + ".txt"), Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        };
    }
}
