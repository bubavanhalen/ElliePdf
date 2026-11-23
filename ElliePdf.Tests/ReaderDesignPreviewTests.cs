using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

public sealed class ReaderDesignPreviewTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invoice--design-preview.pdf")]
    [InlineData("--design-preview extra")]
    public void Normal_or_file_activation_does_not_enable_preview(string? arguments)
    {
        ReaderDesignPreview.Configure(arguments);
        Assert.False(ReaderDesignPreview.IsEnabled);
    }

    [Fact]
    public void Only_explicit_debug_activation_enables_preview()
    {
        ReaderDesignPreview.Configure("--design-preview");
#if DEBUG
        Assert.True(ReaderDesignPreview.IsEnabled);
#else
        Assert.False(ReaderDesignPreview.IsEnabled);
#endif
        ReaderDesignPreview.Configure(null);
        Assert.False(ReaderDesignPreview.IsEnabled);
    }
}
