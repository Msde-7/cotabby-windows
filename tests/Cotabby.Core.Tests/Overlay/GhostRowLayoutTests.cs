using Cotabby.Core.Focus;
using Cotabby.Core.Overlay;

namespace Cotabby.Core.Tests.Overlay;

public sealed class GhostRowLayoutTests
{
    private const string Long = " alpha bravo charlie delta echo foxtrot golf hotel";

    // Every character is 10 wide.
    private static double Width(string text) => text.Length * 10;

    private static GhostRowLayout.Result? Make(
        string text, double firstRow = 300, double? band = 300, double keycap = 0, bool rightToLeft = false) =>
        GhostRowLayout.Make(new GhostRowLayout.Input(text, firstRow, band, keycap, rightToLeft), Width);

    [Fact]
    public void TextThatFits_IsOneRow()
    {
        var layout = Make(" world")!;
        Assert.Equal([" world"], layout.Rows);
        Assert.False(layout.IsTruncated);
    }

    [Fact]
    public void LongText_WrapsAtWordsOntoTheBand()
    {
        var layout = Make(Long, firstRow: 100, band: 300)!;
        Assert.True(layout.Rows.Count >= 2);
        Assert.Equal(" alpha ", layout.Rows[0]);
        Assert.False(layout.Rows[1].StartsWith(' '));
        Assert.Equal(Long, string.Concat(layout.Rows));
        Assert.All(layout.Rows.Skip(1), row => Assert.True(Width(row.TrimEnd()) <= 300));
    }

    [Fact]
    public void WithOneRow_TheHeadThatFitsIsShown()
    {
        var layout = Make(Long, firstRow: 125, band: null)!;
        Assert.Equal([" alpha bravo"], layout.Rows);
        Assert.True(layout.IsTruncated);
    }

    [Fact]
    public void WithOneRow_NothingFittingDeclines() =>
        Assert.Null(Make(" extraordinary", firstRow: 20, band: null));

    [Fact]
    public void TruncatedHead_DropsTheKeycapWhenItIsWhatCutsTheTextShort()
    {
        var layout = Make(Long, firstRow: 125, band: null, keycap: 30)!;
        Assert.Equal([" alpha bravo"], layout.Rows);
        Assert.False(layout.ShowsKeycap);
    }

    [Fact]
    public void NarrowField_DropsTheKeycapInsteadOfDeclining()
    {
        var layout = Make(" jumps", firstRow: 66, band: null, keycap: 30)!;
        Assert.Equal([" jumps"], layout.Rows);
        Assert.False(layout.IsTruncated);
        Assert.False(layout.ShowsKeycap);
    }

    [Fact]
    public void KeycapThatFits_IsKept() =>
        Assert.True(Make(" world", keycap: 30)!.ShowsKeycap);

    [Fact]
    public void FirstWordTooWideForTheCaretRow_WrapsLikeTheHost()
    {
        var layout = Make(" extraordinary", firstRow: 20, band: 300)!;
        Assert.Equal([" ", "extraordinary"], layout.Rows);
    }

    [Fact]
    public void ACaretRowNothingFitsOn_StaysEmpty()
    {
        var layout = Make("alpha bravo", firstRow: 5, band: 300)!;
        Assert.Equal(["", "alpha bravo"], layout.Rows);
    }

    [Fact]
    public void AWordWiderThanTheBand_Declines() =>
        Assert.Null(Make(" extraordinary", firstRow: 20, band: 50));

    [Fact]
    public void MoreThanSixRows_Declines()
    {
        Assert.Null(Make(Long, firstRow: 70, band: 70));
        Assert.NotNull(Make(" a b c d e f", firstRow: 20, band: 20));
        Assert.Null(Make(" a b c d e f g", firstRow: 20, band: 20));
    }

    [Fact]
    public void HardNewlines_StartNewRowsWhenWrapping() =>
        Assert.Equal([" one", "two", "three"], Make(" one\ntwo\r\nthree")!.Rows);

    [Fact]
    public void HardNewline_WithOneRowShowsTheFirstLine()
    {
        var layout = Make(" one\ntwo", band: null)!;
        Assert.Equal([" one"], layout.Rows);
        Assert.True(layout.IsTruncated);
    }

    [Fact]
    public void HardNewlineAtTheCaret_WithOneRowDeclines() =>
        Assert.Null(Make("\ntwo", band: null));

    [Fact]
    public void RightToLeft_IsOneRowAndDeclinesALineBreak()
    {
        Assert.Equal(["שלום עולם"], Make("שלום עולם", firstRow: 10, rightToLeft: true)!.Rows);
        Assert.Null(Make("שלום\nעולם", rightToLeft: true));
    }

    [Fact]
    public void EmptyText_HasNoLayout() => Assert.Null(Make(""));
}

public sealed class GhostCardPlacementTests
{
    private static readonly ScreenRect Caret = new(200, 100, 2, 20);
    private static readonly ScreenRect Field = new(100, 50, 400, 300);

    [Fact]
    public void CardTextStartsAtTheCaret() =>
        Assert.Equal(189, GhostCardPlacement.Left(Caret, Field, 150, 11, rightToLeft: false));

    [Fact]
    public void RightToLeftCardTextEndsAtTheCaret() =>
        Assert.Equal(61, GhostCardPlacement.Left(Caret, Field, 150, 11, rightToLeft: true));

    [Fact]
    public void ACaretWithNoHeight_CentersTheCardUnderTheField()
    {
        var flat = Caret with { Height = 0 };
        Assert.Equal(225, GhostCardPlacement.Left(flat, Field, 150, 11, rightToLeft: false));
        Assert.Equal(354, GhostCardPlacement.Top(flat, Field, 4));
    }

    [Fact]
    public void CardSitsUnderTheCaretsLine() =>
        Assert.Equal(124, GhostCardPlacement.Top(Caret, Field, 4));

    [Theory]
    [InlineData(" world", "world")]
    [InlineData(" one\ntwo  three ", "one two three")]
    [InlineData("   ", "")]
    public void CardText_IsOneLineWithCollapsedSpaces(string suggestion, string expected) =>
        Assert.Equal(expected, GhostCardPlacement.CardText(suggestion));
}
