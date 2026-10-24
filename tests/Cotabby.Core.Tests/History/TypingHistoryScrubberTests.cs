using Cotabby.Core.History;

namespace Cotabby.Core.Tests.History;

public sealed class TypingHistoryScrubberTests
{
    [Fact]
    public void ProseAndShortNumbersPassThrough()
    {
        const string text = "Hi Arnaud, the POC starts on 12 October at 10:30. Kind regards, Senad";
        Assert.Equal(text, TypingHistoryScrubber.Scrub(text));
    }

    [Fact]
    public void LongAllLetterWordsAreKept()
    {
        const string turkish = "Çekoslovakyalılaştıramadıklarımızdanmışsınız diye yazdım.";
        Assert.Equal(turkish, TypingHistoryScrubber.Scrub(turkish));
    }

    [Fact]
    public void CredentialsAndTokensAreRedacted()
    {
        string text = "key sk-ant-api03-abcdefghijklmnop1234 and ghp_abcdefghijklmnopqrstuvwxyz123456 "
            + "plus token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc123";
        string scrubbed = TypingHistoryScrubber.Scrub(text);

        Assert.DoesNotContain("sk-ant", scrubbed);
        Assert.DoesNotContain("ghp_", scrubbed);
        Assert.DoesNotContain("eyJhbGci", scrubbed);
        Assert.StartsWith("key [redacted] and [redacted]", scrubbed);
    }

    [Fact]
    public void CardNumbersAreRedactedButPhoneNumbersAndDatesStay()
    {
        Assert.Equal(
            "card [redacted] exp 12/27, or [redacted]",
            TypingHistoryScrubber.Scrub("card 4111 1111 1111 1111 exp 12/27, or 5500-0000-0000-0004"));
        const string prose = "Call me on +1 415 555 0100 before 2026-10-04 17:30, order 123456789012.";
        Assert.Equal(prose, TypingHistoryScrubber.Scrub(prose));
    }

    [Fact]
    public void PrivateKeyBlocksAreRedacted()
    {
        const string text =
            "here:\n-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----\nthanks";
        Assert.Equal("here:\n[redacted]\nthanks", TypingHistoryScrubber.Scrub(text));
    }

    [Fact]
    public void ACredentialSplitByTheCaretIsStillRedacted()
    {
        // Each half alone is too short to look like a key. Scrubbed apart and joined again, the
        // whole key would be stored.
        var scrubbed = TypingHistoryScrubber.Scrub(before: "my key is sk-abcdefgh", after: "ijklmnop12345678 thanks");

        Assert.Equal("my key is [redacted] thanks", scrubbed.Text);
        // A secret around the caret counts as typed.
        Assert.Equal("my key is [redacted]".Length, scrubbed.TypedLength);
    }

    [Fact]
    public void TheCaretBoundarySurvivesRedactionBeforeIt()
    {
        var scrubbed = TypingHistoryScrubber.Scrub(
            before: "token ghp_abcdefghijklmnopqrstuvwxyz123456 then ", after: "the rest");

        Assert.Equal("token [redacted] then the rest", scrubbed.Text);
        Assert.Equal("token [redacted] then ".Length, scrubbed.TypedLength);
    }

    [Fact]
    public void AKeptLongWordAroundTheCaretKeepsTheBoundaryInPlace()
    {
        var scrubbed = TypingHistoryScrubber.Scrub(before: "Donaudampfschifffahrts", after: "gesellschaft fährt");

        Assert.Equal("Donaudampfschifffahrtsgesellschaft fährt", scrubbed.Text);
        Assert.Equal("Donaudampfschifffahrts".Length, scrubbed.TypedLength);
    }

    [Fact]
    public void LongRecordsKeepTheirTail()
    {
        string text = string.Concat(Enumerable.Repeat("word ", 5_000)) + "the end";
        string scrubbed = TypingHistoryScrubber.Scrub(text);

        Assert.Equal(TypingHistoryScrubber.MaximumRecordCharacters, scrubbed.Length);
        Assert.EndsWith("the end", scrubbed);
    }
}
