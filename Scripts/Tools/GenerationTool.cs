using Godot;
using RPG.AI;
using RPG.AI.Core;
using RPG.Core;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RPG.AI.Providers;
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
                    case "group":
                        await HandleLocationGeneration(request, contextLocations, contextObjects);
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
            var jsonResponse = await lmm.GenerateAsync(new LmmRequest { UserPrompt = prompt, ThinkingLevel = GeminiThinkingLevel.low});

            if (JsonUtils.TryDeserialize<GenerationRequest>(jsonResponse, out var aiResult))
                return aiResult;

            throw new FormatException("Failed to parse generation request.");
        }
        
        private async Task HandleLocationGeneration(GenerationRequest req, List<LocationData> contextLocations, List<ObjectData> contextObjects)
        {
            var locations = new List<LocationData>();
            if (req.Type == "group")
            {
                var targetLoc = req.Id == null ? 
                    WorldStateHelper.FindNearestLocation(req.TargetCells.First(), contextLocations) : 
                    contextLocations.FirstOrDefault(l => l.Id == req.Id);
                locations.Add(targetLoc);
            }            
            var data = await FillGenerationInput(locations, req, contextLocations);
            var locationsContext = WorldStateHelper.FormatLocationData(data.locations, contextObjects);
            var topologyPrompt = BuildPrompt(PromptType.GenerationLocation,
                $"Target cell: {string.Join(" ", data.cells)} \n Location context: {locationsContext} \n Generation description: {req.Description}");
            var topology = await ExecuteLmmRequest<LocationData>(topologyPrompt, data.contextMap);
            if (req.Type == "location")
            {
                topology.Id = StateManager.Instance.CurrentWorld.GetNextId();
                topology.LastUpdateTime = WorldStateHelper.GetCurrentWorldTime(contextLocations);
            }
            
            var structureObjects = await PopulateLocation(req.Description, contextLocations, contextObjects, topology);
            await AssignKeysToEntities(new List<LocationData> { topology }, structureObjects);

            if (req.Type == "group")
            {
                topology.Groups.AddRange(locations[0].Groups);
                topology.Id = locations[0].Id;
                topology.LastUpdateTime = locations[0].LastUpdateTime;
            }
            
            FinishTurn(new MutableData
                {
                    Locations = { topology },
                    Objects = structureObjects,
                }, $"Created {req.Type} Id: {topology.Id}: {req.Description}");
        }
        
        private async Task<(Image contextMap, List<LocationData> locations, List<string> cells)> 
            FillGenerationInput(List<LocationData> existLocations, GenerationRequest req, List<LocationData> contextLocations)
        {
            Image contextMap;
            var cells = new List<string>();
            if (req.TargetCells?.Count == 0 && req.TargetLocations?.Count == 0)
            {
                var tuple = FindStartingCell(contextLocations);
                existLocations.AddRange(contextLocations.Where(data => tuple.LocationId.Contains(data.Id)));
                var active = contextLocations == null || contextLocations.Count == 0
                    ? []
                    : contextLocations.Select(data => data.Id).ToHashSet();
                contextMap = await MapGen.GenerateMapAround(tuple.Cell, 12, contextLocations, active);
            }
            else
            {
                if (req is { TargetLocations.Count: > 0 })
                {
                    existLocations.AddRange(contextLocations.Where(data => req.TargetLocations.Contains(data.Id)));
                }

                if (req is { TargetCells.Count: > 0 })
                {
                    cells.AddRange(req.TargetCells);
                }
                contextMap = await MapGen.GenerateMap(existLocations, contextLocations, cells);   
            }
            
            return (contextMap, existLocations, cells);
        }

        private async Task HandleObjectGeneration(GenerationRequest req)
        {
            OnUpdate?.Invoke($"📦 Generating Object(s): {req.Description}...");
            var hasCell = req.TargetCells.Count > 0 && !string.IsNullOrEmpty(req.TargetCells.First());
            var hasId = req.Id.HasValue;

            if (hasCell && hasId)
                throw new Exception("Ambiguous request: Both Cell and ID provided. Provide only one context.");
            if (!hasCell && !hasId)
                throw new Exception("Context missing: Provide either Cell or Parent ID.");

            var contextJson = hasCell ? $"Root cell: {req.TargetCells.First()}\n" : $"Parent object id: {req.Id}";
            var prompt = BuildPrompt(PromptType.GenerationObject,
                $"Generation description: {req.Description}\nContext: {contextJson}\nNext id: {StateManager.Instance.CurrentWorld.GetNextId()}");

            var response = await ExecuteLmmRequest<ObjectsResponse>(prompt);
            await AssignKeysToEntities(new List<LocationData>(), response.Objects);
            FinishTurn(new MutableData { Objects = response.Objects }, $"Generated objects: {response.Objects.Count}");
        }
        
        private async Task AssignKeysToEntities(List<LocationData> locs, List<ObjectData> objs)
        {
            if (!locs.Any() && !objs.Any()) return;
            
            OnUpdate?.Invoke("🏷️ Assigning Identity Keys...");
            var sb = new StringBuilder();
            foreach(var l in locs) sb.AppendLine($"LOC:{l.Id} {l.Description}");
            foreach(var o in objs) sb.AppendLine($"OBJ:{o.Id} {string.Join(" ", o.History)}");

            var prompt = PromptLibrary.Instance.GetPrompt(PromptType.GenerationKeys, sb);
            
            var req = new LmmRequest { UserPrompt = prompt, Temperature = 0.1f };
            var json = await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(req);
            
            if (JsonUtils.TryDeserialize<KeysGenerationResponse>(json, out var res) && res.Keys != null)
            {
                foreach (var kvp in res.Keys)
                {
                    var loc = locs.FirstOrDefault(l => l.Id == kvp.Key);
                    if (loc != null) 
                    {
                        loc.Keys = kvp.Value;
                        continue;
                    }
                    
                    var obj = objs.FirstOrDefault(o => o.Id == kvp.Key);
                    if (obj != null)
                    {
                        obj.Keys = kvp.Value;
                    }
                }
            }
        }
        
        private async Task<List<ObjectData>> PopulateLocation(string description, List<LocationData> locationsContext,
            List<ObjectData> objectsContext, LocationData newLocation)
        {
            //var temp = new List<LocationData>(locationsContext) { newLocation };
            //var map = await MapGen.GenerateMap(temp, temp);
            var prompt = BuildPrompt(PromptType.GenerationObjects,
                $"Generation description: {description}\n Context objects: {WorldStateHelper.FormatLocationData(locationsContext, objectsContext)}\n" +
                $" LocationToPopulate:{WorldStateHelper.FormatLocationData([newLocation],[])} \n NextId:{StateManager.Instance.CurrentWorld.GetNextId()} \nCurrent time: {WorldStateHelper.GetCurrentWorldTime(locationsContext)}");

            var result = await ExecuteLmmRequest<ObjectsResponse>(prompt, null);
            
            if (result.Objects.Count > 0)
            {
                var maxId = result.Objects.Max(o => o.Id);
                StateManager.Instance.CurrentWorld.SetNextId(maxId + 1);
            }
            else
            {
                throw new Exception($"No objects generated. \n {result}");
            }
            return result.Objects;
        }

        private (List<int> LocationId, string Cell) FindStartingCell(List<LocationData> contextLocations)
        {
            var world = StateManager.Instance.CurrentWorld;
            if (contextLocations == null || contextLocations.Count == 0) return ([], "0:0:0");
            if (world.History.Texts.Count > 0)
            {
                var lastText = world.History.Texts.Last();
                if (lastText.Locations.Count > 0)
                {
                    var locId = lastText.Locations.Last();
                    // Ищем локацию в контексте
                    var loc = contextLocations.FirstOrDefault(l => l.Id == locId);
                    if (loc != null) return ([loc.Id], FindNeighborCellForLocation(loc, contextLocations));
                }
            }

            return default;
        }

        private string FindNeighborCellForLocation(LocationData loc, List<LocationData> allContextLocations)
        {
            var occupied = allContextLocations
                .SelectMany(l => l.GetCellIndices())
                .ToHashSet();

            var locCells = loc.GetCellIndices();
            
            foreach (var cellIdx in locCells)
            {
                var coord = GridCoordinate.Parse(cellIdx);
                var neighbors = new[]
                {
                    new GridCoordinate(coord.X + 1, coord.Y, coord.Z),
                    new GridCoordinate(coord.X - 1, coord.Y, coord.Z),
                    new GridCoordinate(coord.X, coord.Y + 1, coord.Z),
                    new GridCoordinate(coord.X, coord.Y - 1, coord.Z)
                };

                foreach (var n in neighbors)
                {
                    var nIdx = n.ToString();
                    if (!occupied.Contains(nIdx)) return nIdx;
                }
            }
            
            if (locCells.Count > 0)
            {
                var coord = GridCoordinate.Parse(locCells[0]);
                return new GridCoordinate(coord.X + 2, coord.Y + 2, coord.Z).ToString();
            }

            return "0:0:0";
        }

        private string BuildPrompt(PromptType type, string inputData)
        {
            var rules = PromptLibrary.Instance.GetPrompt(PromptType.GenerationRules);
            return PromptLibrary.Instance.GetPrompt(type, rules, inputData);
        }

        private async Task<T> ExecuteLmmRequest<T>(string fullPrompt, Image contextImage = null)
        {
            var request = new LmmRequest
            {
                UserPrompt = fullPrompt,
                Temperature = 0.4f, 
                Images = contextImage != null ? new List<Image> { contextImage } : null,
                ThinkingLevel = GeminiThinkingLevel.medium
            };

            var provider = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            var json = await provider.GenerateAsync(request);

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
            
            var output = JsonUtils.Serialize(new ToolResponseContainer 
            { 
                ToolName = ToolName, 
                Result = result 
            });
            
            LmmFactory.Instance.GetProvider(LmmModelType.Fast).PrintTokens();
            LmmFactory.Instance.GetProvider(LmmModelType.Smart).PrintTokens();
            OnComplete?.Invoke(output);
        }
    }
}