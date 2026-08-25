using ElliePdf.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ElliePdf.ViewModels;

/// <summary>A saved signature shown in the signature gallery.</summary>
public sealed class SavedSignatureViewModel
{
    public SavedSignatureViewModel(SavedSignature signature, BitmapImage? preview)
    {
        Id = signature.Id;
        Name = signature.Name;
        ImageBase64 = signature.ImageBase64;
        AspectRatio = signature.AspectRatio;
        Preview = preview;
    }

    public string Id { get; }

    public string Name { get; }

    public string ImageBase64 { get; }

    public double AspectRatio { get; }

    public BitmapImage? Preview { get; }
}
