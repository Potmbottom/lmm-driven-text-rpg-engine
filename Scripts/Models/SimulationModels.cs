using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace RPG.Models.Simulation
{
// --- 0. NEW: Query Meta Data ---
    public class QueryMetaData
    {
        [JsonPropertyName("user_query")] public string UserQuery { get; set; }
        
        /// <summary>
        /// Format: "days minutes:seconds" (e.g., "0 10:00" for 10 mins, "1 00:00" for 1 day).
        /// Can represent a duration or specific time range.
        /// </summary>
        [JsonPropertyName("target_simulation_duration")] public string TargetSimulationDuration { get; set; } 
        
        [JsonPropertyName("info")] public string Info { get; set; }
        
        [JsonPropertyName("simulation_start_time")] public string SimulationStartTime { get; set; }
    }
    
    public class SimulationResponse
    {
        [JsonPropertyName("structured")] public List<SimulationStep> Structured { get; set; } = new();

        [JsonPropertyName("break_point")] public string BreakPoint { get; set; } // "Time", "Expansion", "Check"

        [JsonPropertyName("break_description")]
        public string BreakDescription { get; set; }
    }

    public class SimulationStep
    {
        [JsonPropertyName("actions")] public List<SimulationAction> Actions { get; set; } = new();

        [JsonPropertyName("time")] public string Time { get; set; }
    }

    public class SimulationAction
    {
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("action")] public JsonElement ActionData { get; set; }
    }
    
    public class ActionMoveToCell
    {
        [JsonPropertyName("set_id")] public string SetId { get; set; } // Cell Index

        [JsonPropertyName("object_id")] public int ObjectId { get; set; }
    }
    
    public class ActionMoveToObject
    {
        [JsonPropertyName("set_id")] public int SetId { get; set; } // Parent Object Id

        [JsonPropertyName("object_id")] public int ObjectId { get; set; }
    }
    
    public class ActionExpandHistory
    {
        [JsonPropertyName("object_id")] public int ObjectId { get; set; }

        [JsonPropertyName("new_text")] public string NewText { get; set; }
    }
    
    public class ActionGroupUpdate
    {
        [JsonPropertyName("cells_id")] public List<string> CellsId { get; set; }
        [JsonPropertyName("new_text")] public string NewText { get; set; }
    }
    
    public class NarrativeResponse
    {
        [JsonPropertyName("text")] public string Text { get; set; }
        [JsonPropertyName("isUnsafe")] public bool IsUnsafe { get; set; }
    }
    
    public class CheckBreakData
    {
        [JsonPropertyName("reason")] public string Reason { get; set; }
        [JsonPropertyName("payload")] public int Payload { get; set; }
    }
    
    public class LocationSearchResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("location_ids")]
        public List<int> LocationIds { get; set; } = new();
    }

    public class ContextSearchResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("think")]
        public string Think { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("search_queries")]
        public List<string> Queries { get; set; }
    }

    public class ExpandRetrieveBreakData
    {
        [JsonPropertyName("target_cell")] public string TargetCell { get; set; }
        [JsonPropertyName("target_id")] public int TargetId { get; set; }
        [JsonPropertyName("generation_information")] public string GenerationInformation { get; set; }
    }
}