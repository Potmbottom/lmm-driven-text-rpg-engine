using Godot;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RPG.Core
{
    public partial class StateManager : Node
    {
        public static StateManager Instance { get; private set; }
        public WorldState CurrentWorld { get; private set; }
        private const string DB_ROOT = "res://Database/";
        private const string VERSIONS_DIR = "res://Database/versions/";
        private const string SNAPSHOT_PATH = "res://Database/snapshot.json";

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

            // Rebuild Vector DB state from loaded world
            await VectorDB.RebuildDatabase(CurrentWorld);
        }

        public async void ApplyChanges(List<string> aggregatedHistory)
        {
            GD.Print("💾 Processing Commit...");
            var consolidatedChanges = new ToolResultContent();
            bool hasAnyChanges = false;

            for (int i = 1; i < aggregatedHistory.Count; i++)
            {
                try
                {
                    var response = JsonUtils.Deserialize<ToolResponseContainer>(aggregatedHistory[i]);
                    if (response == null || response.Result == null) continue;

                    if (response.Result.Mutable != null)
                    {
                        consolidatedChanges.Mutable.Locations.AddRange(response.Result.Mutable.Locations);
                        consolidatedChanges.Mutable.Objects.AddRange(response.Result.Mutable.Objects);
                        consolidatedChanges.Mutable.Cells.AddRange(response.Result.Mutable.Cells);
                    }

                    if (response.Result.Immutable?.Text != null &&
                        !string.IsNullOrEmpty(response.Result.Immutable.Text.Text))
                    {
                        consolidatedChanges.Immutable.Text = response.Result.Immutable.Text;
                    }

                    hasAnyChanges = true;
                }
                catch
                {
                    /* Ignore parse errors */
                }
            }

            if (!hasAnyChanges) return;

            int newVersion = CurrentWorld.Meta.VersionInt + 1;
            string timestamp = CurrentWorld.GetCurrentWorldTime(); // Берем актуальное время мира

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

        private async System.Threading.Tasks.Task ApplyDeltaToState(ToolResultContent changes)
        {
            // Mutable (Upsert)
            if (changes.Mutable != null)
            {
                foreach (var newLoc in changes.Mutable.Locations)
                {
                    int index = CurrentWorld.Locations.FindIndex(l => l.Id == newLoc.Id);
                    if (index != -1) CurrentWorld.Locations[index] = newLoc;
                    else CurrentWorld.Locations.Add(newLoc);

                    // Update Vector DB (Locations)
                    string groupsDesc = string.Join("; ", newLoc.Groups.Select(g => g.Description));
                    string fullDesc = $"[Location] {newLoc.Description}. Groups: {groupsDesc}";

                    await VectorDB.UpdateLocation(newLoc.Id, fullDesc);
                }

                foreach (var newObj in changes.Mutable.Objects)
                {
                    int index = CurrentWorld.Objects.FindIndex(o => o.Id == newObj.Id);
                    if (index != -1) CurrentWorld.Objects[index] = newObj;
                    else CurrentWorld.Objects.Add(newObj);

                    // Update Vector DB (Objects)
                    string desc = $"[Object] History: {string.Join("; ", newObj.History)}";
                    await VectorDB.UpdateObject(newObj.Id, desc);
                }

                foreach (var newCell in changes.Mutable.Cells)
                {
                    int index = CurrentWorld.Cells.FindIndex(c => c.Index == newCell.Index);
                    if (index != -1) CurrentWorld.Cells[index] = newCell;
                    else CurrentWorld.Cells.Add(newCell);
                }
            }

            // Immutable (Append)
            if (changes.Immutable?.Text != null && !string.IsNullOrEmpty(changes.Immutable.Text.Text))
            {
                CurrentWorld.History.Texts.Add(changes.Immutable.Text);

                int newIndex = CurrentWorld.History.Texts.Count - 1;
                await VectorDB.AddEvent(newIndex, changes.Immutable.Text.Text);
            }
        }

        public async void RollbackOneVersion()
        {
            int currentVersion = CurrentWorld.Meta.VersionInt;
            if (currentVersion <= 0) return;

            GD.Print($"⏪ Rolling back from v{currentVersion} to v{currentVersion - 1}...");

            string lastDeltaPath = $"{VERSIONS_DIR}v_{currentVersion}.json";
            if (FileAccess.FileExists(lastDeltaPath)) DirAccess.RemoveAbsolute(lastDeltaPath);

            string originalCreatedAt = CurrentWorld.Meta.CreatedAt;
            CurrentWorld = new WorldState();
            CurrentWorld.Meta.CreatedAt = originalCreatedAt;

            // Replay
            int targetVersion = currentVersion - 1;
            for (int i = 1; i <= targetVersion; i++)
            {
                string path = $"{VERSIONS_DIR}v_{i}.json";
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

            // Full Rebuild of Vectors after Rollback
            await VectorDB.RebuildDatabase(CurrentWorld);

            GD.Print($"✅ Rollback complete. Version: {CurrentWorld.Meta.VersionInt}");
        }

        private void ApplyDeltaToStateMemoryOnly(ToolResultContent changes)
        {
            if (changes.Mutable != null)
            {
                foreach (var l in changes.Mutable.Locations)
                {
                    int i = CurrentWorld.Locations.FindIndex(x => x.Id == l.Id);
                    if (i != -1) CurrentWorld.Locations[i] = l;
                    else CurrentWorld.Locations.Add(l);
                }

                foreach (var o in changes.Mutable.Objects)
                {
                    int i = CurrentWorld.Objects.FindIndex(x => x.Id == o.Id);
                    if (i != -1) CurrentWorld.Objects[i] = o;
                    else CurrentWorld.Objects.Add(o);
                }

                foreach (var c in changes.Mutable.Cells)
                {
                    int i = CurrentWorld.Cells.FindIndex(x => x.Index == c.Index);
                    if (i != -1) CurrentWorld.Cells[i] = c;
                    else CurrentWorld.Cells.Add(c);
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
            string path = $"{VERSIONS_DIR}v_{delta.VersionId}.json";
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
            int maxId = 0;
            if (CurrentWorld.Locations.Count > 0) maxId = Math.Max(maxId, CurrentWorld.Locations.Max(l => l.Id));
            if (CurrentWorld.Objects.Count > 0) maxId = Math.Max(maxId, CurrentWorld.Objects.Max(o => o.Id));
            CurrentWorld.SetNextId(maxId + 1);
        }
    }
}