using System.Runtime.InteropServices;
using Cotabby.Core.Spelling;

namespace Cotabby.Win32.Spelling;

/// <summary>
/// Typo detection through the Windows spell checker, which knows names, learned words and
/// inflections the bundled frequency lists lack. Languages Windows has no checker for fall
/// back to the bundled lexicon.
/// </summary>
public sealed class WindowsSpellChecker(ISpellingLexicon fallback) : ISpellingLexicon
{
    private static readonly Guid FactoryClsid = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private const int S_OK = 0;

    private readonly object _gate = new();
    private readonly Dictionary<SpellingDictionaryLanguage, ISpellChecker?> _checkers = [];

    public bool IsKnownWord(string word, SpellingDictionaryLanguage language)
    {
        lock (_gate)
        {
            if (!_checkers.TryGetValue(language, out var checker))
            {
                checker = Create(LanguageTag(language));
                _checkers[language] = checker;
            }
            if (checker is null) return fallback.IsKnownWord(word, language);

            try
            {
                return checker.Check(word).Next(out _) != S_OK;
            }
            catch (COMException)
            {
                _checkers[language] = null;
                return fallback.IsKnownWord(word, language);
            }
        }
    }

    private static ISpellChecker? Create(string languageTag)
    {
        try
        {
            if (Type.GetTypeFromCLSID(FactoryClsid) is not { } type
                || Activator.CreateInstance(type) is not ISpellCheckerFactory factory
                || !factory.IsSupported(languageTag))
            {
                return null;
            }
            return factory.CreateSpellChecker(languageTag);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return null;
        }
    }

    private static string LanguageTag(SpellingDictionaryLanguage language) => language switch
    {
        SpellingDictionaryLanguage.German => "de-DE",
        SpellingDictionaryLanguage.Spanish => "es-ES",
        SpellingDictionaryLanguage.French => "fr-FR",
        SpellingDictionaryLanguage.Hebrew => "he-IL",
        SpellingDictionaryLanguage.Italian => "it-IT",
        SpellingDictionaryLanguage.Russian => "ru-RU",
        _ => "en-US",
    };

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object SupportedLanguages();

        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);

        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    // Only the members up to Check are declared. The vtable order must match the SDK header.
    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string LanguageTag();

        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig]
        int Next([MarshalAs(UnmanagedType.Interface)] out object? error);
    }
}
