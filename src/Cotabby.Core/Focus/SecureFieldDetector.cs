namespace Cotabby.Core.Focus;

/// <summary>
/// Flags fields whose markers name a secret. The marker set is deliberately broad.
/// A false positive only costs a suggestion while a false negative could show a secret as ghost text.
/// </summary>
public static class SecureFieldDetector
{
    /// <summary>A marker and where in a text it counts.</summary>
    /// <param name="AtWordStart">Only where a word starts, for a word that also sits inside unrelated words.</param>
    /// <param name="NotBefore">Characters that, right after the marker, make it part of another word.</param>
    private readonly record struct Marker(string Text, bool AtWordStart = false, string NotBefore = "")
    {
        public static implicit operator Marker(string text) => new(text);
    }

    private static Marker WordStart(string text) => new(text, AtWordStart: true);

    private static readonly Marker[] SensitiveMarkers =
    [
        "secure", "password", "passcode", "passphrase", "cvv", "cvc", "security code",
        "verification code", "one-time code", "one time code", "social security",
        "card number", "credit card",
        // The same words in the languages Windows is most used in. A label or placeholder is in the
        // app's language, so an English-only list missed most localized sign-in forms. Bare "PIN"
        // is left out: it is inside too many words, and India's postal code is a "PIN code".
        // Words of languages that build compounds, such as "Anmeldepasswort", count anywhere, as
        // the English ones do. The others count only where a word starts, since Portuguese "senha"
        // is inside "resenha" (a review) and "desenhar" (to draw). An ending may follow, as in
        // "senhas" or Turkish "şifrenizi".
        // German
        "passwort", "kennwort", "sicherheitscode", "kartennummer", "kartenprüfnummer",
        // French
        WordStart("mot de passe"), WordStart("code de sécurité"), WordStart("numéro de carte"), WordStart("cryptogramme"),
        // Spanish
        WordStart("contraseña"), WordStart("contrasena"), WordStart("clave de acceso"), WordStart("código de seguridad"),
        WordStart("número de tarjeta"),
        // Portuguese
        WordStart("senha"), WordStart("código de segurança"), WordStart("número do cartão"),
        // Italian
        WordStart("codice di sicurezza"), WordStart("numero della carta"),
        // Dutch
        "wachtwoord", "beveiligingscode", "kaartnummer",
        // Swedish, Danish, Norwegian, Finnish
        "lösenord", "säkerhetskod", "adgangskode", "kodeord", "passord", "salasana",
        // Polish, Czech, Turkish
        WordStart("hasło"), WordStart("heslo"), WordStart("şifre"),
        // Russian and Ukrainian
        "пароль",
        // Greek
        "κωδικός πρόσβασης",
        // Japanese
        "パスワード", "暗証番号", "セキュリティコード",
        // Chinese, simplified and traditional. Followed by "study" it is cryptography, and by
        // "child" a codon.
        new("密码", NotBefore: "学學子"), new("密碼", NotBefore: "学學子"), "验证码", "驗證碼",
        // Korean. Followed by 화 it is encryption or cryptocurrency, and by 학 cryptography.
        "비밀번호", new("암호", NotBefore: "화학"), "인증번호",
        // Arabic, Hebrew, Hindi, Vietnamese, Indonesian, Thai
        "كلمة المرور", "كلمة السر", "סיסמה", "पासवर्ड", WordStart("mật khẩu"), WordStart("kata sandi"), "รหัสผ่าน",
    ];

    public static bool IsSecure(params string?[] markers)
    {
        foreach (var marker in markers)
        {
            if (string.IsNullOrEmpty(marker)) continue;
            foreach (var sensitive in SensitiveMarkers)
            {
                if (Holds(marker, sensitive)) return true;
            }
        }
        return false;
    }

    /// <summary>True when <paramref name="sensitive"/> is in <paramref name="text"/>, ignoring case, where it counts.</summary>
    private static bool Holds(string text, Marker sensitive)
    {
        for (int at = text.IndexOf(sensitive.Text, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(sensitive.Text, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (sensitive.AtWordStart && at > 0 && char.IsLetterOrDigit(text[at - 1])) continue;
            int end = at + sensitive.Text.Length;
            if (end < text.Length && sensitive.NotBefore.Contains(text[end])) continue;
            return true;
        }
        return false;
    }
}
