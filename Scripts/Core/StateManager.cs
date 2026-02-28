using Godot;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RPG.AI;
using RPG.AI.Core;
using RPG.AI.Providers;
using RPG.Core.Helpers;

namespace RPG.Core
{
    public partial class StateManager : Node
    {
        public static StateManager Instance { get; private set; }
        public WorldState CurrentWorld { get; private set; }
        private const string DB_ROOT = "res://Database/";
        private const string VERSIONS_DIR = "res://Database/versions/";
        private const string SNAPSHOT_PATH = "res://Database/snapshot.json";
        
        private const int HISTORY_COMPRESSION_THRESHOLD_CHARS = 1500;
        private const int HISTORY_COMPRESSION_THRESHOLD_COUNT = 6;

        public VectorDatabase VectorDB;

        public override void _Ready()
        {
            Instance = this;
            EnsureDirectoriesExist();

            VectorDB = GetNodeOrNull<VectorDatabase>("VectorDatabase");
            if (VectorDB == null)
            {
                VectorDB = new VectorDatabase();
                VectorDB.Name = "VectorDatabase";
                AddChild(VectorDB);
            }

            LoadWorld();
        }

        public async void LoadWorld()
        {
            if (!FileAccess.FileExists(SNAPSHOT_PATH))
            {
                CreateNewWorld();
                return;
            }

            using var file = FileAccess.Open(SNAPSHOT_PATH, FileAccess.ModeFlags.Read);
            CurrentWorld = JsonUtils.Deserialize<WorldState>(file.GetAsText());

            GD.Print($"🌍 World Loaded. Version: {CurrentWorld.Meta.VersionInt}");
            await VectorDB.RebuildDatabase(CurrentWorld);
        }

        public async void ApplyChanges(List<string> aggregatedHistory)
        {
            GD.Print("💾 Processing Commit...");
            var consolidatedChanges = new ToolResultContent();
            var hasAnyChanges = false;

            for (var i = 1; i < aggregatedHistory.Count; i++)
            {
                var response = JsonUtils.Deserialize<ToolResponseContainer>(aggregatedHistory[i]);
                if (response == null || response.Result == null) continue;

                if (response.Result.Mutable != null)
                {
                    consolidatedChanges.Mutable.Locations.AddRange(response.Result.Mutable.Locations);
                    consolidatedChanges.Mutable.Objects.AddRange(response.Result.Mutable.Objects);
                }

                if (response.Result.Immutable?.Text != null &&
                    !string.IsNullOrEmpty(response.Result.Immutable.Text.Text))
                {
                    consolidatedChanges.Immutable.Text = response.Result.Immutable.Text;
                }

                hasAnyChanges = true;
            }

            if (!hasAnyChanges) return;

            var newVersion = CurrentWorld.Meta.VersionInt + 1;
            var timestamp = WorldStateHelper.GetCurrentWorldTime(CurrentWorld.Locations);

            var versionDelta = new WorldVersionDelta
            {
                VersionId = newVersion,
                Timestamp = timestamp,
                Changes = consolidatedChanges
            };

            SaveDeltaFile(versionDelta);

            await ApplyDeltaToState(consolidatedChanges);

            VectorDB.SaveCache();

            CurrentWorld.Meta.VersionInt = newVersion;
            CurrentWorld.Meta.LastUpdated = timestamp;
            SaveSnapshot();

            GD.Print($"✅ Commit complete. Version {newVersion} saved.");
        }

        private async Task ApplyDeltaToState(ToolResultContent changes)
        {
            if (changes.Mutable != null)
            {
                foreach (var newLoc in changes.Mutable.Locations)
                {
                    var index = CurrentWorld.Locations.FindIndex(l => l.Id == newLoc.Id);
                    if (index != -1) CurrentWorld.Locations[index] = newLoc;
                    else CurrentWorld.Locations.Add(newLoc);
                    var groupsDesc = string.Join("; ", newLoc.Groups.Select(g => g.Description));
                    var fullDesc = $"[Location] {newLoc.Description}. Groups: {groupsDesc}";

                    await VectorDB.UpdateLocation(newLoc.Id, fullDesc);
                }
                
                var objectTasks = new List<Task>();

                foreach (var newObj in changes.Mutable.Objects)
                {
                    objectTasks.Add(ProcessObjectUpsert(newObj));
                }
                
                if (objectTasks.Count > 0)
                {
                    await Task.WhenAll(objectTasks);
                }
            }
            
            if (changes.Immutable?.Text != null && !string.IsNullOrEmpty(changes.Immutable.Text.Text))
            {
                CurrentWorld.History.Texts.Add(changes.Immutable.Text);

                var newIndex = CurrentWorld.History.Texts.Count - 1;
                await VectorDB.AddEvent(newIndex, changes.Immutable.Text.Text);
            }
        }
        
        private async Task ProcessObjectUpsert(ObjectData newObj)
        {
            await CompressHistoryIfNeeded(newObj);
            var index = CurrentWorld.Objects.FindIndex(o => o.Id == newObj.Id);
            if (index != -1) CurrentWorld.Objects[index] = newObj;
            else CurrentWorld.Objects.Add(newObj);
            var desc = $"[Object] History: {string.Join("; ", newObj.History)}";
            await VectorDB.UpdateObject(newObj.Id, desc);
        }
        
        private async Task CompressHistoryIfNeeded(ObjectData obj)
        {
            if (obj.History is not { Count: > 1 }) return;
            
            var totalChars = obj.History.Skip(1).Sum(s => s?.Length ?? 0);
            var needsCompression = (obj.History.Count >= HISTORY_COMPRESSION_THRESHOLD_COUNT) || 
                                   (totalChars >= HISTORY_COMPRESSION_THRESHOLD_CHARS);
            if (!needsCompression) return;

            GD.Print($"🗜️ Compressing history for Object ID {obj.Id}. Entries: {obj.History.Count}, Chars: {totalChars}");

            var rawHistory = string.Join("\n", obj.History);
            var prompt = PromptLibrary.Instance.GetPrompt(PromptType.HistoryCompressor, rawHistory);
            var request = new LmmRequest
            {
                UserPrompt = prompt,
                SystemInstruction = "You are a precise data compressor. Keep all timestamps.",
                Temperature = 0.4f,
                ThinkingLevel = GeminiThinkingLevel.medium
            };
            
            var provider = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            var compressedResult = "START OF MEMORY SNAPSHOT\n";
            compressedResult += await provider.GenerateAsync(request);
            compressedResult += "\nEND OF MEMORY SNAPSHOT";
            
            obj.History.Clear();
            obj.History.Add(compressedResult.Trim());
            GD.Print($"✅ Compression success for Object ID {obj.Id}. New Length: {compressedResult.Length}");
        }

        public async void RollbackOneVersion()
        {
            var currentVersion = CurrentWorld.Meta.VersionInt;
            if (currentVersion <= 0) return;

            GD.Print($"⏪ Rolling back from v{currentVersion} to v{currentVersion - 1}...");

            var lastDeltaPath = $"{VERSIONS_DIR}v_{currentVersion}.json";
            if (FileAccess.FileExists(lastDeltaPath)) DirAccess.RemoveAbsolute(lastDeltaPath);

            var originalCreatedAt = CurrentWorld.Meta.CreatedAt;
            CurrentWorld = new WorldState();
            CurrentWorld.Meta.CreatedAt = originalCreatedAt;
            
            var targetVersion = currentVersion - 1;
            for (var i = 1; i <= targetVersion; i++)
            {
                var path = $"{VERSIONS_DIR}v_{i}.json";
                if (FileAccess.FileExists(path))
                {
                    using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                    var delta = JsonUtils.Deserialize<WorldVersionDelta>(file.GetAsText());
                    if (delta != null)
                    {
                        ApplyDeltaToStateMemoryOnly(delta.Changes);
                        CurrentWorld.Meta.LastUpdated = delta.Timestamp;
                    }
                }
            }

            CurrentWorld.Meta.VersionInt = targetVersion;
            RecalculateNextId();
            SaveSnapshot();
            await VectorDB.RebuildDatabase(CurrentWorld);

            GD.Print($"✅ Rollback complete. Version: {CurrentWorld.Meta.VersionInt}");
        }

        private void ApplyDeltaToStateMemoryOnly(ToolResultContent changes)
        {
            if (changes.Mutable != null)
            {
                foreach (var l in changes.Mutable.Locations)
                {
                    var i = CurrentWorld.Locations.FindIndex(x => x.Id == l.Id);
                    if (i != -1) CurrentWorld.Locations[i] = l;
                    else CurrentWorld.Locations.Add(l);
                }

                foreach (var o in changes.Mutable.Objects)
                {
                    var i = CurrentWorld.Objects.FindIndex(x => x.Id == o.Id);
                    if (i != -1) CurrentWorld.Objects[i] = o;
                    else CurrentWorld.Objects.Add(o);
                }
            }

            if (changes.Immutable?.Text != null)
            {
                CurrentWorld.History.Texts.Add(changes.Immutable.Text);
            }
        }

        private void EnsureDirectoriesExist()
        {
            if (!DirAccess.DirExistsAbsolute(DB_ROOT)) DirAccess.MakeDirAbsolute(DB_ROOT);
            if (!DirAccess.DirExistsAbsolute(VERSIONS_DIR)) DirAccess.MakeDirAbsolute(VERSIONS_DIR);
        }

        private void SaveDeltaFile(WorldVersionDelta delta)
        {
            var path = $"{VERSIONS_DIR}v_{delta.VersionId}.json";
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            file.StoreString(JsonUtils.Serialize(delta));
        }

        private void SaveSnapshot()
        {
            using var file = FileAccess.Open(SNAPSHOT_PATH, FileAccess.ModeFlags.Write);
            file.StoreString(JsonUtils.Serialize(CurrentWorld));
        }

        private void CreateNewWorld()
        {
            CurrentWorld = new WorldState();
            CurrentWorld.Meta.CreatedAt = Time.GetDatetimeStringFromSystem();
            CurrentWorld.Meta.LastUpdated = CurrentWorld.Meta.CreatedAt;
            CurrentWorld.Meta.VersionInt = 0;
            SaveSnapshot();
        }

        private void RecalculateNextId()
        {
            var maxId = 0;
            if (CurrentWorld.Locations.Count > 0) maxId = Math.Max(maxId, CurrentWorld.Locations.Max(l => l.Id));
            if (CurrentWorld.Objects.Count > 0) maxId = Math.Max(maxId, CurrentWorld.Objects.Max(o => o.Id));
            CurrentWorld.SetNextId(maxId + 1);
        }
    }
}