using System.Text.Json.Serialization;
using ElliePdf.Models;

namespace ElliePdf.Services;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PageOverlayDocument))]
internal sealed partial class ElliePdfJsonContext : JsonSerializerContext
{
}
