using System.Text.Json.Serialization;
using ElliePdf.Models;

namespace ElliePdf.Services;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PageOverlayDocument))]
[JsonSerializable(typeof(UserSettings))]
internal sealed partial class ElliePdfJsonContext : JsonSerializerContext
{
}
