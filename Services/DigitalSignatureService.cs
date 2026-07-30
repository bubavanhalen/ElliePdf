using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

namespace ElliePdf.Services;

public sealed record SigningCertificateInfo(
    string Thumbprint,
    string DisplayName,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

public sealed record DigitalSignatureRequest(
    string CertificateThumbprint,
    int PageIndex,
    PdfRect Bounds,
    string Reason,
    string Location = "");

public interface ICertificateService
{
    IReadOnlyList<SigningCertificateInfo> GetSigningCertificates();

    SigningCertificateInfo CreateSelfSignedCertificate(string displayName, string? emailAddress = null);
}

public interface IDigitalSignatureService
{
    Task SignAsync(
        string inputPath,
        string outputPath,
        DigitalSignatureRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class CertificateService : ICertificateService
{
    public IReadOnlyList<SigningCertificateInfo> GetSigningCertificates()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        var now = DateTimeOffset.UtcNow;
        return store.Certificates
            .OfType<X509Certificate2>()
            .Where(certificate =>
                certificate.HasPrivateKey &&
                certificate.NotBefore.ToUniversalTime() <= now &&
                certificate.NotAfter.ToUniversalTime() >= now)
            .OrderBy(certificate => certificate.GetNameInfo(X509NameType.SimpleName, false))
            .Select(ToInfo)
            .ToArray();
    }

    public SigningCertificateInfo CreateSelfSignedCertificate(string displayName, string? emailAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        using var rsa = RSA.Create(3072);
        var escapedName = EscapeDistinguishedNameValue(displayName.Trim());
        var subject = string.IsNullOrWhiteSpace(emailAddress)
            ? $"CN={escapedName}"
            : $"CN={escapedName}, E={EscapeDistinguishedNameValue(emailAddress.Trim())}";
        var request = new CertificateRequest(
            new X500DistinguishedName(subject),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pfx = generated.Export(X509ContentType.Pfx, password);
        using var persisted = X509CertificateLoader.LoadPkcs12(
            pfx,
            password,
            X509KeyStorageFlags.UserKeySet |
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.Exportable);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return ToInfo(persisted);
    }

    private static SigningCertificateInfo ToInfo(X509Certificate2 certificate)
    {
        var displayName = certificate.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = certificate.Subject;
        }

        return new SigningCertificateInfo(
            certificate.Thumbprint,
            displayName,
            certificate.NotBefore,
            certificate.NotAfter);
    }

    private static string EscapeDistinguishedNameValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("+", "\\+", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}

public sealed class DigitalSignatureService : IDigitalSignatureService
{
    private static readonly Lock FontSettingsLock = new();
    private static bool _fontSettingsInitialized;

    public async Task SignAsync(
        string inputPath,
        string outputPath,
        DigitalSignatureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using var certificate = FindCertificate(request.CertificateThumbprint);
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("The selected certificate does not have an accessible private key.");
        }

        EnsureWindowsFontsEnabled();
        using var document = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
        if (request.PageIndex < 0 || request.PageIndex >= document.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The signature page is outside the PDF.");
        }

        var bounds = request.Bounds;
        var width = Math.Max(36, bounds.Right - bounds.Left);
        var height = Math.Max(24, bounds.Top - bounds.Bottom);
        var options = new DigitalSignatureOptions
        {
            AppName = "ElliePdf",
            ContactInfo = certificate.GetNameInfo(X509NameType.EmailName, false),
            Location = request.Location,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? "Document approval" : request.Reason,
            PageIndex = request.PageIndex,
            Rectangle = new XRect(bounds.Left, bounds.Bottom, width, height),
            AppearanceHandler = new CertificateAppearanceHandler(
                certificate.GetNameInfo(X509NameType.SimpleName, false))
        };

        _ = DigitalSignatureHandler.ForDocument(
            document,
            new PdfSharpDefaultSigner(certificate, PdfMessageDigestType.SHA256, null),
            options);

        cancellationToken.ThrowIfCancellationRequested();
        await document.SaveAsync(outputPath);
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal),
            validOnly: false);
        return matches.Count == 0
            ? throw new InvalidOperationException("The selected signing certificate is no longer available.")
            : new X509Certificate2(matches[0]);
    }

    private static void EnsureWindowsFontsEnabled()
    {
        lock (FontSettingsLock)
        {
            if (_fontSettingsInitialized)
            {
                return;
            }

            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            _fontSettingsInitialized = true;
        }
    }

    private sealed class CertificateAppearanceHandler(string signerName) : IAnnotationAppearanceHandler
    {
        public void DrawAppearance(XGraphics graphics, XRect rectangle)
        {
            var accent = XColor.FromArgb(255, 28, 112, 180);
            graphics.DrawRectangle(new XSolidBrush(XColors.White), rectangle);
            graphics.DrawRectangle(new XPen(accent, 1), rectangle);

            var iconSize = Math.Min(rectangle.Height - 8, 28);
            var icon = new XRect(rectangle.X + 6, rectangle.Y + 4, iconSize, iconSize);
            graphics.DrawEllipse(new XPen(accent, 1.5), icon);
            graphics.DrawLine(
                new XPen(accent, 2),
                icon.X + (icon.Width * 0.25),
                icon.Y + (icon.Height * 0.55),
                icon.X + (icon.Width * 0.44),
                icon.Y + (icon.Height * 0.73));
            graphics.DrawLine(
                new XPen(accent, 2),
                icon.X + (icon.Width * 0.44),
                icon.Y + (icon.Height * 0.73),
                icon.X + (icon.Width * 0.78),
                icon.Y + (icon.Height * 0.3));

            var textX = icon.Right + 6;
            var textWidth = Math.Max(1, rectangle.Right - textX - 4);
            var name = string.IsNullOrWhiteSpace(signerName) ? "Certificate signature" : signerName;
            graphics.DrawString(
                name,
                new XFont("Arial", Math.Clamp(rectangle.Height * 0.22, 7, 12), XFontStyleEx.Bold),
                XBrushes.Black,
                new XRect(textX, rectangle.Y + 4, textWidth, rectangle.Height / 2),
                XStringFormats.TopLeft);
            graphics.DrawString(
                "Digitally signed with ElliePdf",
                new XFont("Arial", Math.Clamp(rectangle.Height * 0.17, 6, 9), XFontStyleEx.Regular),
                XBrushes.DimGray,
                new XRect(textX, rectangle.Y + (rectangle.Height / 2), textWidth, rectangle.Height / 2),
                XStringFormats.TopLeft);
        }
    }
}
