namespace Cotabby.Core.Focus;

/// <summary>
/// Flags fields whose markers name a secret. The marker set is deliberately broad.
/// A false positive only costs a suggestion while a false negative could show a secret as ghost text.
/// </summary>
public static class SecureFieldDetector
{
    private static readonly string[] SensitiveMarkers =
    [
        "secure", "password", "passcode", "passphrase", "cvv", "cvc", "security code",
        "verification code", "one-time code", "one time code", "social security",
        "card number", "credit card",
    ];

    public static bool IsSecure(params string?[] markers)
    {
        foreach (var marker in markers)
        {
            if (string.IsNullOrEmpty(marker)) continue;
            foreach (var sensitive in SensitiveMarkers)
            {
                if (marker.Contains(sensitive, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
