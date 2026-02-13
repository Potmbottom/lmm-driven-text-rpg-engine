using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using RPG.Core.Helpers;
using RPG.Models.Simulation;

namespace RPG.Models
{
    public class WorldState
    {
        [JsonPropertyName("meta")] public MetaData Meta { get; set; } = new();

        [JsonPropertyName("next_id")] public int NextId { get; set; } = 0; // Changed to public for serialization

        [JsonPropertyName("locations")] public List<LocationData> Locations { get; set; } = new();

        [JsonPropertyName("objects")] public List<ObjectData> Objects { get; set; } = new();

        [JsonPropertyName("history")] public HistoryData History { get; set; } = new();

        public int GetNextId()
        {
            return NextId++;
        }

        public void SetNextId(int id)
        {
            NextId = id;
        }

        public string GetCurrentWorldTime()
        {
            if (History.Texts.Count == 0) return "day 1, 06:00:00";

            var lastEntry = History.Texts.Last();
            if (lastEntry.Locations.Count == 0) return "day 1, 06:00:00";
            int locId = lastEntry.Locations.First();
            
            var loc = Locations.FirstOrDefault(l => l.Id == locId);
            if (loc == null || loc.GetCellIndices().Count == 0) return "day 1, 06:00:00";

            return loc.LastUpdateTime;
        }
    }

    public class MetaData
    {
        public string Version { get; set; } = "0.1";
        public int VersionInt { get; set; } = 0;
        public string CreatedAt { get; set; }
        public string LastUpdated { get; set; }
    }
    
    public class WorldVersionDelta
    {
        [JsonPropertyName("version_id")] public int VersionId { get; set; }
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; }
        [JsonPropertyName("changes")] public ToolResultContent Changes { get; set; } = new();
    }
    
    public class LocationData
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("groups")] public List<GroupData> Groups { get; set; } = new();
        [JsonPropertyName("last_update_time")] public string LastUpdateTime { get; set; }
    }

    public class GroupData
    {
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("cell_indices")] public List<string> CellIndices { get; set; } = new();
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