using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Focus;

public sealed class SecureFieldDetectorTests
{
    [Fact]
    public void IsSecure_PlainTextField_IsFalse() =>
        Assert.False(SecureFieldDetector.IsSecure("Edit", null, "text field", "Email", null));

    [Fact]
    public void IsSecure_UnrelatedSearchField_IsFalse() =>
        Assert.False(SecureFieldDetector.IsSecure("Edit", null, "text field", "Search", "Type to search"));

    [Fact]
    public void IsSecure_NullAndEmptyMarkers_AreIgnored() =>
        Assert.False(SecureFieldDetector.IsSecure("", null, "", null, ""));

    [Theory]
    [InlineData("secure text field")]
    [InlineData("AXSecureTextField")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    public void IsSecure_MatchesAMarkerInsideAnyTextIgnoringCase(string marker) =>
        Assert.True(SecureFieldDetector.IsSecure("Edit", null, marker));

    [Theory]
    [InlineData("secure")]
    [InlineData("password")]
    [InlineData("passcode")]
    [InlineData("passphrase")]
    [InlineData("cvv")]
    [InlineData("cvc")]
    [InlineData("security code")]
    [InlineData("verification code")]
    [InlineData("one-time code")]
    [InlineData("one time code")]
    [InlineData("social security")]
    [InlineData("card number")]
    [InlineData("credit card")]
    public void IsSecure_EverySensitiveMarkerTripsInAnyPosition(string sensitive)
    {
        for (int position = 0; position < 3; position++)
        {
            var markers = new string?[3];
            markers[position] = $"Enter {sensitive} here";

            Assert.True(SecureFieldDetector.IsSecure(markers), $"{sensitive} at {position}");
        }
    }
}
