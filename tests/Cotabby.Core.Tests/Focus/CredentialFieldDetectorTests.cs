using Cotabby.Core.Focus;
using Cotabby.Core.Tests.Suggestions;

namespace Cotabby.Core.Tests.Focus;

public sealed class CredentialFieldDetectorTests
{
    private static FocusedField Field(
        string label = "", string id = "", string text = "", string helpText = "", bool singleLine = true) =>
        Fields.Text(text, singleLine: singleLine) with { Label = label, HelpText = helpText, AutomationId = id };

    [Fact]
    public void IsCredentialField_GoogleSignInField()
    {
        Assert.True(CredentialFieldDetector.IsCredentialField(Field("Email or phone", "identifierId", "realdeepdark")));
        Assert.True(CredentialFieldDetector.IsCredentialField(Field(id: "identifierId")));
    }

    [Theory]
    [InlineData("Username")]
    [InlineData("Enter PIN")]
    [InlineData("Verification code")]
    [InlineData("Phone number")]
    [InlineData("Card number")]
    [InlineData("Log in")]
    [InlineData("Email address")]
    public void IsCredentialField_LabelOrHelpTextNamingACredential(string label)
    {
        Assert.True(CredentialFieldDetector.IsCredentialField(Field(label)));
        Assert.True(CredentialFieldDetector.IsCredentialField(Field(helpText: label)));
    }

    [Theory]
    [InlineData("Email subject")]
    [InlineData("Email preview text")]
    [InlineData("Search email")]
    [InlineData("Message to phone")]
    [InlineData("Spinning")]
    public void IsCredentialField_LabelAboutWritingOrOnlyContainingAKeyword_IsNotBlocked(string label) =>
        Assert.False(CredentialFieldDetector.IsCredentialField(Field(label)));

    [Theory]
    [InlineData("username")]
    [InlineData("email")]
    [InlineData("passwd")]
    [InlineData("otp")]
    [InlineData("login_field")]
    [InlineData("user_login")]
    [InlineData("user_email")]
    [InlineData("loginEmail")]
    [InlineData("txtUserName")]
    [InlineData("phoneNumber")]
    [InlineData("phonenumber")]
    [InlineData("emailaddress")]
    [InlineData("otp-3")]
    [InlineData("OTPCode")]
    [InlineData("one-time-code")]
    public void IdentifierNamesCredential_SignInIds(string id) =>
        Assert.True(CredentialFieldDetector.IdentifierNamesCredential(id));

    [Theory]
    [InlineData("phonebook-search")]
    [InlineData("emailSearch")]
    [InlineData("email-subject")]
    [InlineData("userInput")]
    [InlineData("shipping")]
    [InlineData("mailbox")]
    [InlineData("identifier")]
    [InlineData("q")]
    [InlineData("")]
    public void IdentifierNamesCredential_IdsThatOnlyContainACredentialWord_AreNotBlocked(string id)
    {
        Assert.False(CredentialFieldDetector.IdentifierNamesCredential(id));
        Assert.False(CredentialFieldDetector.IsCredentialField(Field("Search", id, "hello")));
    }

    [Fact]
    public void IdentifierNamesCredential_IdsLongerThanSixtyFourCharacters_AreNotBlocked()
    {
        Assert.True(CredentialFieldDetector.IdentifierNamesCredential(new string('1', 59) + "email"));
        Assert.False(CredentialFieldDetector.IdentifierNamesCredential(new string('1', 60) + "email"));
    }

    [Theory]
    [InlineData("senad@imperum.io")]
    [InlineData("alice@")]
    [InlineData("alice@gm")]
    [InlineData("alice@gmail.com")]
    public void IsCredentialField_AddressBeingTyped_IsBlockedWithoutATellingLabelOrId(string text) =>
        Assert.True(CredentialFieldDetector.IsCredentialField(Field("Account", "identifier", text)));

    [Theory]
    [InlineData("mail senad@imperum.io today")]
    [InlineData("@alice")]
    [InlineData("alice")]
    [InlineData("medium.com/@alice")]
    [InlineData("ping alice@")]
    [InlineData("a@b@c")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeAddressEntry_FalseUnlessTheWholeValueIsOneAddress(string? text) =>
        Assert.False(CredentialFieldDetector.LooksLikeAddressEntry(text));

    [Fact]
    public void IsCredentialField_OrdinaryField_IsNotBlocked() =>
        Assert.False(CredentialFieldDetector.IsCredentialField(
            Field("Shipping notes", "q", "hello there", helpText: "Type a message")));

    [Fact]
    public void IsCredentialField_MultiLineField_IsNeverBlocked() =>
        Assert.False(CredentialFieldDetector.IsCredentialField(Field("Email body", "email", "a@b.co", singleLine: false)));
}
