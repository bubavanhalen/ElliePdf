using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Pure geometry for pressure-sensitive ink, shared by the on-screen surface and the PDF writer so
/// a stroke looks the same once it is embedded.
/// </summary>
internal static class InkGeometry
{
    /// <summary>Width at zero pressure, as a fraction of the stroke's nominal thickness.</summary>
    private const double MinWidthFactor = 0.35;

    public readonly record struct Vertex(double X, double Y);

    /// <summary>True when every point carries the same pressure, so a plain stroke will do.</summary>
    public static bool HasUniformPressure(IReadOnlyList<PointOverlay> points)
    {
        if (points.Count == 0)
        {
            return true;
        }

        var first = points[0].Pressure;
        foreach (var point in points)
        {
            if (Math.Abs(point.Pressure - first) > 0.02)
            {
                return false;
            }
        }

        return true;
    }

    public static double WidthAt(double thickness, double pressure) =>
        thickness * (MinWidthFactor + ((1 - MinWidthFactor) * Math.Clamp(pressure, 0, 1)));

    /// <summary>
    /// Converts a stroke into a closed outline polygon whose width follows pen pressure. The
    /// polygon runs up one side of the centre line and back down the other, so it can be filled.
    /// </summary>
    public static IReadOnlyList<Vertex> BuildOutline(IReadOnlyList<PointOverlay> points, double thickness)
    {
        var centre = Deduplicate(points);
        if (centre.Count < 2)
        {
            return [];
        }

        var left = new List<Vertex>(centre.Count);
        var right = new List<Vertex>(centre.Count);

        for (var index = 0; index < centre.Count; index++)
        {
            var (nx, ny) = NormalAt(centre, index);
            var half = WidthAt(thickness, centre[index].Pressure) / 2;

            left.Add(new Vertex(centre[index].X + (nx * half), centre[index].Y + (ny * half)));
            right.Add(new Vertex(centre[index].X - (nx * half), centre[index].Y - (ny * half)));
        }

        var outline = new List<Vertex>(left.Count + right.Count);
        outline.AddRange(left);
        for (var index = right.Count - 1; index >= 0; index--)
        {
            outline.Add(right[index]);
        }

        return outline;
    }

    /// <summary>Unit normal to the stroke direction, averaged across the joint at interior points.</summary>
    private static (double X, double Y) NormalAt(IReadOnlyList<PointOverlay> points, int index)
    {
        var previous = points[Math.Max(0, index - 1)];
        var next = points[Math.Min(points.Count - 1, index + 1)];

        var dx = next.X - previous.X;
        var dy = next.Y - previous.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < 1e-6)
        {
            return (0, 1);
        }

        // Rotate the tangent a quarter turn.
        return (-dy / length, dx / length);
    }

    private static List<PointOverlay> Deduplicate(IReadOnlyList<PointOverlay> points)
    {
        var result = new List<PointOverlay>(points.Count);

        foreach (var point in points)
        {
            if (result.Count == 0 ||
                Math.Abs(point.X - result[^1].X) > 1e-6 ||
                Math.Abs(point.Y - result[^1].Y) > 1e-6)
            {
                result.Add(point);
            }
        }

        return result;
    }

    /// <summary>
    /// Removes the part of a stroke covered by an eraser disc, returning the fragments that remain.
    /// An empty result means the whole stroke was erased.
    /// </summary>
    public static List<List<PointOverlay>> Erase(
        IReadOnlyList<PointOverlay> points,
        double centreX,
        double centreY,
        double radius)
    {
        var fragments = new List<List<PointOverlay>>();
        var current = new List<PointOverlay>();

        foreach (var point in points)
        {
            var dx = point.X - centreX;
            var dy = point.Y - centreY;

            if ((dx * dx) + (dy * dy) <= radius * radius)
            {
                // Inside the eraser: end the run in progress.
                if (current.Count >= 2)
                {
                    fragments.Add(current);
                }

                current = [];
                continue;
            }

            current.Add(point);
        }

        if (current.Count >= 2)
        {
            fragments.Add(current);
        }

        return fragments;
    }

    /// <summary>Shortest distance from a point to the stroke's centre line.</summary>
    public static double DistanceTo(IReadOnlyList<PointOverlay> points, double x, double y)
    {
        if (points.Count == 0)
        {
            return double.MaxValue;
        }

        if (points.Count == 1)
        {
            return Distance(x, y, points[0].X, points[0].Y);
        }

        var closest = double.MaxValue;
        for (var index = 1; index < points.Count; index++)
        {
            closest = Math.Min(closest, DistanceToSegment(x, y, points[index - 1], points[index]));
        }

        return closest;
    }

    private static double DistanceToSegment(double x, double y, PointOverlay start, PointOverlay end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;

        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
        {
            return Distance(x, y, start.X, start.Y);
        }

        var t = Math.Clamp((((x - start.X) * dx) + ((y - start.Y) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
        return Distance(x, y, start.X + (t * dx), start.Y + (t * dy));
    }

    private static double Distance(double ax, double ay, double bx, double by) =>
        Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
}
