using Godot;
using RPG.AI;
using RPG.AI.Core;
using RPG.Core;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RPG.Core.Helpers;

namespace RPG.Tools
{
    public partial class GenerationTool : Node, ITool
    {
        [Export] public MapGenerator MapGen;

        public string ToolName => "Generation";

        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;
        
        public void Call(string parameters)
        {
            CallWithContext([..StateManager.Instance.CurrentWorld.Locations], [..StateManager.Instance.CurrentWorld.Objects], parameters);
        }
        
        public async void CallWithContext(List<LocationData> contextLocations, List<ObjectData> contextObjects, string parameters)
        {
            try
            {
                var request = await ParseInput(parameters);

                switch (request.Type.ToLower())
                {
                    case "location":
                        await HandleLocationGeneration(request, contextLocations, contextObjects);
                        break;
                    case "group":
                        await HandleGroupGeneration(request, contextLocations, contextObjects);
                        break;
                    case "object":
                        await HandleObjectGeneration(request);
                        break;
                    default:
                        OnFail?.Invoke($"Unknown generation type: {request.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr(ex);
                OnFail?.Invoke($"Generation error: {ex.Message}");
            }
        }
        
        private async Task<GenerationRequest> ParseInput(string input)
        {
            if (JsonUtils.TryDeserialize<GenerationRequest>(input, out var result))
            {
                if (!string.IsNullOrEmpty(result.Type) && !string.IsNullOrEmpty(result.Description))
                    return result;
            }

            OnUpdate?.Invoke("🧠 Parsing intent...");
            var prompt = PromptLibrary.Instance.GetPrompt(PromptType.GenerationQueryParser, input);
            var lmm = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            var jsonResponse = await lmm.GenerateAsync(new LmmRequest { UserPrompt = prompt });

            if (JsonUtils.TryDeserialize<GenerationRequest>(jsonResponse, out var aiResult))
                return aiResult;

            throw new FormatException("Failed to parse generation request.");
        }

        private async Task HandleLocationGeneration(GenerationRequest req, List<LocationData> contextLocations, List<ObjectData> contextObjects)
        {
            OnUpdate?.Invoke($"🏗️ Generating Location: {req.Description}...");
            
            string startCell = FindStartingCell(req.Cell, contextLocations);
            var contextMap = await MapGen.GenerateMapAround(startCell, 12, contextLocations, contextLocations.Select(data => data.Id).ToHashSet());
            
            OnUpdate?.Invoke($"📍 Find center {startCell}");
            OnUpdate?.Invoke("📐 Planning topology...");
            
            var topologyPrompt = BuildPrompt(PromptType.GenerationLocation, 
                JsonUtils.Serialize(new { anchor_cell = startCell, description = req.Description }));
            var topology = await ExecuteLmmRequest<LocationData>(topologyPrompt, contextMap);
            
            OnUpdate?.Invoke("🧱 Building world...");
            var structureObjects = await PopulateLocationWithBase(req.Description, contextLocations, contextObjects, topology);
            contextObjects.AddRange(structureObjects);
            var contentObjects = await PopulateLocationWithObjects(req.Description, contextLocations, contextObjects, topology);
            contextObjects.AddRange(contentObjects);
            
            var totalObjects = new List<ObjectData>();
            totalObjects.AddRange(structureObjects);
            totalObjects.AddRange(contextObjects);
            
            OnUpdate?.Invoke("📝 Writing descriptions...");
            var finalGroups = await GenerateGroupDescriptions(topology.Groups, totalObjects);

            var newLocation = new LocationData
            {
                Id = StateManager.Instance.CurrentWorld.GetNextId(),
                Description = topology.Description,
                Groups = finalGroups,
                LastUpdateTime = StateManager.Instance.CurrentWorld.GetCurrentWorldTime()
            };
            
            FinishTurn(new MutableData
            {
                Locations = { newLocation },
                Objects = totalObjects,
            }, $"Created location {newLocation.Id}: {req.Description}");
        }

        private async Task HandleGroupGeneration(GenerationRequest req, List<LocationData> contextLocations, List<ObjectData> contextObjects)
        {
            var targetLoc = req.Id == null ? 
                WorldStateHelper.FindNearestLocation(req.Cell, contextLocations) : 
                contextLocations.FirstOrDefault(l => l.Id == req.Id);

            OnUpdate?.Invoke($"🏗️ Adding Group to Location {targetLoc.Id}...");
            string startCell = FindNeighborCellForLocation(targetLoc, contextLocations);
            var contextMap = await MapGen.GenerateMapAround(startCell, 12, contextLocations, contextLocations.Select(data => data.Id).ToHashSet());
            
            OnUpdate?.Invoke("📐 Planning group topology...");
            var topologyPrompt = BuildPrompt(PromptType.GenerationGroup,
                JsonUtils.Serialize(new { location_id = targetLoc.Id, anchor_cell = startCell, description = req.Description }));
            var topology = await ExecuteLmmRequest<GroupResponse>(topologyPrompt, contextMap);

            OnUpdate?.Invoke("🧱 Furnishing group...");
            targetLoc.Groups.AddRange(topology.Groups);
            var structureObjects = await PopulateLocationWithBase(req.Description, contextLocations, contextObjects, targetLoc);
            contextObjects.AddRange(structureObjects);
            var contentObjects = await PopulateLocationWithObjects(req.Description, contextLocations, contextObjects, targetLoc);

            var allObjects = new List<ObjectData>();
            allObjects.AddRange(structureObjects);
            allObjects.AddRange(contentObjects);

            OnUpdate?.Invoke("📝 Describing group...");
            var describedGroups = await GenerateGroupDescriptions(targetLoc.Groups, allObjects);
            targetLoc.Groups = describedGroups;

            FinishTurn(new MutableData
            {
                Locations = [targetLoc],
                Objects = allObjects
            }, $"Added group to Location {targetLoc.Id}");
        }

        private async Task HandleObjectGeneration(GenerationRequest req)
        {
            OnUpdate?.Invoke($"📦 Generating Object(s): {req.Description}...");
            bool hasCell = !string.IsNullOrEmpty(req.Cell);
            bool hasId = req.Id.HasValue;

            if (hasCell && hasId)
                throw new Exception("Ambiguous request: Both Cell and ID provided. Provide only one context.");
            if (!hasCell && !hasId)
                throw new Exception("Context missing: Provide either Cell or Parent ID.");

            string contextJson = hasCell
                ? JsonUtils.Serialize(new { root_cell = req.Cell })
                : JsonUtils.Serialize(new { parent_object_id = req.Id.Value });

            var prompt = BuildPrompt(PromptType.GenerationObject,
                JsonUtils.Serialize(new
                {
                    description = req.Description, context = contextJson,
                    next_id = StateManager.Instance.CurrentWorld.GetNextId()
                }));

            var response = await ExecuteLmmRequest<ObjectsResponse>(prompt);
            FinishTurn(new MutableData { Objects = response.Objects }, $"Generated objects: {req.Description}");
        }

        private async Task<List<ObjectData>> PopulateLocationWithBase(string description, List<LocationData> locationsContext,
            List<ObjectData> objectsContext, LocationData newLocation)
        {
            var newDescription =
                $"{description}\n Focus Only on BASE objects, floor, walls, windows, doors and etc(except ceiling). Assemble continuous walls in to single object.";
            return await PopulateLocation(newDescription, locationsContext, objectsContext, newLocation);
        }
        
        private async Task<List<ObjectData>> PopulateLocationWithObjects(string description, List<LocationData> locationsContext,
            List<ObjectData> objectsContext, LocationData newLocation)
        {
            var newDescription =
                $"{description}\n Do not generate: floor, walls, windows, doors and etc";
            return await PopulateLocation(newDescription, locationsContext, objectsContext, newLocation);
        }
        
        private async Task<List<ObjectData>> PopulateLocation(string description, List<LocationData> locationsContext,
            List<ObjectData> objectsContext, LocationData newLocation)
        {
            var temp = new List<LocationData>(locationsContext) { newLocation };
            var map = await MapGen.GenerateMap(temp, temp);
            
            var prompt = BuildPrompt(PromptType.GenerationObjects,
                JsonUtils.Serialize(new 
                { 
                    description, 
                    Context = WorldStateHelper.FormatLocationData(locationsContext, objectsContext),
                    LocationToPopulate = WorldStateHelper.FormatLocationData([newLocation],[]),
                    next_id = StateManager.Instance.CurrentWorld.GetNextId(),
                }));

            var result = await ExecuteLmmRequest<ObjectsResponse>(prompt, null);
            
            if (result.Objects.Count > 0)
            {
                int maxId = result.Objects.Max(o => o.Id);
                StateManager.Instance.CurrentWorld.SetNextId(maxId + 1);
            }
            else
            {
                throw new Exception($"No objects generated. \n {result}");
            }
            return result.Objects;
        }

        private async Task<List<GroupData>> GenerateGroupDescriptions(List<GroupData> groups, List<ObjectData> objects)
        {
            if (groups == null || groups.Count == 0) return groups;

            var result = WorldStateHelper.FormatGroupData(groups, objects);
            var prompt = BuildPrompt(PromptType.GenerationGroupDescription, result);
            
            var lmm = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            var jsonResponse = await lmm.GenerateAsync(new LmmRequest { UserPrompt = prompt, Temperature = 0.7f });
            
            if (JsonUtils.TryDeserialize<GroupResponse>(jsonResponse, out var response))
            {
                return response.Groups;
            }

            return groups;
        }

        private string FindStartingCell(string requestedCell, List<LocationData> contextLocations)
        {
            if (!string.IsNullOrEmpty(requestedCell)) return requestedCell;

            var world = StateManager.Instance.CurrentWorld;
            
            // Если переданный контекст локаций пуст
            if (contextLocations == null || contextLocations.Count == 0) return "0:0:0";

            // Пытаемся найти последнюю активную локацию из истории, но проверяем ее наличие в контексте
            if (world.History.Texts.Count > 0)
            {
                var lastText = world.History.Texts.Last();
                if (lastText.Locations.Count > 0)
                {
                    int locId = lastText.Locations.Last();
                    // Ищем локацию в контексте
                    var loc = contextLocations.FirstOrDefault(l => l.Id == locId);
                    if (loc != null) return FindNeighborCellForLocation(loc, contextLocations);
                }
            }

            // Фоллбек: случайная локация из контекста
            var randomLoc = contextLocations[new Random().Next(contextLocations.Count)];
            return FindNeighborCellForLocation(randomLoc, contextLocations);
        }

        private string FindNeighborCellForLocation(LocationData loc, List<LocationData> allContextLocations)
        {
            // Собираем все занятые ячейки из переданного контекста локаций
            // Это важнее, чем world.Cells, так как в цепочке генерации CellData может еще не существовать,
            // но LocationData уже содержит обновленные индексы групп.
            var occupied = allContextLocations
                .SelectMany(l => l.GetCellIndices())
                .ToHashSet();

            var locCells = loc.GetCellIndices();
            
            foreach (var cellIdx in locCells)
            {
                var coord = GridCoordinate.Parse(cellIdx);
                // Проверяем 4 соседей (плоскость)
                var neighbors = new[]
                {
                    new GridCoordinate(coord.X + 1, coord.Y, coord.Z),
                    new GridCoordinate(coord.X - 1, coord.Y, coord.Z),
                    new GridCoordinate(coord.X, coord.Y + 1, coord.Z),
                    new GridCoordinate(coord.X, coord.Y - 1, coord.Z)
                };

                foreach (var n in neighbors)
                {
                    string nIdx = n.ToString();
                    if (!occupied.Contains(nIdx)) return nIdx;
                }
            }

            // Если не нашли свободного соседа (редкий случай), просто отступаем от первой ячейки
            if (locCells.Count > 0)
            {
                var coord = GridCoordinate.Parse(locCells[0]);
                return new GridCoordinate(coord.X + 2, coord.Y + 2, coord.Z).ToString();
            }

            return "0:0:0";
        }

        private string BuildPrompt(PromptType type, string specificJson)
        {
            string rules = PromptLibrary.Instance.GetPrompt(PromptType.GenerationRules);
            string task = PromptLibrary.Instance.GetPrompt(type, specificJson);
            return $"{rules}\n\n{task}";
        }

        private async Task<T> ExecuteLmmRequest<T>(string fullPrompt, Image contextImage = null)
        {
            var request = new LmmRequest
            {
                UserPrompt = fullPrompt,
                Temperature = 0.4f, 
                Images = contextImage != null ? new List<Image> { contextImage } : null
            };

            var provider = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            string json = await provider.GenerateAsync(request);

            if (JsonUtils.TryDeserialize<T>(json, out var result))
            {
                return result;
            }
            
            GD.PrintErr($"JSON Parse Failed. Response: {json}");
            return default;
        }

        private void FinishTurn(MutableData data, string logResult)
        {
            var result = new ToolResultContent
            {
                Mutable = data,
                Immutable = new ImmutableData
                {
                    Text = new TextEntry { Text = $"Generation Tool: {logResult}" }
                }
            };
            
            string output = JsonUtils.Serialize(new ToolResponseContainer 
            { 
                ToolName = ToolName, 
                Result = result 
            });
            
            OnComplete?.Invoke(output);
        }
    }
}