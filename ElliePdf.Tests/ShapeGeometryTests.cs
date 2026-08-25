using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

public sealed class ShapeGeometryTests
{
    private static ShapeOverlay Shape(ShapeKind kind, double x1, double y1, double x2, double y2, double thickness = 2) =>
        new()
        {
            Kind = kind,
            Start = new PointOverlay { X = x1, Y = y1 },
            End = new PointOverlay { X = x2, Y = y2 },
            Thickness = thickness
        };

    [Fact]
    public void Bounds_are_normalised_whichever_way_the_drag_went()
    {
        var forward = ShapeGeometry.Bounds(Shape(ShapeKind.Rectangle, 10, 20, 60, 80));
        var backward = ShapeGeometry.Bounds(Shape(ShapeKind.Rectangle, 60, 80, 10, 20));

        Assert.Equal(forward, backward);
        Assert.Equal((10d, 20d, 50d, 60d), forward);
    }

    [Fact]
    public void Rectangle_has_four_corners_in_order()
    {
        var corners = ShapeGeometry.RectangleCorners(Shape(ShapeKind.Rectangle, 0, 0, 10, 5));

        Assert.Equal(4, corners.Count);
        Assert.Equal(new ShapeGeometry.Vertex(0, 0), corners[0]);
        Assert.Equal(new ShapeGeometry.Vertex(10, 0), corners[1]);
        Assert.Equal(new ShapeGeometry.Vertex(10, 5), corners[2]);
        Assert.Equal(new ShapeGeometry.Vertex(0, 5), corners[3]);
    }

    [Fact]
    public void Ellipse_curves_close_back_on_themselves()
    {
        var (start, segments) = ShapeGeometry.EllipseCurves(Shape(ShapeKind.Ellipse, 0, 0, 100, 50));

        Assert.Equal(4, segments.Count);

        // Starts at the top of the ellipse and returns there.
        Assert.Equal(50, start.X, 3);
        Assert.Equal(0, start.Y, 3);
        Assert.Equal(start, segments[^1].End);
    }

    [Fact]
    public void Ellipse_curve_endpoints_sit_on_the_ellipse()
    {
        var shape = Shape(ShapeKind.Ellipse, 0, 0, 100, 50);
        var (_, segments) = ShapeGeometry.EllipseCurves(shape);

        foreach (var segment in segments)
        {
            var nx = (segment.End.X - 50) / 50;
            var ny = (segment.End.Y - 25) / 25;
            Assert.Equal(1, (nx * nx) + (ny * ny), 3);
        }
    }

    [Fact]
    public void Arrow_head_points_along_the_shaft()
    {
        var shape = Shape(ShapeKind.Arrow, 0, 0, 100, 0, thickness: 4);
        var head = ShapeGeometry.ArrowHead(shape);

        Assert.NotNull(head);
        Assert.Equal(3, head!.Count);

        // The tip is the shape's end point.
        Assert.Equal(100, head[0].X, 3);
        Assert.Equal(0, head[0].Y, 3);

        // The two barbs sit either side of the shaft, behind the tip.
        Assert.True(head[1].X < 100 && head[2].X < 100);
        Assert.True(Math.Sign(head[1].Y) != Math.Sign(head[2].Y));
    }

    [Fact]
    public void Arrow_shaft_stops_short_of_the_head()
    {
        var shape = Shape(ShapeKind.Arrow, 0, 0, 100, 0, thickness: 4);
        var shaftEnd = ShapeGeometry.ArrowShaftEnd(shape);

        Assert.True(shaftEnd.X < 100, "the shaft must not run through the arrowhead");
        Assert.True(shaftEnd.X > 0);
    }

    [Fact]
    public void A_degenerate_arrow_has_no_head()
    {
        Assert.Null(ShapeGeometry.ArrowHead(Shape(ShapeKind.Arrow, 5, 5, 5, 5)));
    }

    [Fact]
    public void Distance_to_a_rectangle_is_measured_to_its_outline()
    {
        var shape = Shape(ShapeKind.Rectangle, 0, 0, 100, 100);

        // Just outside the left edge.
        Assert.Equal(5, ShapeGeometry.DistanceTo(shape, -5, 50), 3);

        // Dead centre: far from every edge, which is why unfilled shapes are not hit there.
        Assert.Equal(50, ShapeGeometry.DistanceTo(shape, 50, 50), 3);
    }

    [Fact]
    public void Unfilled_shapes_have_no_interior_but_filled_ones_do()
    {
        var shape = Shape(ShapeKind.Rectangle, 0, 0, 100, 100);
        Assert.False(ShapeGeometry.ContainsInterior(shape, 50, 50));

        shape.FillColorHex = "#FF0000";
        Assert.True(ShapeGeometry.ContainsInterior(shape, 50, 50));
        Assert.False(ShapeGeometry.ContainsInterior(shape, 150, 50));
    }

    [Fact]
    public void A_filled_ellipse_uses_an_elliptical_interior_not_its_bounding_box()
    {
        var shape = Shape(ShapeKind.Ellipse, 0, 0, 100, 100);
        shape.FillColorHex = "#FF0000";

        Assert.True(ShapeGeometry.ContainsInterior(shape, 50, 50));

        // The bounding box corner lies outside the ellipse itself.
        Assert.False(ShapeGeometry.ContainsInterior(shape, 2, 2));
    }

    [Fact]
    public void Selection_bounds_allow_for_stroke_width()
    {
        var shape = Shape(ShapeKind.Rectangle, 10, 10, 50, 50, thickness: 10);
        var (left, top, width, height) = ShapeGeometry.SelectionBounds(shape);

        Assert.True(left < 10 && top < 10);
        Assert.True(width > 40 && height > 40);
    }
}
