using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ElliePdf.Pdfium;

public sealed record PdfiumAssetExpectation(
    string RuntimeIdentifier,
    long Length,
    string Sha256,
    ushort PeMachine)
{
    public static PdfiumAssetExpectation ForCurrentProcess() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => PdfiumKnownAssets.WinX64,
        Architecture.Arm64 => PdfiumKnownAssets.WinArm64,
        var architecture => throw new PlatformNotSupportedException(
            $"ElliePdf supports PDFium only on x64 and ARM64, not {architecture}.")
    };
}

public static class PdfiumKnownAssets
{
    public static PdfiumAssetExpectation WinX64 { get; } = new(
        "win-x64",
        7_262_720,
        "2A9031FA88F412147C3BC7115054550048C724DB6EA70298B6C6B0D13E513882",
        0x8664);

    public static PdfiumAssetExpectation WinArm64 { get; } = new(
        "win-arm64",
        6_705_152,
        "B8A41647AC18C039C4A9CE4F00C1D71A08133EDF92531A9C7903FD985A04DB73",
        0xAA64);
}

public static class PdfiumAssetVerifier
{
    public static string GetAppPrivatePath(string? baseDirectory = null)
    {
        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        return Path.Combine(root, "pdfium.dll");
    }

    public static void VerifyAppPrivateAsset(
        string? baseDirectory = null,
        PdfiumAssetExpectation? expectation = null)
    {
        using var verified = OpenVerifiedAppPrivateAsset(baseDirectory, expectation);
    }

    internal static FileStream OpenVerifiedAppPrivateAsset(
        string? baseDirectory = null,
        PdfiumAssetExpectation? expectation = null)
    {
        var expected = expectation ?? PdfiumAssetExpectation.ForCurrentProcess();
        var path = GetAppPrivatePath(baseDirectory);
        var info = new FileInfo(path);

        if (!info.Exists)
        {
            throw new FileNotFoundException(
                $"The pinned {expected.RuntimeIdentifier} app-private pdfium.dll is missing.",
                path);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new BadImageFormatException("The app-private pdfium.dll must not be a reparse point.");
        }

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);

        try
        {
            if (stream.Length != expected.Length)
            {
                throw new BadImageFormatException(
                    $"The app-private pdfium.dll length does not match the pinned {expected.RuntimeIdentifier} asset.");
            }

            var machine = ReadPeMachine(stream);
            if (machine != expected.PeMachine)
            {
                throw new BadImageFormatException(
                    $"The app-private pdfium.dll PE architecture 0x{machine:X4} does not match " +
                    $"{expected.RuntimeIdentifier} (0x{expected.PeMachine:X4}).");
            }

            stream.Position = 0;
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
            {
                throw new BadImageFormatException(
                    $"The app-private pdfium.dll hash does not match the pinned {expected.RuntimeIdentifier} asset.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static ushort ReadPeMachine(Stream stream)
    {
        Span<byte> dosHeader = stackalloc byte[64];
        stream.Position = 0;
        stream.ReadExactly(dosHeader);
        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw new BadImageFormatException("The app-private pdfium.dll has no DOS PE header.");
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3C..]);
        if (peOffset < 64 || peOffset > stream.Length - 6)
        {
            throw new BadImageFormatException("The app-private pdfium.dll has an invalid PE offset.");
        }

        stream.Position = peOffset;
        Span<byte> peHeader = stackalloc byte[6];
        stream.ReadExactly(peHeader);
        if (peHeader[0] != (byte)'P'
            || peHeader[1] != (byte)'E'
            || peHeader[2] != 0
            || peHeader[3] != 0)
        {
            throw new BadImageFormatException("The app-private pdfium.dll has an invalid PE signature.");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..]);
    }
}
