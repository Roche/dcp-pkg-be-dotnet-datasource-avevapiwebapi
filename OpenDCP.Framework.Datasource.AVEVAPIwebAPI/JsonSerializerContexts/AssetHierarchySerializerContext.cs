using OpenDCP.Framework.DataTypes.Equipment.Responses.AssetHierarchy;
using System.Text.Json.Serialization;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.JsonSerializerContexts
{
    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        WriteIndented = false)]
    [JsonSerializable(typeof(SensorListResponse))]
    public partial class AssetHierarchySerializerContext : JsonSerializerContext
    {
    }
}
