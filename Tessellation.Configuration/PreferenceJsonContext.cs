using System.Text.Json.Serialization;

namespace Tessellation.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TessellationPreferences))]
internal partial class PreferenceJsonContext : JsonSerializerContext
{
}
