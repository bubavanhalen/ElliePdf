using System.Collections.Immutable;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Pdf.Contracts;

/// <summary>The version of the wire-neutral contract shapes, independent of the IPC transport.</summary>
public readonly record struct PdfContractVersion(int Major, int Minor)
{
    public static PdfContractVersion Current => new(1, 1);

    public PdfContractVersion Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Major);
        ArgumentOutOfRangeException.ThrowIfNegative(Minor);
        return this;
    }
}

public static class PdfContractLimits
{
    public const int MaxStringLength = 4_096;
    public const int MaxMetadataStringLength = 4_096;
    public const int MaxPasswordLength = 512;
    public const int MaxPageCount = 1_000_000;
    public const int MaxCollectionCount = 100_000;
    public const int MaxOutlineDepth = 128;
    public const int MaxTextLength = 16 * 1024 * 1024;
    public const int MaxSearchQueryLength = 4_096;
    public const int MaxFormOptions = 10_000;
    public const int MaxPageRanges = 100_000;
    public const int MaxPixelDimension = 32_768;
    public const int MaxPixelStride = MaxPixelDimension * 4;
    public const long MaxPixelBufferBytes = 16L * 1024 * 1024;

    internal static string RequiredString(string value, int maximum, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        if (value.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value.Length, $"The value must be at most {maximum} characters.");
        }

        return value;
    }

    internal static string? OptionalString(string? value, int maximum, string parameterName)
    {
        if (value is not null && value.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value.Length, $"The value must be at most {maximum} characters.");
        }

        return value;
    }

    internal static ImmutableArray<T> ReadOnly<T>(IEnumerable<T>? values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var array = values.ToImmutableArray();
        if (array.Length > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, array.Length, $"The collection must contain at most {maximum} items.");
        }

        return array;
    }

    internal static void PageIndex(int value, string parameterName = "pageIndex")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        if (value >= MaxPageCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The page index must be less than {MaxPageCount}.");
        }
    }

    internal static void FinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be finite and greater than zero.");
        }
    }

    internal static void FiniteNonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be finite and non-negative.");
        }
    }
}

public readonly record struct PdfPoint(double X, double Y)
{
    public PdfPoint Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y))
        {
            throw new ArgumentOutOfRangeException(nameof(PdfPoint), "Coordinates must be finite.");
        }

        return this;
    }
}

public readonly record struct PdfRect(double Left, double Top, double Right, double Bottom)
{
    public PdfRect Validate()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Right) || !double.IsFinite(Bottom))
        {
            throw new ArgumentOutOfRangeException(nameof(PdfRect), "Coordinates must be finite.");
        }

        if (Right < Left || Bottom < Top)
        {
            throw new ArgumentOutOfRangeException(nameof(PdfRect), "Right/Bottom must not precede Left/Top.");
        }

        return this;
    }
}

public enum RenderQuality
{
    Draft,
    Standard,
    High
}

public enum EngineJobPriority
{
    VisibleInteractionCritical,
    OtherVisible,
    DirectionalOverscan,
    VisibleThumbnail,
    DirectionalPrefetch,
    Background
}

public enum PixelFormat
{
    Bgra8Premultiplied
}

public readonly record struct PdfSourceHandle(string Value)
{
    public PdfSourceHandle Validate()
    {
        PdfContractLimits.RequiredString(Value, PdfContractLimits.MaxStringLength, nameof(Value));
        return this;
    }
}

public readonly record struct FormFieldId(Guid Value)
{
    public FormFieldId Validate()
    {
        if (Value == Guid.Empty) throw new ArgumentException("The field id must not be empty.", nameof(Value));
        return this;
    }
}
