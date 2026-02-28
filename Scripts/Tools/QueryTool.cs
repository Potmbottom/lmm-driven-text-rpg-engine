using Godot;
using RPG.AI;
using RPG.AI.Core;
using RPG.Core;
using RPG.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RPG.AI.Providers;
using RPG.Core.Helpers;

namespace RPG.Tools
{
    public partial class QueryTool : Node, ITool
    {
        public string ToolName => "Query";

        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;
        
        private List<string> _aggregatedHistory = new();
        private HashSet<int> _extractedEventIndices = new();
        private const int MAX_STEPS = 1;
        
        public async void Call(string parameters)
        {
            try
            {
                _aggregatedHistory.Clear();
                _extractedEventIndices.Clear();
                
                _aggregatedHistory.Add($"[USER_REQUEST]: {parameters}");

                OnUpdate?.Invoke("🔍 Starting investigation...");

                await RunOrchestrationLoop();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"QueryTool Error: {ex}");
                OnFail?.Invoke(JsonUtils.Serialize(new { error = ex.Message }));
            }
        }

        private async Task RunOrchestrationLoop()
        {
            var stepCount = 0;
            var isComplete = false;

            while (!isComplete && stepCount < MAX_STEPS)
            {
                stepCount++;
                var historyContext = string.Join("\n---\n", _aggregatedHistory);
                var request = new LmmRequest
                {
                    SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.QueryOrchestrator),
                    UserPrompt = historyContext,
                    Temperature = 0.7f,
                    ThinkingLevel = GeminiThinkingLevel.medium
                };

                var responseJson = await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(request);
                
                if (!JsonUtils.TryDeserialize<OrchestratorDecision>(responseJson, out var decision))
                {
                    _aggregatedHistory.Add($"[SYSTEM_ERROR]: Failed to parse decision JSON: {responseJson}");
                    continue;
                }

                OnUpdate?.Invoke($"🤔 {decision.Think}");
                foreach (var tool in decision.Tools)
                {
                    OnUpdate?.Invoke($"🛠️ Executing: {tool.ToolName}...");
                    var toolResult = await ExecuteSubTool(tool);
                    _aggregatedHistory.Add($"[TOOL_RESULT ({tool.ToolName})]: {toolResult.Item1}");
                    isComplete = toolResult.Item2;
                    if(isComplete) break;
                }
            }

            if (isComplete || stepCount >= MAX_STEPS)
            {
                OnUpdate?.Invoke("Generating final answer...");
                var finalRes = await GenerateFinalResponse();
                ProcessFinalResult(finalRes);
            }
        }

        private async Task<(string, bool)> ExecuteSubTool(OrchestratorToolData decision)
        {
            var world = StateManager.Instance.CurrentWorld;

            switch (decision.ToolName.ToLower())
            {
                case "get_location_by_id":
                    if (int.TryParse(decision.Payload, out var locId))
                    {
                        var loc = world.Locations.FirstOrDefault(l => l.Id == locId);
                        return (loc != null ? JsonUtils.Serialize(loc) : "Location not found.", false);
                    }
                    return ("Invalid ID format.", false);

                case "get_object_by_id":
                    if (int.TryParse(decision.Payload, out var objId))
                    {
                        var obj = world.Objects.FirstOrDefault(o => o.Id == objId);
                        return (obj != null ? JsonUtils.Serialize(obj) : "Object not found.", false);
                    }
                    return ("Invalid ID format.", false);

                case "get_nested_objects":
                    if (int.TryParse(decision.Payload, out var parentId))
                    {
                        var nestedObjects = GetRecursiveChildObjects(parentId, world);
                        return (nestedObjects.Count > 0 
                            ? JsonUtils.Serialize(nestedObjects) 
                            : "No nested objects found (Empty inventory/container).", false);
                    }
                    return ("Invalid ID format for nested objects.", false);
                case "get_all_location_objects":
                    if (int.TryParse(decision.Payload, out var targetLocIdForAll))
                    {
                        var targetLocObj = world.Locations.FirstOrDefault(l => l.Id == targetLocIdForAll);
                        if (targetLocObj == null) return ("Location not found.", false);
                        var allObjectIds = GetObjectsInLocation(targetLocObj, world);
                        var allObjects = world.Objects
                            .Where(o => allObjectIds.Contains(o.Id))
                            .ToList();

                        return (allObjects.Count > 0
                            ? JsonUtils.Serialize(allObjects)
                            : "Location is strictly empty (no objects found).", false);
                    }
                    return ("Invalid ID format for location objects.", false);

                case "get_location_by_cell":
                    var cellIndex = decision.Payload.Trim();
                    var locByCell = world.Locations.FirstOrDefault(l => l.GetCellIndices().Contains(cellIndex));
                    return (locByCell != null ? JsonUtils.Serialize(locByCell) : $"Location with cell {cellIndex} not found.", false);

                case "get_recent_events":
                    if (int.TryParse(decision.Payload, out var count))
                    {
                        var events = world.History.Texts.TakeLast(Math.Min(count, world.History.Texts.Count)).ToList();
                        
                        var total = world.History.Texts.Count;
                        for (var i = 0; i < events.Count; i++) 
                            _extractedEventIndices.Add(total - events.Count + i);
                            
                        return (events.Count > 0 ? JsonUtils.Serialize(events) : "No recent events.", false);
                    }
                    return ("Invalid count format.", false);

                case "get_by_key":
                    var searchLocation = world.Locations.FirstOrDefault(data => data.Keys.Contains(decision.Payload));
                    var searchObject = world.Objects.FirstOrDefault(o => o.Keys.Contains(decision.Payload));
                    return (
                        $"Find entities for key {decision.Payload}:\nLocations:{JsonUtils.Serialize(searchLocation)}\nObjects:{JsonUtils.Serialize(searchObject)}",
                        false);
                case "find_object_by_similarity":
                    return (await HandleVectorSearch(decision.Payload, SearchType.Object), false);

                case "find_location_by_similarity":
                    return (await HandleVectorSearch(decision.Payload, SearchType.Location), false);

                case "find_text_by_similarity":
                    return (await HandleVectorSearch(decision.Payload, SearchType.Event), false);

                case "find_object_in_location":
                    // Ожидаемый формат: "LocationID | Description"
                    var parts = decision.Payload.Split('|');
                    if (parts.Length < 2) return ("Invalid payload format. Use: 'LocationID | ObjectDescription'", false);
                    
                    if (!int.TryParse(parts[0].Trim(), out var targetLocId)) return ("Invalid Location ID.", false);
                    var searchDesc = parts[1].Trim();

                    var targetLoc = world.Locations.FirstOrDefault(l => l.Id == targetLocId);
                    if (targetLoc == null) return ("Location not found.", false);
                    var allowedObjectIds = GetObjectsInLocation(targetLoc, world);
                    
                    if (allowedObjectIds.Count == 0) return ("Location contains no objects.", false);
                    
                    return (await HandleVectorSearch(searchDesc, SearchType.Object, allowedObjectIds), false);

                case "final":
                    return ("Final", true);

                default:
                    return ($"Unknown tool: {decision.ToolName}", false);
            }
        }
        
        private List<ObjectData> GetRecursiveChildObjects(int rootParentId, WorldState world)
        {
            var result = new List<ObjectData>();
            var openList = new Queue<int>();
            var visited = new HashSet<int>();

            openList.Enqueue(rootParentId);
            visited.Add(rootParentId);

            while (openList.Count > 0)
            {
                var currentParent = openList.Dequeue();
                var children = world.Objects
                    .Where(o => o.ParentObjectId == currentParent)
                    .ToList();

                foreach (var child in children)
                {
                    if (!visited.Contains(child.Id))
                    {
                        visited.Add(child.Id);
                        result.Add(child);
                        openList.Enqueue(child.Id);
                    }
                }
            }

            return result;
        }

        private List<int> GetObjectsInLocation(LocationData loc, WorldState world)
        {
            var allowedIds = new HashSet<int>();
            var locCellIndices = new HashSet<string>(loc.GetCellIndices());
            
            foreach (var obj in world.Objects)
            {
                if (obj.CellIndices != null && obj.CellIndices.Any(c => locCellIndices.Contains(c)))
                {
                    allowedIds.Add(obj.Id);
                }
            }
            
            bool addedNew;
            do
            {
                addedNew = false;
                foreach (var obj in world.Objects)
                {
                    if (allowedIds.Contains(obj.Id)) continue;

                    if (obj.ParentObjectId.HasValue && allowedIds.Contains(obj.ParentObjectId.Value))
                    {
                        allowedIds.Add(obj.Id);
                        addedNew = true;
                    }
                }
            } while (addedNew);

            return allowedIds.ToList();
        }

        private async Task<string> HandleVectorSearch(string query, SearchType type, List<int> allowedIds = null)
        {
            var results = await StateManager.Instance.VectorDB.Search(query, type, limit: 5, allowedIds: allowedIds);
            if (type == SearchType.Event)
            {
                results = results.Where(r => !_extractedEventIndices.Contains(r.Id)).ToList();
            }

            if (results.Count == 0) return "No matches found in VectorDB.";
            if (results.Count == 1)
            {
                return await FetchEntityById(results[0].Id, type);
            }
            
            var candidatesJson = JsonUtils.Serialize(results.Select(r => new { id = r.Id, content = r.Content, score = r.HybridScore }));
            
            var request = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.QuerySelector),
                UserPrompt = $"Original Query: {query}\nCandidates:\n{candidatesJson}\nType: {type}",
                Temperature = 0.1f
            };

            var response = await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(request);

            if (JsonUtils.TryDeserialize<SelectionResult>(response, out var selection))
            {
                if (type == SearchType.Event && selection.SelectedIndex.HasValue)
                {
                    return await FetchEntityById(selection.SelectedIndex.Value, type);
                }
                else if (selection.SelectedId.HasValue)
                {
                    return await FetchEntityById(selection.SelectedId.Value, type);
                }
            }

            return "Multiple candidates found but none selected confidently.";
        }

        private async Task<string> FetchEntityById(int id, SearchType type)
        {
            var world = StateManager.Instance.CurrentWorld;
            switch (type)
            {
                case SearchType.Location:
                    var loc = world.Locations.FirstOrDefault(l => l.Id == id);
                    return loc != null ? JsonUtils.Serialize(loc) : "Error: Location ID found in vector DB but missing in World.";
                
                case SearchType.Object:
                    var obj = world.Objects.FirstOrDefault(o => o.Id == id);
                    return obj != null ? JsonUtils.Serialize(obj) : "Error: Object ID found in vector DB but missing in World.";
                
                case SearchType.Event:
                    if (id >= 0 && id < world.History.Texts.Count)
                    {
                        _extractedEventIndices.Add(id);
                        return JsonUtils.Serialize(world.History.Texts[id]);
                    }
                    return "Error: Event Index out of bounds.";
            }
            return "Unknown type.";
        }

        private async Task<string> GenerateFinalResponse()
        {
            var request = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.QueryFinalizer),
                UserPrompt = string.Join("\n---\n", _aggregatedHistory),
                Temperature = 0.5f
            };

            return await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(request);
        }

        private void ProcessFinalResult(string finalJson)
        {
            var finalResultString = "";

            if (JsonUtils.TryDeserialize<FinalResponse>(finalJson, out var response))
            {
                try
                {
                    finalResultString = response.Result.GetRawText();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }

            var container = new ToolResponseContainer
            {
                ToolName = ToolName,
                Result = new ToolResultContent
                {
                    Temporary = new TemporaryData
                    {
                        Result = finalResultString
                    }
                }
            };

            LmmFactory.Instance.GetProvider(LmmModelType.Fast).PrintTokens();
            LmmFactory.Instance.GetProvider(LmmModelType.Smart).PrintTokens();
            OnUpdate?.Invoke("✅ Query Complete.");
            OnUpdate?.Invoke(finalResultString);
            OnComplete?.Invoke(JsonUtils.Serialize(container));
        }
    }
}