using System.Text.RegularExpressions;

namespace Cotabby.Core.Focus;

/// <summary>
/// Recognizes sign-in and verification fields (email, username, phone, one-time
/// codes, card numbers) where a completion would only guess at who the user is.
/// Applies to single-line fields only. A multi-line field labelled "email" is an
/// email body, which is where completions belong. Every signal is matched by whole
/// words or whole id parts so "Email subject" and "phonebook-search" stay ordinary fields.
/// </summary>
public static partial class CredentialFieldDetector
{
    public const string BlockedReason = "Sign-in and verification fields are left alone.";

    private static readonly string[] LabelKeywords =
    [
        "email", "e-mail", "username", "user name", "user id", "userid", "login", "log in", "sign in",
        "phone", "mobile number", "password", "passcode", "pin", "one-time", "one time code",
        "verification code", "security code", "otp", "2fa", "two-factor", "authentication code",
        "card number", "cvc", "cvv", "expiry", "expiration",
    ];

    private static readonly string[] ProseLabelWords =
    [
        "subject", "message", "body", "preview", "title", "signature", "template",
        "comment", "note", "notes", "reply", "search",
    ];

    private static readonly string[] CredentialIdentifierWords =
    [
        "email", "mail", "username", "userid", "login", "logon", "signin", "passwd", "password", "pwd",
        "passcode", "otp", "totp", "mfa", "2fa", "phone", "telephone", "tel", "mobile", "msisdn", "pin",
        "cvc", "cvv", "cardnumber", "identifierid", "onetimecode", "verificationcode", "securitycode", "authcode",
    ];

    private static readonly string[] QualifierIdentifierWords =
    [
        "user", "account", "address", "addr", "id", "identifier", "name", "number", "num", "no", "code",
        "field", "input", "txt", "tb", "verification", "verify", "security", "auth", "onetime", "confirm",
        "current", "new", "primary", "your", "enter",
    ];

    /// <summary>Longer ids are generated names and not the name of a sign-in field.</summary>
    private const int MaximumIdentifierLength = 64;

    public static bool IsCredentialField(FocusedField field) =>
        field.IsSingleLine
        && (LabelNamesCredential(field.Label)
            || LabelNamesCredential(field.HelpText)
            || IdentifierNamesCredential(field.AutomationId)
            || LooksLikeAddressEntry(field.Text));

    /// <summary>True when the label holds a credential keyword and no word that makes it describe prose.</summary>
    public static bool LabelNamesCredential(string? label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        var lower = label.ToLowerInvariant();
        return LabelKeywords.Any(k => ContainsWord(k, lower))
            && !ProseLabelWords.Any(w => ContainsWord(w, lower));
    }

    /// <summary>
    /// True when the whole id splits into known id parts and at least one names a
    /// credential. Case, separators and digits are ignored.
    /// </summary>
    public static bool IdentifierNamesCredential(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return false;
        var text = new string(identifier.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (text.Length == 0 || text.Length > MaximumIdentifierLength) return false;

        // reach[i] is null when no split of the first i characters exists, false when one
        // exists, and true when one exists that includes a credential word.
        var reach = new bool?[text.Length + 1];
        reach[0] = false;
        for (int start = 0; start < text.Length; start++)
        {
            if (reach[start] is not { } namesCredential) continue;
            if (char.IsDigit(text[start]))
            {
                reach[start + 1] = (reach[start + 1] ?? false) || namesCredential;
            }
            Extend(reach, text, start, CredentialIdentifierWords, true);
            Extend(reach, text, start, QualifierIdentifierWords, namesCredential);
        }
        return reach[text.Length] == true;
    }

    private static void Extend(bool?[] reach, string text, int start, string[] words, bool namesCredential)
    {
        foreach (var word in words)
        {
            if (string.CompareOrdinal(text, start, word, 0, word.Length) != 0) continue;
            int end = start + word.Length;
            reach[end] = (reach[end] ?? false) || namesCredential;
        }
    }

    /// <summary>
    /// True when the whole value is one address shaped token, finished or still being
    /// typed. A slash or a colon before the at sign marks a URL instead.
    /// </summary>
    public static bool LooksLikeAddressEntry(string? text)
    {
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed) && AddressEntry().IsMatch(trimmed);
    }

    [GeneratedRegex(@"^[^\s@/:]+@[^\s@/:]*$")]
    private static partial Regex AddressEntry();

    private static bool ContainsWord(string keyword, string label)
    {
        int from = 0;
        while (true)
        {
            int at = label.IndexOf(keyword, from, StringComparison.Ordinal);
            if (at < 0) return false;
            int end = at + keyword.Length;
            bool boundaryBefore = at == 0 || !char.IsLetterOrDigit(label[at - 1]);
            bool boundaryAfter = end == label.Length || !char.IsLetterOrDigit(label[end]);
            if (boundaryBefore && boundaryAfter) return true;
            from = end;
        }
    }
}
