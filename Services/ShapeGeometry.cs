using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Pure geometry for shape annotations, shared by the on-screen surface and the PDF writer.
/// All values are in page units.
/// </summary>
internal static class ShapeGeometry
{
    /// <summary>Circle-to-bezier constant: the handle length that best approximates a quarter arc.</summary>
    private const double Kappa = 0.5522847498307936;

    private const double ArrowHeadLengthFactor = 4.5;
    private const double MinArrowHeadLength = 8;

    public readonly record struct Vertex(double X, double Y);

    public readonly record struct CubicSegment(Vertex Control1, Vertex Control2, Vertex End);

    public static (double Left, double Top, double Width, double Height) Bounds(ShapeOverlay shape)
    {
        var left = Math.Min(shape.Start.X, shape.End.X);
        var top = Math.Min(shape.Start.Y, shape.End.Y);
        return (left, top, Math.Abs(shape.End.X - shape.Start.X), Math.Abs(shape.End.Y - shape.Start.Y));
    }

    /// <summary>Bounds padded by the stroke's half-width, which is what the eye sees.</summary>
    public static (double Left, double Top, double Width, double Height) SelectionBounds(ShapeOverlay shape)
    {
        var (left, top, width, height) = Bounds(shape);
        var padding = Math.Max(2, shape.Thickness / 2);
        return (left - padding, top - padding, width + (padding * 2), height + (padding * 2));
    }

    public static IReadOnlyList<Vertex> RectangleCorners(ShapeOverlay shape)
    {
        var (left, top, width, height) = Bounds(shape);
        return
        [
            new Vertex(left, top),
            new Vertex(left + width, top),
            new Vertex(left + width, top + height),
            new Vertex(left, top + height)
        ];
    }

    /// <summary>The ellipse as a start point plus four cubic segments, running clockwise from the top.</summary>
    public static (Vertex Start, IReadOnlyList<CubicSegment> Segments) EllipseCurves(ShapeOverlay shape)
    {
        var (left, top, width, height) = Bounds(shape);
        var rx = width / 2;
        var ry = height / 2;
        var cx = left + rx;
        var cy = top + ry;
        var hx = rx * Kappa;
        var hy = ry * Kappa;

        var start = new Vertex(cx, cy - ry);
        var segments = new CubicSegment[]
        {
            new(new Vertex(cx + hx, cy - ry), new Vertex(cx + rx, cy - hy), new Vertex(cx + rx, cy)),
            new(new Vertex(cx + rx, cy + hy), new Vertex(cx + hx, cy + ry), new Vertex(cx, cy + ry)),
            new(new Vertex(cx - hx, cy + ry), new Vertex(cx - rx, cy + hy), new Vertex(cx - rx, cy)),
            new(new Vertex(cx - rx, cy - hy), new Vertex(cx - hx, cy - ry), start)
        };

        return (start, segments);
    }

    /// <summary>
    /// The three corners of an arrowhead at <see cref="ShapeOverlay.End"/>. Returns <c>null</c> when
    /// the shaft is too short to place one.
    /// </summary>
    public static IReadOnlyList<Vertex>? ArrowHead(ShapeOverlay shape)
    {
        var dx = shape.End.X - shape.Start.X;
        var dy = shape.End.Y - shape.Start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < 1e-3)
        {
            return null;
        }

        var ux = dx / length;
        var uy = dy / length;

        var head = Math.Min(length, Math.Max(MinArrowHeadLength, shape.Thickness * ArrowHeadLengthFactor));
        var halfWidth = head * 0.45;

        // Base of the head, back along the shaft from the tip.
        var baseX = shape.End.X - (ux * head);
        var baseY = shape.End.Y - (uy * head);

        return
        [
            new Vertex(shape.End.X, shape.End.Y),
            new Vertex(baseX - (uy * halfWidth), baseY + (ux * halfWidth)),
            new Vertex(baseX + (uy * halfWidth), baseY - (ux * halfWidth))
        ];
    }

    /// <summary>
    /// Where the shaft should stop so it does not poke through the arrowhead.
    /// </summary>
    public static Vertex ArrowShaftEnd(ShapeOverlay shape)
    {
        var dx = shape.End.X - shape.Start.X;
        var dy = shape.End.Y - shape.Start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < 1e-3)
        {
            return new Vertex(shape.End.X, shape.End.Y);
        }

        var head = Math.Min(length, Math.Max(MinArrowHeadLength, shape.Thickness * ArrowHeadLengthFactor));
        // Overlap slightly so no gap shows at the join.
        var stop = Math.Max(0, length - (head * 0.8));

        return new Vertex(shape.Start.X + (dx / length * stop), shape.Start.Y + (dy / length * stop));
    }

    /// <summary>Distance from a point to the shape's drawn outline, for hit-testing and erasing.</summary>
    public static double DistanceTo(ShapeOverlay shape, double x, double y)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Line:
            case ShapeKind.Arrow:
                return DistanceToSegment(x, y, shape.Start.X, shape.Start.Y, shape.End.X, shape.End.Y);

            case ShapeKind.Rectangle:
            {
                var corners = RectangleCorners(shape);
                var closest = double.MaxValue;
                for (var index = 0; index < corners.Count; index++)
                {
                    var next = corners[(index + 1) % corners.Count];
                    closest = Math.Min(
                        closest,
                        DistanceToSegment(x, y, corners[index].X, corners[index].Y, next.X, next.Y));
                }

                return closest;
            }

            default:
            {
                // Distance to the ellipse, approximated by sampling its perimeter.
                var (left, top, width, height) = Bounds(shape);
                var rx = Math.Max(1e-6, width / 2);
                var ry = Math.Max(1e-6, height / 2);
                var cx = left + rx;
                var cy = top + ry;

                var closest = double.MaxValue;
                const int samples = 64;
                for (var index = 0; index < samples; index++)
                {
                    var angle = 2 * Math.PI * index / samples;
                    var px = cx + (rx * Math.Cos(angle));
                    var py = cy + (ry * Math.Sin(angle));
                    closest = Math.Min(closest, Math.Sqrt(((x - px) * (x - px)) + ((y - py) * (y - py))));
                }

                return closest;
            }
        }
    }

    /// <summary>True when the point lies inside a filled shape's interior.</summary>
    public static bool ContainsInterior(ShapeOverlay shape, double x, double y)
    {
        if (shape.FillColorHex is null || shape.Kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            return false;
        }

        var (left, top, width, height) = Bounds(shape);

        if (shape.Kind == ShapeKind.Rectangle)
        {
            return x >= left && x <= left + width && y >= top && y <= top + height;
        }

        var rx = Math.Max(1e-6, width / 2);
        var ry = Math.Max(1e-6, height / 2);
        var nx = (x - (left + rx)) / rx;
        var ny = (y - (top + ry)) / ry;
        return (nx * nx) + (ny * ny) <= 1;
    }

    private static double DistanceToSegment(double x, double y, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;

        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
        {
            return Math.Sqrt(((x - ax) * (x - ax)) + ((y - ay) * (y - ay)));
        }

        var t = Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
        var px = ax + (t * dx);
        var py = ay + (t * dy);
        return Math.Sqrt(((x - px) * (x - px)) + ((y - py) * (y - py)));
    }
}
