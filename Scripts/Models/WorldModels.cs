using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using RPG.Models.Simulation;

namespace RPG.Models
{
    // --- Existing Classes ---
    public class WorldState
    {
        [JsonPropertyName("meta")] public MetaData Meta { get; set; } = new();

        [JsonPropertyName("next_id")] public int NextId { get; set; } = 0; // Changed to public for serialization

        [JsonPropertyName("locations")] public List<LocationData> Locations { get; set; } = new();

        [JsonPropertyName("cells")] public List<CellData> Cells { get; set; } = new();

        [JsonPropertyName("objects")] public List<ObjectData> Objects { get; set; } = new();

        [JsonPropertyName("history")] public HistoryData History { get; set; } = new();

        public int GetNextId()
        {
            NextId++;
            return NextId;
        }

        public void SetNextId(int id)
        {
            NextId = id;
        }

        public string GetCurrentWorldTime()
        {
             return GetCurrentWorldTime(Locations, Cells);
        }

        public string GetCurrentWorldTime(List<LocationData> locations, List<CellData> cells)
        {
            if (History.Texts.Count == 0) return "day 1, 09:00";

            var lastEntry = History.Texts.Last();
            if (lastEntry.Locations == null || lastEntry.Locations.Count == 0) return "day 1, 09:00";
            int locId = lastEntry.Locations.First();
            
            var loc = locations.FirstOrDefault(l => l.Id == locId);
            if (loc == null || loc.CellIndices.Count == 0) return "day 1, 09:00";

            string firstCellIndex = loc.CellIndices.First();
            var cell = cells.FirstOrDefault(c => c.Index == firstCellIndex);

            return cell?.LastUpdateTime ?? "day 1, 09:00";
        }
    }

    public class MetaData
    {
        public string Version { get; set; } = "3.3"; // Bumped version
        public int VersionInt { get; set; } = 0; // Incremental Database Version
        public string CreatedAt { get; set; }
        public string LastUpdated { get; set; }
    }

    // --- NEW: Delta Structure for Versioning ---
    public class WorldVersionDelta
    {
        [JsonPropertyName("version_id")] public int VersionId { get; set; }
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; }
        
        // Храним изменения, которые были применены в этом коммите
        [JsonPropertyName("changes")] public ToolResultContent Changes { get; set; } = new();
    }
    
    // ... (Остальные классы LocationData, GroupData, CellData, ObjectData, HistoryData, TextEntry остаются без изменений) ...
    public class LocationData
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("groups")] public List<GroupData> Groups { get; set; } = new();
        public List<string> CellIndices => Groups.SelectMany(data => data.CellIndices).ToList();
    }

    public class GroupData
    {
        public string Description { get; set; }
        public List<string> CellIndices { get; set; } = new();
    }

    public class CellData
    {
        [JsonPropertyName("index")] public string Index { get; set; } 
        [JsonPropertyName("links")] public List<string> Links { get; set; } = new();
        [JsonPropertyName("last_update_time")] public string LastUpdateTime { get; set; }
    }

    public class ObjectData
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("parent_object_id")] public int? ParentObjectId { get; set; }
        [JsonPropertyName("cell_indices")] public List<string> CellIndices { get; set; }
        [JsonPropertyName("history")] public List<string> History { get; set; } = new();
    }

    public class HistoryData
    {
        public List<TextEntry> Texts { get; set; } = new();
    }

    public class TextEntry
    {
        public string Text { get; set; }
        public List<int> Locations { get; set; } = new();
        public List<SimulationResponse> SimulationLog { get; set; } = new();
    }
}