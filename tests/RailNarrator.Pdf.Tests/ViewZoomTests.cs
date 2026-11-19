using RailNarrator.Pdf;
using Xunit;

namespace RailNarrator.Pdf.Tests;

public sealed class ViewZoomTests
{
    /// <summary>
    /// The defect this replaces: a cover sheet 634 pt wide in front of an article 504 pt wide. Fit
    /// was computed once from the cover, so the article filled 79 % of the window.
    /// </summary>
    [Fact]
    public void FitWidth_FollowsThePageShown()
    {
        var zoom = new ViewZoom();
        Assert.True(zoom.Layout(viewWidth: 1268, pageWidth: 634));
        Assert.Equal(2.0, zoom.Zoom, 9);

        Assert.True(zoom.Layout(viewWidth: 1268, pageWidth: 504));
        Assert.Equal(1268.0 / 504, zoom.Zoom, 9);
        Assert.Equal(1268, 504 * zoom.Zoom, 6);   // the article page fills the view
    }

    [Fact]
    public void FitWidth_FollowsTheViewsWidth()
    {
        var zoom = new ViewZoom();
        zoom.Layout(1000, 500);
        Assert.True(zoom.Layout(700, 500));   // the script panel opened
        Assert.Equal(1.4, zoom.Zoom, 9);
        Assert.False(zoom.Layout(700, 500));  // nothing changed: no re-render, no camera reset
    }

    [Fact]
    public void AZoomTheReaderChose_SurvivesAResizeAndAPageTurn()
    {
        var zoom = new ViewZoom();
        zoom.Layout(1000, 500);
        zoom.SetByReader(4.0);

        Assert.False(zoom.Layout(700, 500));    // resize
        Assert.False(zoom.Layout(700, 634));    // another page size
        Assert.Equal(4.0, zoom.Zoom);
        Assert.False(zoom.FitsWidth);
    }

    [Fact]
    public void AskingToFitAgain_ReFitsAtTheNextLayout()
    {
        var zoom = new ViewZoom(fitsWidth: false, zoom: 4.0);
        Assert.False(zoom.Layout(1000, 500));
        Assert.Equal(4.0, zoom.Zoom);

        zoom.Fit();
        Assert.True(zoom.Layout(1000, 500));
        Assert.Equal(2.0, zoom.Zoom, 9);
    }

    [Fact]
    public void Pinning_KeepsTheFittedZoomAsTheReaders()
    {
        var zoom = new ViewZoom();
        zoom.Layout(1000, 500);
        zoom.Pin();
        Assert.False(zoom.Layout(600, 500));
        Assert.Equal(2.0, zoom.Zoom, 9);
    }

    [Theory]
    [InlineData(100, 1000, ViewZoom.Minimum)]    // a very wide page in a narrow view
    [InlineData(10000, 100, ViewZoom.Maximum)]   // a tiny page
    public void AFit_StaysWithinTheZoomLimits(double viewWidth, double pageWidth, double expected)
    {
        var zoom = new ViewZoom();
        zoom.Layout(viewWidth, pageWidth);
        Assert.Equal(expected, zoom.Zoom);
    }

    [Theory]
    [InlineData(0, 500)]      // before the first layout pass
    [InlineData(1000, 0)]     // a page that could not be measured
    [InlineData(double.NaN, 500)]
    public void NothingToMeasure_ChangesNothing(double viewWidth, double pageWidth)
    {
        var zoom = new ViewZoom(fitsWidth: true, zoom: 1.5);
        Assert.False(zoom.Layout(viewWidth, pageWidth));
        Assert.Equal(1.5, zoom.Zoom);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(-3.0, 1.0)]
    [InlineData(50.0, ViewZoom.Maximum)]
    [InlineData(0.01, ViewZoom.Minimum)]
    public void AStoredZoomThatIsUnusable_IsReplaced(double stored, double expected)
        => Assert.Equal(expected, new ViewZoom(fitsWidth: false, zoom: stored).Zoom);
}
