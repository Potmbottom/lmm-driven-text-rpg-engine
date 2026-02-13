using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RPG.Models
{
    public class ToolResponseContainer
    {
        [JsonPropertyName("tool")] public string ToolName { get; set; }
        [JsonPropertyName("result")] public ToolResultContent Result { get; set; }
    }

    public class ToolResultContent
    {
        [JsonPropertyName("mutable")] public MutableData Mutable { get; set; } = new();

        [JsonPropertyName("immutable")] public ImmutableData Immutable { get; set; } = new();
        [JsonPropertyName("temporary")] public TemporaryData Temporary { get; set; } = new();
    }

    public class MutableData
    {
        [JsonPropertyName("locations")] public List<LocationData> Locations { get; set; } = new();
        [JsonPropertyName("objects")] public List<ObjectData> Objects { get; set; } = new();
    }

    public class ImmutableData
    {
        [JsonPropertyName("text")] public TextEntry Text { get; set; }
    }
    
    public class TemporaryData
    {
        [JsonPropertyName("text")] public string Result { get; set; }
    }
    
    public class GenerationRequest
    {
        [JsonPropertyName("type")] public string Type { get; set; } // location, group, object
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("cell")] public string Cell { get; set; }
        [JsonPropertyName("id")] public int? Id { get; set; }
    }
        
    public class ObjectsResponse
    {
        [JsonPropertyName("objects")] public List<ObjectData> Objects { get; set; }
    }

    public class GroupResponse
    {
        [JsonPropertyName("groups")] public List<GroupData> Groups { get; set; }
    }
}