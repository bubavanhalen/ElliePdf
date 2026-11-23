using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ElliePdf.Helpers;

internal static class BitmapHelper
{
    public static async Task<BitmapImage> CreateBitmapAsync(byte[] imageBytes)
    {
        var bitmap = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(imageBytes.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    public static ImageSource CreateBitmapFromBgra(byte[] bgraPixels, int width, int height)
        => CreateBitmapFromBgra(bgraPixels, width, height, checked(width * 4));

    public static ImageSource CreateBitmapFromBgra(byte[] bgraPixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (stride < checked(width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        var expectedLength = checked(stride * height);
        if (bgraPixels.Length < expectedLength)
        {
            throw new ArgumentException(
                $"Expected {expectedLength} BGRA bytes for a {width}x{height} surface.",
                nameof(bgraPixels));
        }

        var bitmap = new WriteableBitmap(width, height);
        using var pixelStream = bitmap.PixelBuffer.AsStream();
        if (stride == width * 4)
        {
            pixelStream.Write(bgraPixels, 0, checked(width * height * 4));
        }
        else
        {
            var packedStride = checked(width * 4);
            for (var row = 0; row < height; row++)
            {
                pixelStream.Write(bgraPixels, checked(row * stride), packedStride);
            }
        }
        bitmap.Invalidate();
        return bitmap;
    }
}
