using System.Text;
using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>
/// Tokens need not align with words or Unicode scalars, and replay must stay invisible.
/// </summary>
public class TokenHealingBufferTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static TokenHealingBuffer Buffer(string replayedPrefix) => new(Utf8(replayedPrefix));

    [Fact]
    public void EmptyReplayStreamsCumulativeText()
    {
        var buffer = Buffer("");
        Assert.True(buffer.ReplayComplete);
        Assert.Equal("Hello", buffer.Append(Utf8("Hello")));
        Assert.Equal("Hello world", buffer.Append(Utf8(" world")));
        Assert.Equal("Hello world", buffer.Text);
    }

    [Fact]
    public void LargerVocabularyTokenPublishesOnlyItsUnwrittenSuffix()
    {
        var buffer = Buffer("sched");
        Assert.Equal("ule", buffer.Append(Utf8("schedule")));
        Assert.True(buffer.ReplayComplete);
        Assert.Equal("ule", buffer.Text);
    }

    [Fact]
    public void ReplayCanSpanSeveralTokens()
    {
        var buffer = Buffer("sched");
        Assert.Null(buffer.Append(Utf8("sc")));
        Assert.False(buffer.ReplayComplete);
        Assert.Equal("hed", Encoding.UTF8.GetString(buffer.RemainingReplay));
        Assert.Null(buffer.Append(Utf8("h")));
        Assert.Equal("", buffer.Text);
        Assert.Equal("ule", buffer.Append(Utf8("edule")));
        Assert.True(buffer.ReplayComplete);
    }

    [Fact]
    public void ExactReplayPublishesNothingUntilNewTextArrives()
    {
        var buffer = Buffer("sched");
        Assert.Null(buffer.Append(Utf8("sched")));
        Assert.True(buffer.ReplayComplete);
        Assert.False(buffer.HasVisibleBytes);
        Assert.Equal("", buffer.Text);
        Assert.Equal("ule", buffer.Append(Utf8("ule")));
    }

    [Fact]
    public void TrailingSpaceReplayPreservesOnlyNewWhitespace()
    {
        var buffer = Buffer(" ");
        Assert.Equal("word\n\t", buffer.Append(Utf8(" word\n\t")));
        Assert.Equal("word\n\t", buffer.Text);
    }

    [Fact]
    public void MismatchPermanentlyFailsClosed()
    {
        var buffer = Buffer("sched");
        Assert.Null(buffer.Append(Utf8("sc")));
        Assert.Null(buffer.Append(Utf8("xanything")));
        Assert.True(buffer.HasReplayMismatch);
        Assert.False(buffer.ReplayComplete);
        Assert.Equal("", buffer.Text);
        Assert.Null(buffer.Append(Utf8("hedule")));
        Assert.Null(buffer.Append(Utf8(" new text")));
        Assert.Equal("", buffer.Text);
    }

    [Fact]
    public void CanonicallyEquivalentTextDoesNotBypassExactByteReplay()
    {
        // The editor holds the composed bytes, so decomposed bytes are a mismatch.
        var buffer = Buffer("é");
        Assert.Null(buffer.Append(Utf8("e\u0301clair")));
        Assert.True(buffer.HasReplayMismatch);
    }

    [Fact]
    public void ReplayTokenMayContainOnlyTheLastByteOfAUnicodeScalar()
    {
        var buffer = new TokenHealingBuffer([0xA9]);
        Assert.Equal("clair", buffer.Append([0xA9, .. Utf8("clair")]));
        Assert.True(buffer.ReplayComplete);
    }

    [Fact]
    public void SplitUtf8AfterReplayIsRetainedUntilComplete()
    {
        var buffer = Buffer("a");
        Assert.False(buffer.HasVisibleBytes);
        Assert.Null(buffer.Append([0x61, 0xF0, 0x9F]));
        Assert.True(buffer.ReplayComplete);
        // The token contains new output even before it can be displayed.
        Assert.True(buffer.HasVisibleBytes);
        Assert.Equal("", buffer.Text);
        Assert.Equal("😀", buffer.Append([0x98, 0x80]));
        Assert.DoesNotContain('\uFFFD', buffer.Text);
    }

    [Fact]
    public void SplitScalarKeepsLastCompleteVisibleTextUntilItFinishes()
    {
        var buffer = Buffer("");
        Assert.Equal("Hi ", buffer.Append(Utf8("Hi ")));
        Assert.Null(buffer.Append([0xF0, 0x9F]));
        Assert.Equal("Hi ", buffer.Text);
        Assert.Equal("Hi 😀", buffer.Append([0x98, 0x80]));
    }

    [Fact]
    public void InvalidUtf8NeverPublishesReplacementCharacters()
    {
        var buffer = Buffer("");
        Assert.Null(buffer.Append([0xFF]));
        Assert.Null(buffer.Append(Utf8("text")));
        Assert.Equal("", buffer.Text);
    }

    [Fact]
    public void EmptyTokenDoesNotCreateAVisibleUpdate()
    {
        var buffer = Buffer("");
        Assert.Null(buffer.Append([]));
        Assert.Equal("word", buffer.Append(Utf8("word")));
        Assert.Null(buffer.Append([]));
        Assert.Equal("word", buffer.Text);
    }

    [Fact]
    public void MaximumCandidateFitsTheReplayAllowanceEvenWithByteFallback()
    {
        Assert.Equal(16, TokenHealingBuffer.MaximumReplayTokens);
        Assert.True(TokenHealingBuffer.MaximumHealedTokenBytes <= TokenHealingBuffer.MaximumReplayTokens);
        byte[] prefix = Enumerable.Repeat((byte)0x61, TokenHealingBuffer.MaximumHealedTokenBytes).ToArray();
        var buffer = new TokenHealingBuffer(prefix);
        foreach (byte value in prefix)
        {
            Assert.Null(buffer.Append([value]));
        }
        Assert.True(buffer.ReplayComplete);
        Assert.Equal("fter", buffer.Append(Utf8("fter")));
    }
}
