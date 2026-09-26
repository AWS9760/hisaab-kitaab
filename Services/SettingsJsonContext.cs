using System.Text.Json.Serialization;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

// Source-generated serializer: no runtime reflection, so it keeps working
// in trimmed/single-file publishes.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
