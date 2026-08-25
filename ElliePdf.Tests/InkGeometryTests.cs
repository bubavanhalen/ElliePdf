using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

public sealed class InkGeometryTests
{
    private static List<PointOverlay> Line(int count, double pressure = 1)
    {
        var points = new List<PointOverlay>();
        for (var index = 0; index < count; index++)
        {
            points.Add(new PointOverlay { X = index * 10, Y = 0, Pressure = pressure });
        }

        return points;
    }

    [Fact]
    public void Mouse_strokes_are_uniform_so_they_stay_constant_width()
    {
        Assert.True(InkGeometry.HasUniformPressure(Line(5)));
    }

    [Fact]
    public void Varying_pressure_is_detected()
    {
        var points = Line(3);
        points[1].Pressure = 0.4;
        Assert.False(InkGeometry.HasUniformPressure(points));
    }

    [Fact]
    public void Width_tapers_with_pressure_but_never_reaches_zero()
    {
        var full = InkGeometry.WidthAt(10, 1);
        var light = InkGeometry.WidthAt(10, 0);

        Assert.Equal(10, full, 3);
        Assert.True(light > 0, "a zero-pressure sample must still draw something");
        Assert.True(light < full);
    }

    [Fact]
    public void Outline_is_closed_and_matches_the_stroke_width()
    {
        var points = Line(4);
        var outline = InkGeometry.BuildOutline(points, 8);

        // One vertex per side, per point.
        Assert.Equal(points.Count * 2, outline.Count);

        // The stroke runs along y = 0, so the ribbon spans half the width either side.
        var top = outline.Min(v => v.Y);
        var bottom = outline.Max(v => v.Y);
        Assert.Equal(8, bottom - top, 1);
    }

    [Fact]
    public void Erasing_the_middle_splits_a_stroke_in_two()
    {
        var points = Line(11);

        var fragments = InkGeometry.Erase(points, centreX: 50, centreY: 0, radius: 12);

        Assert.Equal(2, fragments.Count);
        Assert.All(fragments, fragment => Assert.True(fragment.Count >= 2));

        // Nothing within the eraser disc may survive.
        Assert.All(
            fragments.SelectMany(fragment => fragment),
            point => Assert.True(Math.Abs(point.X - 50) > 12));
    }

    [Fact]
    public void Erasing_an_end_shortens_the_stroke_without_splitting_it()
    {
        var fragments = InkGeometry.Erase(Line(11), centreX: 0, centreY: 0, radius: 15);

        var fragment = Assert.Single(fragments);
        Assert.True(fragment.All(point => point.X > 15));
    }

    [Fact]
    public void Erasing_everything_removes_the_stroke()
    {
        Assert.Empty(InkGeometry.Erase(Line(5), centreX: 20, centreY: 0, radius: 500));
    }

    [Fact]
    public void Distance_is_measured_to_the_stroke_not_its_endpoints()
    {
        var points = Line(11);

        // Directly above the middle of the line.
        Assert.Equal(5, InkGeometry.DistanceTo(points, 50, 5), 3);

        // Beyond the end, so the nearest point is the endpoint itself.
        Assert.Equal(10, InkGeometry.DistanceTo(points, 110, 0), 3);
    }

    [Fact]
    public void A_pen_reporting_constant_half_pressure_still_draws_at_full_width()
    {
        // Digitizers without pressure support report a flat 0.5 for every sample. Treating that
        // literally would silently draw every stroke at 67% of the chosen thickness.
        var points = Line(6, pressure: 0.5);

        Assert.True(
            InkGeometry.HasUniformPressure(points),
            "a constant-pressure stroke must be detectable so it can be normalised");
    }

    [Fact]
    public void An_outline_is_only_built_for_strokes_that_actually_vary()
    {
        var varying = Line(6);
        varying[2].Pressure = 0.3;
        varying[3].Pressure = 0.6;

        var outline = InkGeometry.BuildOutline(varying, 10);

        // The narrow middle must be measurably thinner than the full-pressure ends.
        var startWidth = Math.Abs(outline[0].Y - outline[^1].Y);
        var middleWidth = Math.Abs(outline[2].Y - outline[^3].Y);
        Assert.True(middleWidth < startWidth, $"expected a taper, got {middleWidth} vs {startWidth}");
    }
}
