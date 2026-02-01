using Godot;
using RPG.AI;
using RPG.AI.Core;
using RPG.Models;
using RPG.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RPG.Tools
{
    public partial class LocationGeneratorTool : Node, ITool
    {
        private class ObjectsResponseRoot
        {
            [System.Text.Json.Serialization.JsonPropertyName("objects")]
            public List<ObjectData> Objects { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("last_id")]
            public int LastId { get; set; }
        }
        
        [Export] public MapGenerator MapGen;
        [Export] public int Radius = 10; 
        
        public string ToolName => "LocationGeneration";
        
        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;

        public async void Call(string parameters)
        {
            try
            {
                var world = StateManager.Instance.CurrentWorld;
                OnUpdate?.Invoke("📍 Calculating Topology...");

                // --- 1. Find Center & Bounds ---
                GridCoordinate center = GetCenter(world);
                Rect2I bounds = GetWorkingBounds(world, center);
                OnUpdate?.Invoke($"Center: {center.X}:{center.Y}, Bounds: {bounds}");

                // --- 2. Classification (Free vs Occupied) ---
                var (freeCells, occupiedCells) = ClassifyCells(world, bounds);
                var allRelevantIndices = new List<string>(freeCells);
                allRelevantIndices.AddRange(occupiedCells); // Combined list for LMM Context
                
                OnUpdate?.Invoke($"Cells: {freeCells.Count} Free, {occupiedCells.Count} Occupied");

                // --- 3. Generate Pixel Map ---
                OnUpdate?.Invoke("🎨 Drawing Pixel Map...");
                // Pass pre-calculated lists and bounds to the dumb generator
                Image mapImage = await MapGen.GenerateMapImage(freeCells, occupiedCells, bounds);
                
                // Save debug
                mapImage.SavePng("res://generated_map.png");

                // --- 4. Generate Location ---
                OnUpdate?.Invoke("🏗️ Generating Location Data...");
                var locResponse = await GenerateLocationData(parameters, allRelevantIndices, mapImage);
                if (locResponse == null) throw new Exception("Failed to generate Location.");

                // --- 5. Generate Objects ---
                OnUpdate?.Invoke("📦 Generating Objects...");
                var objResponse = await GenerateObjectData(parameters, locResponse);

                // --- 6. Prepare Cell Data ---
                // Создаем объекты CellData из индексов, которые вернула нейросеть в новой локации
                var time = world.GetCurrentWorldTime();
                var newCellIndices = locResponse.CellIndices;
                var newCells = newCellIndices.Select(idx => new CellData { Index = idx, LastUpdateTime = time}).ToList();

                // --- 7. Update Map State & Image ---
                OnUpdate?.Invoke("🎨 Updating Topology Map...");

                // Обновляем списки для генератора: убираем новые клетки из свободных, добавляем в занятые
                foreach (var idx in newCellIndices)
                {
                    if (freeCells.Contains(idx)) freeCells.Remove(idx);
                    if (!occupiedCells.Contains(idx)) occupiedCells.Add(idx);
                }

                // Генерируем новую картинку с учетом изменений
                Image updatedMapImage = await MapGen.GenerateMapImage(freeCells, occupiedCells, bounds);
                updatedMapImage.SavePng("res://generated_map_updated.png");

                // --- 8. Final Response ---
                var result = new ToolResponseContainer
                {
                    ToolName = ToolName,
                    Result = new ToolResultContent
                    {
                        Mutable = new MutableData
                        {
                            Locations = new List<LocationData> { locResponse },
                            Cells = newCells,
                            Objects = objResponse ?? new List<ObjectData>()
                        },
                        Immutable = new ImmutableData
                        {
                            Text = new TextEntry
                            {
                                Text = $"Location created: {locResponse.Description}",
                                Locations = [locResponse.Id]
                            },
                        }
                    }
                };

                OnUpdate?.Invoke("✅ Complete.");
                OnComplete?.Invoke(JsonUtils.Serialize(result));
            }
            catch (Exception ex)
            {
                GD.PrintErr(ex);
                OnFail?.Invoke(JsonUtils.Serialize(new { error = ex.Message }));
            }
        }

        private GridCoordinate GetCenter(WorldState world)
        {
            if (world.Locations.Count == 0) return new GridCoordinate(0, 0, 0);

            int lastActiveId = 0;
            if (world.History.Texts.Count > 0)
            {
                var lastEntry = world.History.Texts.LastOrDefault(t => t.Locations != null && t.Locations.Count > 0);
                if (lastEntry != null) lastActiveId = lastEntry.Locations.Last();
            }

            var loc = world.Locations.FirstOrDefault(l => l.Id == lastActiveId);
            if (loc != null && loc.CellIndices.Count > 0)
            {
                long sumX = 0, sumY = 0;
                int count = 0;
                foreach (var idx in loc.CellIndices)
                {
                    var c = GridCoordinate.Parse(idx);
                    sumX += c.X;
                    sumY += c.Y;
                    count++;
                }
                return new GridCoordinate((int)Math.Round((double)sumX / count), (int)Math.Round((double)sumY / count), 0);
            }

            return new GridCoordinate(0, 0, 0);
        }

        private Rect2I GetWorkingBounds(WorldState world, GridCoordinate center)
        {
            int minX = center.X - Radius;
            int maxX = center.X + Radius;
            int minY = center.Y - Radius;
            int maxY = center.Y + Radius;

            // Expand bounds if overlapping locations exist
            var initialRect = new Rect2I(minX, minY, maxX - minX, maxY - minY);
            
            // Helper local function to parse point
            Vector2I ParseP(string idx) { var c = GridCoordinate.Parse(idx); return new Vector2I(c.X, c.Y); }

            var overlappingLocs = world.Locations.Where(l => 
                l.CellIndices.Any(idx => initialRect.HasPoint(ParseP(idx)))
            ).ToList();

            foreach (var loc in overlappingLocs)
            {
                foreach (var idx in loc.CellIndices)
                {
                    var p = ParseP(idx);
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }

            return new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private (List<string> free, List<string> occupied) ClassifyCells(WorldState world, Rect2I bounds)
        {
            var free = new List<string>();
            var occupied = new List<string>();

            // Optimisation: HashSet for fast lookup
            var allOccupiedInWorld = world.Locations
                .SelectMany(l => l.CellIndices)
                .ToHashSet();

            for (int y = bounds.Position.Y; y < bounds.Position.Y + bounds.Size.Y; y++)
            {
                for (int x = bounds.Position.X; x < bounds.Position.X + bounds.Size.X; x++)
                {
                    string index = $"{x}:{y}:0"; // Ignore Z for map
                    
                    if (allOccupiedInWorld.Contains(index))
                    {
                        occupied.Add(index);
                    }
                    else
                    {
                        free.Add(index);
                    }
                }
            }

            return (free, occupied);
        }

        private async Task<LocationData> GenerateLocationData(string userPrompt, List<string> cells, Image map)
        {
            var req = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.LocationGeneration),
                UserPrompt = $"Request: {userPrompt}\nCells: {JsonUtils.Serialize(cells)}",
                Images = new List<Image> { map },
                Temperature = 0.7f
            };
            
            var json = await LmmFactory.Instance.GetProvider(LmmModelType.Smart).GenerateAsync(req);
            
            if (JsonUtils.TryDeserialize<Dictionary<string, LocationData>>(json, out var root) && root.ContainsKey("location"))
            {
                var loc = root["location"];
                loc.Id = StateManager.Instance.CurrentWorld.GetNextId();
                return loc;
            }
            return null;
        }

        private async Task<List<ObjectData>> GenerateObjectData(string userPrompt, LocationData location)
        {
            var world = StateManager.Instance.CurrentWorld;
            int startId = world.GetNextId();

            string contextData = $@"
User Request: ""{userPrompt}""
Start ID: {startId}

--- CREATED LOCATION STRUCTURE ---
{JsonUtils.Serialize(location)}

--- INSTRUCTIONS ---
1. Analyze the 'groups' in the Location.
2. Extract every physical object mentioned in the group descriptions.
3. Generate an ObjectData entry for each item.
4. Assign IDs starting strictly from {startId}. Increment for each object.
5. Calculate 'last_id' (the value of the ID of the last generated object).
";

            var req = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.ObjectsGeneration),
                UserPrompt = contextData,
                Temperature = 0.7f
            };

            var json = await LmmFactory.Instance.GetProvider(LmmModelType.Smart).GenerateAsync(req);

            if (JsonUtils.TryDeserialize<ObjectsResponseRoot>(json, out var root))
            {
                world.SetNextId(root.LastId + 1);

                return root.Objects;
            }

            GD.PrintErr($"LMM Failed to generate objects JSON: {json}");
            return new List<ObjectData>();
        }
    }
}