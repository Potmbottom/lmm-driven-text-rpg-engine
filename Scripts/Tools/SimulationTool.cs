using Godot;
using RPG.AI;
using RPG.AI.Core;
using RPG.Core;
using RPG.Models;
using RPG.Models.Simulation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RPG.Core.Helpers;

namespace RPG.Tools
{
    public partial class SimulationTool : Node, ITool
    {
        [Export] public MapGenerator MapGen;
        [Export] public GenerationTool GeneratorTool;
        [Export] public QueryTool QueryTool;
        public string ToolName => "Simulation";

        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;

        private WorldState _worldState;

        // Working copies of data
        private List<ObjectData> _cachedObjects;
        private List<LocationData> _cachedLocations;

        // Snapshots for change tracking
        private Dictionary<int, string> _initialObjectSnapshots;
        private Dictionary<int, string> _initialLocationSnapshots;

        private List<string> _aggregatedLog = new();
        private List<SimulationResponse> _structuredOutput = new();
        private QueryMetaData _currentMetaData; 
        
        private const int HISTORY_CONTEXT_DEPTH = 3;

        public async void Call(string parameters)
        {
            try
            {
                var metaData = await AnalyzeUserRequest(parameters);
                InitializeState(metaData);
                var activeLocationIds = await DetermineActiveLocations(metaData.UserQuery);
                await RunSimulationLoop(activeLocationIds);
                await FinalizeSimulation(activeLocationIds);
            }
            catch (Exception ex)
            {
                GD.PrintErr(ex);
                OnFail?.Invoke(JsonUtils.Serialize(new { error = ex.Message }));
            }
        }
        
        private async Task<QueryMetaData> AnalyzeUserRequest(string rawParameters)
        {
            OnUpdate?.Invoke("🧠 Analyzing request parameters...");

            var request = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.SimulationQueryParser),
                UserPrompt = rawParameters,
                Temperature = 0.0f
            };

            var json = await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(request);
            
            return JsonUtils.TryDeserialize<QueryMetaData>(json, out var metaData) ? metaData : null;
        }

        private void InitializeState(QueryMetaData metaData)
        {
            _aggregatedLog.Clear();
            _structuredOutput.Clear();
            
            _worldState = StateManager.Instance.CurrentWorld;
            _cachedObjects = new List<ObjectData>(_worldState.Objects);
            _cachedLocations = new List<LocationData>(_worldState.Locations);
            
            _initialObjectSnapshots = _cachedObjects.ToDictionary(o => o.Id, JsonUtils.Serialize);
            _initialLocationSnapshots = _cachedLocations.ToDictionary(l => l.Id, JsonUtils.Serialize);
            
            metaData.SimulationStartTime = _worldState.GetCurrentWorldTime();
            _currentMetaData = metaData;
            
            _aggregatedLog.Add(JsonUtils.Serialize(_currentMetaData));

            OnUpdate?.Invoke($"🔄 Initializing Simulation at {_currentMetaData.SimulationStartTime}...");
        }

        private async Task<List<int>> DetermineActiveLocations(string userPrompt)
        {
            OnUpdate?.Invoke("🧐 Analyzing context...");

            var request = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.SimulationContext),
                UserPrompt = userPrompt,
                Temperature = 0.1f
            };

            var json = await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(request);
            if (!JsonUtils.TryDeserialize<ContextSearchResult>(json, out var contextRes) || contextRes.Queries == null)
                return null;

            var queryInput = new
            {
                queries = contextRes.Queries,
                format = "{ \"location_ids\": [int] } // Return a SINGLE JSON object merging ALL unique location IDs."
            };

            var tcs = new TaskCompletionSource<string>();
            Action<string> onComplete = (res) => tcs.TrySetResult(res);
            Action<string> onFail = (err) => tcs.TrySetException(new Exception(err));

            QueryTool.OnComplete += onComplete;
            QueryTool.OnFail += onFail;

            try
            {
                QueryTool.Call(JsonUtils.Serialize(queryInput));
                string queryJson = await tcs.Task;

                if (JsonUtils.TryDeserialize<ToolResponseContainer>(queryJson, out var container) &&
                    JsonUtils.TryDeserialize<LocationSearchResult>(container.Result?.Temporary?.Result, out var locRes))
                {
                    return locRes.LocationIds.Distinct().ToList();
                }
            }
            finally
            {
                QueryTool.OnComplete -= onComplete;
                QueryTool.OnFail -= onFail;
            }

            return null;
        }
        
        private string CalculateTimeParams(List<LocationData> locations)
        {
            string oldest = _currentMetaData.SimulationStartTime;
            foreach (var location in locations)
            {
                oldest = TimeHelper.GetMin(oldest, location.LastUpdateTime);
            }

            return oldest;
        }

        private async Task RunSimulationLoop(List<int> activeLocIds)
        {
            int safety = 0;
            bool running = true;

            while (running && safety++ < 4)
            {
                var activeLocations = _cachedLocations.Where(data => activeLocIds.Contains(data.Id)).ToList();
                var areaResult = WorldStateHelper.GetExtendedArea(activeLocations, 12, _cachedLocations);
                var currentSimTime = CalculateTimeParams([..activeLocations]);
                
                List<string> stepRequestLog = [];
                Image currentMap = null;
                List<string> recentHistory = [];
                string context = "";

                var catchUp = TimeHelper.Compare(currentSimTime, _currentMetaData.SimulationStartTime) < 0;
                
                if (catchUp)
                {
                    OnUpdate?.Invoke($"⚙️ Syncing Step {safety} (Time: {currentSimTime})...");
                    
                    context = SimulationHelper.BuildContext(
                        [..areaResult.Locations, ..activeLocations], //context
                        activeLocations.Where(data => TimeHelper.Compare(data.LastUpdateTime, _currentMetaData.SimulationStartTime) < 0).Select(data => data.Id).ToHashSet(), //to show
                        _cachedObjects,
                        new QueryMetaData
                        {
                            SimulationStartTime = currentSimTime,
                            Info = _currentMetaData.Info,
                            TargetSimulationDuration = TimeHelper.SubtractDuration(_currentMetaData.SimulationStartTime, currentSimTime),
                            UserQuery = "SYSTEM: The world state is lagging behind. Simulate background events, physics, and NPC schedules to sync with the timeline."
                        },
                        recentHistory,
                        stepRequestLog);
                    currentMap = await MapGen.GenerateMap(activeLocations, _cachedLocations);
                }
                else
                {
                    OnUpdate?.Invoke($"⚙️ Interaction Step {safety} (Time: {currentSimTime})...");
                    stepRequestLog = _aggregatedLog;
                    recentHistory = _worldState.History.Texts
                        .TakeLast(HISTORY_CONTEXT_DEPTH)
                        .Select(t => t.Text)
                        .ToList();
                    context = SimulationHelper.BuildContext(
                        [..areaResult.Locations, ..activeLocations], //context
                        activeLocations.Select(data => data.Id).ToHashSet(), //to show
                        _cachedObjects,
                        _currentMetaData,
                        recentHistory,
                        stepRequestLog);
                    currentMap = await MapGen.GenerateMap(activeLocations, _cachedLocations);
                }
                
                var response = await CallLmm(context, currentMap);
                _currentMetaData.SimulationStartTime = response.Structured.Last().Time;
                _structuredOutput.Add(response);
                foreach (var step in response.Structured)
                {
                    SimulationHelper.ApplyActions(step.Actions, _cachedObjects, _cachedLocations);
                }
                foreach (var location in _cachedLocations.Where(location => activeLocations.Contains(location)))
                {
                    location.LastUpdateTime = _currentMetaData.SimulationStartTime;
                }

                if (!catchUp)
                {
                    if (_aggregatedLog.Count > 0)
                    {
                        _aggregatedLog[0] = JsonUtils.Serialize(_currentMetaData);
                    }
                    running = await HandleBreakPoint(response, activeLocIds);   
                }
            }
        }

        private async Task<SimulationResponse> CallLmm(string context, Image map)
        {
            var req = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Simulation),
                UserPrompt = context,
                Images = new List<Image> { map },
                Temperature = 0.5f
            };

            string json = await LmmFactory.Instance.GetProvider(LmmModelType.Smart).GenerateAsync(req);

            if (JsonUtils.TryDeserialize<SimulationResponse>(json, out var res)) return res;

            GD.PrintErr($"Failed to parse simulation response: {json}");
            return null;
        }

        private async Task<bool> HandleBreakPoint(SimulationResponse response,
            List<int> activeLocIds)
        {
            OnUpdate?.Invoke($"Simulation break: {response.BreakPoint} ({response.BreakDescription})");

            switch (response.BreakPoint)
            {
                case "Time":
                    OnUpdate?.Invoke("✅ Simulation finished by Time.");
                    return false;

                case "ExpansionLocation":
                case "ExpansionGroup":
                case "ExpansionObject":
                case "RetrieveLocation":
                    var result = JsonUtils.Deserialize<ExpandRetrieveBreakData>(response.BreakDescription);
                    await HandleAdvancedExpansion(response.BreakPoint, result, activeLocIds);
                    return true;

                case "Check":
                    if (!JsonUtils.TryDeserialize<CheckBreakData>(response.BreakDescription, out var data))
                    {
                        throw new Exception($"Failed to parse break description: {response.BreakDescription}");
                    }

                    var checkResult = SimulationHelper.PerformSkillCheck(data);
                    
                    _aggregatedLog.Add($"Check '{checkResult.Reason}': {(checkResult.IsSuccess ? "Success" : "Failure")}");
                    
                    OnUpdate?.Invoke($"🎲 Check ({checkResult.Reason}): {(checkResult.IsSuccess ? "Success" : "Fail")}");
                    return true;

                default:
                    // Default to continuing unless explicit stop or safety limit
                    return false;
            }
        }

        private async Task HandleAdvancedExpansion(string type, ExpandRetrieveBreakData data, List<int> activeLocIds)
        {
            if (type == "RetrieveLocation")
            {
                activeLocIds.Add(data.TargetId);
                return;
            }
            
            var tcs = new TaskCompletionSource<string>();
            Action<string> onComp = (s) => tcs.TrySetResult(s);
            GeneratorTool.OnComplete += onComp;

            try
            {
                string generatorRequest = $"Generate Type: {type}. Target cell: {data.TargetCell ?? "none"} Target Index: {data.TargetId}. Full Context: {data.GenerationInformation}";
                
                GeneratorTool.CallWithContext([.._cachedLocations], [.._cachedObjects], generatorRequest);
                string json = await tcs.Task;

                if (JsonUtils.TryDeserialize<ToolResponseContainer>(json, out var res))
                {
                    bool dataAdded = false;

                    // Handle New/Modified Locations
                    if (res.Result.Mutable.Locations != null)
                    {
                        foreach (var newLoc in res.Result.Mutable.Locations)
                        {
                            // If it's a new location or update
                            var existing = _cachedLocations.FirstOrDefault(l => l.Id == newLoc.Id);
                            if (existing == null)
                            {
                                _cachedLocations.Add(newLoc);
                                activeLocIds.Add(newLoc.Id);
                                _aggregatedLog.Add($"New Location Created: {newLoc.Id}");
                            }
                            else
                            {
                                // Merging logic if needed, or simple replace for simulation cache
                                int idx = _cachedLocations.IndexOf(existing);
                                _cachedLocations[idx] = newLoc;
                            }
                        }
                    }

                    // Handle Objects
                    if (res.Result.Mutable.Objects != null && res.Result.Mutable.Objects.Count > 0)
                    {
                        _cachedObjects.AddRange(res.Result.Mutable.Objects);
                    }
                }
            }
            finally
            {
                GeneratorTool.OnComplete -= onComp;
            }
        }

        private async Task FinalizeSimulation(List<int> activeLocIds)
        {
            OnUpdate?.Invoke("✍️ Writing Narrative...");

            var context = WorldStateHelper.FormatLocationData(_cachedLocations, _cachedObjects);
            var events = _worldState.History.Texts
                .TakeLast(1);
    
            var preNarrativeData =
                $"Last events{events}\nFull Execution Log:\n{JsonUtils.Serialize(_structuredOutput)}\nContext: {context}";
            var narrativeReq = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Narrative),
                UserPrompt = preNarrativeData,
                Temperature = 0.7f
            };

            string narrativeJson =
                await LmmFactory.Instance.GetProvider(LmmModelType.Smart).GenerateAsync(narrativeReq);
            string narrativeText = JsonUtils.TryDeserialize<NarrativeResponse>(narrativeJson, out var nr)
                ? nr.Text
                : "Narrative failed.";

            if (nr.IsUnsafe)
            {
                var unsafeNarrativeReq = new LmmRequest
                {
                    SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.UnsafeNarrative),
                    UserPrompt = $"User request: {_currentMetaData.UserQuery}\n{preNarrativeData}\n First stage generation: {narrativeText}",
                    Temperature = 0f,
                };

                narrativeText = await LmmFactory.Instance.GetProvider(LmmModelType.Local).GenerateAsync(unsafeNarrativeReq);
            }

            var changedLocations = _cachedLocations.Where(l =>
                !_initialLocationSnapshots.ContainsKey(l.Id) ||
                _initialLocationSnapshots[l.Id] != JsonUtils.Serialize(l)
            ).ToList();

            var changedObjects = _cachedObjects.Where(o =>
                !_initialObjectSnapshots.ContainsKey(o.Id) ||
                _initialObjectSnapshots[o.Id] != JsonUtils.Serialize(o)
            ).ToList();

            var result = new ToolResponseContainer
            {
                ToolName = ToolName,
                Result = new ToolResultContent
                {
                    Mutable = new MutableData
                    {
                        Locations = changedLocations,
                        Objects = changedObjects,
                    },
                    Immutable = new ImmutableData
                    {
                        Text = new TextEntry
                            { Text = $"{narrativeText}", Locations = activeLocIds, SimulationLog = _structuredOutput }
                    }
                }
            };

            OnComplete?.Invoke(JsonUtils.Serialize(result));
        }
    }
}