using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace RPG.Models.Simulation
{
// --- 1. Active Zone ---
    public class ActiveZoneResponse
    {
        [JsonPropertyName("thinking")] public string Thinking { get; set; }
        [JsonPropertyName("result")] public int? LocationId { get; set; }
    }

// --- 2. Time Calculation ---
    public class TimeCalculationResponse
    {
        [JsonPropertyName("time")] public string Time { get; set; } // "ss:mm:hh" or "00:00:00"

        [JsonPropertyName("result")] public bool HasExplicitTime { get; set; }
    }

// --- 3. Simulation Core Response ---
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

        [JsonPropertyName("text")] public string Text { get; set; }

        [JsonPropertyName("time")] public string Time { get; set; }
    }

    public class SimulationAction
    {
        [JsonPropertyName("type")] public string Type { get; set; }

        // Action is dynamic, we keep it as JsonElement for manual parsing 
        // or parse it into specific classes based on Type
        [JsonPropertyName("action")] public JsonElement ActionData { get; set; }
    }

// --- Action Data Structures ---

// 1. Move between cells
    public class ActionMoveToCell
    {
        [JsonPropertyName("set_id")] public string SetId { get; set; } // Cell Index

        [JsonPropertyName("object_id")] public int ObjectId { get; set; }
    }

// 2. Move between objects (hierarchy)
    public class ActionMoveToObject
    {
        [JsonPropertyName("set_id")] public int SetId { get; set; } // Parent Object Id

        [JsonPropertyName("object_id")] public int ObjectId { get; set; }
    }

// 3. Expand history
    public class ActionExpandHistory
    {
        [JsonPropertyName("object_id")] public int ObjectId { get; set; }

        [JsonPropertyName("new_text")] public string NewText { get; set; }
    }

// 4. Change group description
    public class ActionGroupUpdate
    {
        [JsonPropertyName("cells_id")] public List<string> CellsId { get; set; }

        [JsonPropertyName("new_text")] public string NewText { get; set; }
    }

// --- Final Narrative Response ---
    public class NarrativeResponse
    {
        [JsonPropertyName("text")] public string Text { get; set; }
    }
}