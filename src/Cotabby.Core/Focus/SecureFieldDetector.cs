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
        // The same words in the languages Windows is most used in. A label or placeholder is in the
        // app's language, so an English-only list missed most localized sign-in forms. Bare "PIN"
        // is left out: it is inside too many words, and India's postal code is a "PIN code".
        // German
        "passwort", "kennwort", "sicherheitscode", "kartennummer", "kartenprüfnummer",
        // French
        "mot de passe", "code de sécurité", "numéro de carte", "cryptogramme",
        // Spanish
        "contraseña", "contrasena", "clave de acceso", "código de seguridad", "número de tarjeta",
        // Portuguese
        "senha", "código de segurança", "número do cartão",
        // Italian
        "codice di sicurezza", "numero della carta",
        // Dutch
        "wachtwoord", "beveiligingscode", "kaartnummer",
        // Swedish, Danish, Norwegian, Finnish
        "lösenord", "säkerhetskod", "adgangskode", "kodeord", "passord", "salasana",
        // Polish, Czech, Turkish
        "hasło", "heslo", "şifre",
        // Russian and Ukrainian
        "пароль",
        // Greek
        "κωδικός πρόσβασης",
        // Japanese
        "パスワード", "暗証番号", "セキュリティコード",
        // Chinese, simplified and traditional
        "密码", "密碼", "验证码", "驗證碼",
        // Korean
        "비밀번호", "암호", "인증번호",
        // Arabic, Hebrew, Hindi, Vietnamese, Indonesian, Thai
        "كلمة المرور", "كلمة السر", "סיסמה", "पासवर्ड", "mật khẩu", "kata sandi", "รหัสผ่าน",
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
