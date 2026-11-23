using Microsoft.UI.Xaml.XamlTypeInfo;

namespace ElliePdf.ElliePdf_XamlTypeInfo;

internal partial class XamlTypeInfoProvider
{
    public XamlTypeInfoProvider()
    {
        AddOtherProvider(new XamlControlsXamlMetaDataProvider());
    }
}
