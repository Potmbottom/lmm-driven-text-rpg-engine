using System.Collections.Generic;
using System.Text.Json;
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
    
    public class KeysGenerationResponse
    {
        [JsonPropertyName("keys")] public Dictionary<int, List<string>> Keys { get; set; }
    }
    
    public class GenerationRequest
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("id")] public int? Id { get; set; }
        [JsonPropertyName("target_cells")] public List<string> TargetCells { get; set; }
        [JsonPropertyName("target_locations")] public List<int> TargetLocations { get; set; }
    }
        
    public class ObjectsResponse
    {
        [JsonPropertyName("objects")] public List<ObjectData> Objects { get; set; }
    }
    
    public class OrchestratorDecision
    {
        [JsonPropertyName("think")] public string Think { get; set; }
        [JsonPropertyName("tool_calls")] public List<OrchestratorToolData> Tools { get; set; }
    }
    public class OrchestratorToolData
    {
        [JsonPropertyName("tool_name")] public string ToolName { get; set; }
        [JsonPropertyName("payload")] public string Payload { get; set; }
    }

    public class SelectionResult
    {
        [JsonPropertyName("selected_id")] public int? SelectedId { get; set; }
        [JsonPropertyName("selected_index")] public int? SelectedIndex { get; set; }
    }

    public class FinalResponse
    {
        [JsonPropertyName("result")] public JsonElement Result { get; set; }
    }
}