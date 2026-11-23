namespace ElliePdf.Telemetry;

/// <summary>Guards telemetry APIs from document identity, paths and extracted text.</summary>
public static class TelemetryPayloadPolicy
{
    public static bool IsSafe(string? value) => string.IsNullOrEmpty(value) ||
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') &&
        !value.Contains(".pdf", StringComparison.OrdinalIgnoreCase);

    public static void RequireSafe(string? value, string name = "payload")
    {
        if (!IsSafe(value)) throw new ArgumentException($"Telemetry {name} must not contain paths, filenames, or document content.", name);
    }
}
