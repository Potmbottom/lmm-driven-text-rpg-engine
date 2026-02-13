using Godot;
using RPG.AI.Core;
using RPG.AI;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace RPG.Core
{
    public class VectorSearchResult
    {
        public int Id;
        public string Content;
        public float Similarity;
        public float HybridScore;
        public SearchType Type;
        public string LastUpdateTime;
    }

    public enum SearchType
    {
        Location,
        Object,
        Event
    }

    public partial class VectorDatabase : Node
    {
        public static VectorDatabase Instance { get; private set; }

        private const string CACHE_PATH = "res://Database/vectors_cache.json";

        // --- Runtime Storage (In-Memory Index) ---
        private Dictionary<int, float[]> _locationVectors = new();
        private Dictionary<int, float[]> _objectVectors = new();
        private Dictionary<int, float[]> _eventVectors = new();

        // --- Disk Cache (Persistent Storage: Hash -> Vector) ---
        private Dictionary<string, float[]> _vectorCache = new();

        public override void _Ready()
        {
            Instance = this;
            LoadCache();
        }

        // --- Public API ---

        public async Task UpdateLocation(int id, string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return;

            var result = await GetVectorWithCache(description);
            if (result.Vector != null)
            {
                _locationVectors[id] = result.Vector;
            }
        }

        public async Task UpdateObject(int id, string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return;

            var result = await GetVectorWithCache(description);
            if (result.Vector != null)
            {
                _objectVectors[id] = result.Vector;
            }
        }

        public async Task AddEvent(int index, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var result = await GetVectorWithCache(text);
            if (result.Vector != null)
            {
                _eventVectors[index] = result.Vector;
            }
        }

        public async Task RebuildDatabase(WorldState world)
        {
            GD.Print("🧠 Rebuilding Vector Database...");
            _locationVectors.Clear();
            _objectVectors.Clear();
            _eventVectors.Clear();

            HashSet<string> activeHashes = new HashSet<string>();

            foreach (var loc in world.Locations)
            {
                string context = FormatLocationText(loc);
                var result = await GetVectorWithCache(context);
                if (result.Vector != null)
                {
                    _locationVectors[loc.Id] = result.Vector;
                    activeHashes.Add(result.Hash);
                }
            }

            foreach (var obj in world.Objects)
            {
                string context = FormatObjectText(obj);
                var result = await GetVectorWithCache(context);
                if (result.Vector != null)
                {
                    _objectVectors[obj.Id] = result.Vector;
                    activeHashes.Add(result.Hash);
                }
            }

            for (int i = 0; i < world.History.Texts.Count; i++)
            {
                string text = world.History.Texts[i].Text;
                var result = await GetVectorWithCache(text);
                if (result.Vector != null)
                {
                    _eventVectors[i] = result.Vector;
                    activeHashes.Add(result.Hash);
                }
            }

            PruneCache(activeHashes);
            SaveCache();
            GD.Print($"🧠 Rebuild Complete. Cache Size: {_vectorCache.Count}. Locs: {_locationVectors.Count}, Objs: {_objectVectors.Count}");
        }

        private void PruneCache(HashSet<string> activeHashes)
        {
            int initialCount = _vectorCache.Count;
            var keysToRemove = _vectorCache.Keys.Where(k => !activeHashes.Contains(k)).ToList();

            foreach (var key in keysToRemove)
            {
                _vectorCache.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                GD.Print($"🧹 Vector Cache Pruned: Removed {keysToRemove.Count} orphaned vectors. ({initialCount} -> {_vectorCache.Count})");
            }
        }

        // MODIFIED: Added allowedIds parameter for filtering
        public async Task<List<VectorSearchResult>> Search(string query, SearchType type, int limit = 5, List<int> allowedIds = null)
        {
            var result = await GetVectorWithCache(query);
            var queryVector = result.Vector;

            if (queryVector == null) return new List<VectorSearchResult>();

            var results = new List<VectorSearchResult>();
            var world = StateManager.Instance.CurrentWorld;
            string currentWorldTime = world.GetCurrentWorldTime();

            if (type == SearchType.Location)
            {
                foreach (var kvp in _locationVectors)
                {
                    int id = kvp.Key;
                    
                    // Filter check
                    if (allowedIds != null && allowedIds.Count > 0 && !allowedIds.Contains(id)) continue;

                    var loc = world.Locations.FirstOrDefault(l => l.Id == id);
                    if (loc == null) continue;

                    float sim = CosineSimilarity(queryVector, kvp.Value);
                    string fullContent = FormatLocationText(loc);
                    string time = loc.LastUpdateTime;

                    results.Add(new VectorSearchResult
                    {
                        Id = id,
                        Type = SearchType.Location,
                        Similarity = sim,
                        Content = fullContent,
                        LastUpdateTime = time,
                        HybridScore = CalculateHybridScore(sim, time, currentWorldTime)
                    });
                }
            }
            else if (type == SearchType.Object)
            {
                foreach (var kvp in _objectVectors)
                {
                    int id = kvp.Key;

                    // Filter check
                    if (allowedIds != null && allowedIds.Count > 0 && !allowedIds.Contains(id)) continue;

                    var obj = world.Objects.FirstOrDefault(o => o.Id == id);
                    if (obj == null) continue;

                    float sim = CosineSimilarity(queryVector, kvp.Value);
                    string fullContent = FormatObjectText(obj);
                    string time = GetObjectTimeRecursive(obj, world);

                    results.Add(new VectorSearchResult
                    {
                        Id = id,
                        Type = SearchType.Object,
                        Similarity = sim,
                        Content = fullContent,
                        LastUpdateTime = time,
                        HybridScore = CalculateHybridScore(sim, time, currentWorldTime)
                    });
                }
            }
            else if (type == SearchType.Event)
            {
                foreach (var kvp in _eventVectors)
                {
                    int index = kvp.Key;

                    // Filter check
                    if (allowedIds != null && allowedIds.Count > 0 && !allowedIds.Contains(index)) continue;

                    if (index >= world.History.Texts.Count) continue;

                    var entry = world.History.Texts[index];
                    float sim = CosineSimilarity(queryVector, kvp.Value);
                    string eventTime = GetEventTime(entry, world);

                    results.Add(new VectorSearchResult
                    {
                        Id = index,
                        Type = SearchType.Event,
                        Similarity = sim,
                        Content = entry.Text,
                        LastUpdateTime = eventTime,
                        HybridScore = CalculateHybridScore(sim, eventTime, currentWorldTime)
                    });
                }
            }

            return results.OrderByDescending(r => r.HybridScore).Take(limit).ToList();
        }

        // --- Helper Methods ---

        private string FormatLocationText(LocationData loc)
        {
            var groupsDesc = string.Join("; ", loc.Groups.Select(g => g.Description));
            return $"[Location ID: {loc.Id}] Description: {loc.Description}. Groups: {groupsDesc}";
        }

        private string FormatObjectText(ObjectData obj)
        {
            string hist = obj.History.Count > 0 ? string.Join("; ", obj.History) : "No history";
            return $"[Object ID: {obj.Id}] History: {hist}";
        }

        private string GetObjectTimeRecursive(ObjectData obj, WorldState world)
        {
            return "day 1, 00:00"; // Placeholder implementation as in source
        }

        private string GetEventTime(TextEntry entry, WorldState world)
        {
            return "day 1, 00:00"; // Placeholder implementation as in source
        }

        private float CalculateHybridScore(float similarity, string itemTime, string worldTime)
        {
            long tItem = TimeHelper.ParseToSeconds(itemTime);
            long tWorld = TimeHelper.ParseToSeconds(worldTime);
            long diff = Math.Abs(tWorld - tItem);
            const float MAX_DIFF_MINUTES = 259200;
            float timeFactor = 1.0f - Math.Clamp(diff / MAX_DIFF_MINUTES, 0f, 1f);
            return (similarity * 0.7f) + (timeFactor * 0.3f);
        }

        private async Task<(float[] Vector, string Hash)> GetVectorWithCache(string text)
        {
            string hash = ComputeHash(text);
            if (_vectorCache.TryGetValue(hash, out float[] cachedVec))
            {
                return (cachedVec, hash);
            }

            var provider = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            var newVec = await provider.GetEmbeddingAsync(text);

            if (newVec != null) _vectorCache[hash] = newVec;
            return (newVec, hash);
        }

        public void SaveCache()
        {
            string json = JsonUtils.Serialize(_vectorCache);
            using var file = FileAccess.Open(CACHE_PATH, FileAccess.ModeFlags.Write);
            file.StoreString(json);
        }

        private void LoadCache()
        {
            if (!FileAccess.FileExists(CACHE_PATH)) return;
            using var file = FileAccess.Open(CACHE_PATH, FileAccess.ModeFlags.Read);
            string json = file.GetAsText();
            var data = JsonUtils.Deserialize<Dictionary<string, float[]>>(json);
            if (data != null) _vectorCache = data;
        }

        private string ComputeHash(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                return Convert.ToHexString(hashBytes);
            }
        }

        private float CosineSimilarity(float[] vecA, float[] vecB)
        {
            if (vecA == null || vecB == null || vecA.Length != vecB.Length) return 0f;
            float dot = 0f, magA = 0f, magB = 0f;
            for (int i = 0; i < vecA.Length; i++)
            {
                dot += vecA[i] * vecB[i];
                magA += vecA[i] * vecA[i];
                magB += vecB[i] * vecB[i];
            }

            if (magA == 0 || magB == 0) return 0f;
            return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
        }
    }
}