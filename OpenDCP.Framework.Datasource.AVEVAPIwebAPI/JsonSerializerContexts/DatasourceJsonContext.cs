using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenDCP.Framework.Datasource.AVEVAPIwebAPI.JsonSerializerContexts
{
    [JsonSerializable(typeof(JsonObject))]
    [JsonSerializable(typeof(JsonArray))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    [JsonSerializable(typeof(List<string>))]
    public partial class DatasourceSerializerContext : JsonSerializerContext
    {

    }
}
