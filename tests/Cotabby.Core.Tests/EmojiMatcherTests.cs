using System.Linq;
using Cotabby.Core.Emoji;
using Xunit;

namespace Cotabby.Core.Tests;

public class EmojiMatcherTests
{
    [Fact]
    public void ExactNameWins()
    {
        var top = EmojiMatcher.Search("smile").First();
        Assert.Equal("smile", top.Name);
    }

    [Fact]
    public void TadaResolves()
    {
        var top = EmojiMatcher.Search("tada").First();
        Assert.Equal("tada", top.Name);
        Assert.Equal("🎉", top.Glyph);
    }

    [Fact]
    public void Plus1Alias()
    {
        var top = EmojiMatcher.Search("+1").First();
        Assert.Equal("+1", top.Name);
        Assert.Equal("👍", top.Glyph);
    }

    [Fact]
    public void ThumbsUpAlias()
    {
        // "thumbsup" alias resolves to +1.
        var top = EmojiMatcher.Search("thumbsup").First();
        Assert.Equal("+1", top.Name);
    }

    [Fact]
    public void HelloAliasResolvesToWave()
    {
        var top = EmojiMatcher.Search("hello").First();
        Assert.Equal("wave", top.Name);
    }

    [Fact]
    public void EmptyReturnsEmpty()
    {
        Assert.Empty(EmojiMatcher.Search(""));
        Assert.Empty(EmojiMatcher.Search("  "));
    }

    [Fact]
    public void NoMatchReturnsEmpty()
    {
        // Sufficiently random nonsense token that shouldn't substring-match
        // any built-in name or alias.
        Assert.Empty(EmojiMatcher.Search("qzqxxqzqxx"));
    }

    [Fact]
    public void MaxLimitsResults()
    {
        var results = EmojiMatcher.Search("e", max: 3);
        Assert.True(results.Count <= 3);
    }
}
