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
using RPG.AI.Providers;
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
        
        private List<ObjectData> _cachedObjects;
        private List<LocationData> _cachedLocations;
        
        private Dictionary<int, string> _initialObjectSnapshots;
        private Dictionary<int, string> _initialLocationSnapshots;

        private List<string> _aggregatedLog = new();
        private List<SimulationResponse> _structuredOutput = new();
        private QueryMetaData _currentMetaData; 
        
        private List<string> _structuredOutputTruncated = new();
        private List<int> _structuredObjectsIds = new();
        
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
            _structuredObjectsIds.Clear();
            _structuredOutputTruncated.Clear();
            
            _worldState = StateManager.Instance.CurrentWorld;
            _cachedObjects = new List<ObjectData>(_worldState.Objects);
            _cachedLocations = new List<LocationData>(_worldState.Locations);
            
            _initialObjectSnapshots = _cachedObjects.ToDictionary(o => o.Id, JsonUtils.Serialize);
            _initialLocationSnapshots = _cachedLocations.ToDictionary(l => l.Id, JsonUtils.Serialize);
            
            metaData.SimulationStartTime = WorldStateHelper.GetCurrentWorldTime(_worldState.Locations);
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
                notes = "Your final goal is to find locations, do not dig deep to find objects in already investigated locations",
                format = "{ \"location_ids\": [int] } // Return a SINGLE JSON object merging ALL unique location IDs. JSON response format : { array[int] }"
            };

            var tcs = new TaskCompletionSource<string>();
            Action<string> onComplete = (res) => tcs.TrySetResult(res);
            Action<string> onFail = (err) => tcs.TrySetException(new Exception(err));

            QueryTool.OnComplete += onComplete;
            QueryTool.OnFail += onFail;

            try
            {
                QueryTool.Call(JsonUtils.Serialize(queryInput));
                var queryJson = await tcs.Task;

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
            var oldest = _currentMetaData.SimulationStartTime;
            foreach (var location in locations)
            {
                oldest = TimeHelper.GetMin(oldest, location.LastUpdateTime);
            }

            return oldest;
        }

        private async Task RunSimulationLoop(List<int> activeLocIds)
        {
            var safety = 0;
            var running = true;

            while (running && safety++ < 4)
            {
                var activeLocations = _cachedLocations.Where(data => activeLocIds.Contains(data.Id)).ToList();
                var areaResult = WorldStateHelper.GetExtendedArea(activeLocations, _cachedLocations);
                var currentSimTime = CalculateTimeParams([..activeLocations]);
                
                List<string> stepRequestLog = [];
                Image currentMap = null;
                List<string> recentHistory = [];
                var context = "";
                var model = LmmModelType.Fast;

                var catchUp = TimeHelper.Compare(currentSimTime, _currentMetaData.SimulationStartTime) < 0;
                
                if (catchUp)
                {
                    OnUpdate?.Invoke($"⚙️ Syncing Step {safety} (Time: {currentSimTime})...");

                    var catchUpContext = activeLocations.Where(data =>
                        TimeHelper.Compare(data.LastUpdateTime, _currentMetaData.SimulationStartTime) < 0);
                    
                    context = SimulationHelper.BuildContext(
                        catchUpContext, //context
                        catchUpContext.Select(data => data.Id).ToHashSet(), //to show
                        _cachedObjects,
                        new QueryMetaData
                        {
                            SimulationStartTime = currentSimTime,
                            TargetSimulationDuration = TimeHelper.SubtractDuration(_currentMetaData.SimulationStartTime, currentSimTime),
                            UserQuery = "The world state is lagging behind. Simulate only valuable background events, physics, and NPC schedules to sync with the timeline." +
                                        "Do not simulate not important things, ignore it. If no valuable actions to simulate return empty Action array."
                        },
                        recentHistory,
                        stepRequestLog);
                    model = LmmModelType.Fast;
                }
                else
                {
                    OnUpdate?.Invoke($"⚙️ Interaction Step {safety} (Time: {currentSimTime})...");
                    stepRequestLog = _aggregatedLog[1..];
                    recentHistory = _worldState.History.Texts
                        .TakeLast(HISTORY_CONTEXT_DEPTH)
                        .Select(t => t.Text)
                        .ToList();
                    context = SimulationHelper.BuildContext(
                        [..areaResult.Locations, ..activeLocations],
                        activeLocations.Select(data => data.Id).ToHashSet(),
                        _cachedObjects,
                        _currentMetaData,
                        recentHistory,
                        stepRequestLog);
                    currentMap = await MapGen.GenerateMap(activeLocations, _cachedLocations);
                }
                
                var response = await CallLmm(context, currentMap, model);
                _structuredOutput.Add(response);
                
                foreach (var step in response.Structured)
                {
                    var res = SimulationHelper.ApplyActions(step.Actions, _cachedObjects, _cachedLocations);
                    _structuredOutputTruncated.Add($"{res.Item2}\n{step.Time}");
                    _structuredObjectsIds.AddRange(res.Item1);
                    _structuredObjectsIds = _structuredObjectsIds.Distinct().ToList();
                }

                if (!catchUp)
                {
                    _currentMetaData.SimulationStartTime = response.Structured.Last().Time;
                    if (_aggregatedLog.Count > 0)
                    {
                        _aggregatedLog[0] = JsonUtils.Serialize(_currentMetaData);
                    }
                }
                
                foreach (var location in _cachedLocations.Where(location => activeLocations.Contains(location)))
                {
                    location.LastUpdateTime = _currentMetaData.SimulationStartTime;
                }

                if (!catchUp)
                {
                    running = await HandleBreakPoint(response, activeLocIds);   
                }
            }
            
            LmmFactory.Instance.GetProvider(LmmModelType.Fast).PrintTokens();
            LmmFactory.Instance.GetProvider(LmmModelType.Smart).PrintTokens();
            OnUpdate?.Invoke($"🔚 Simulation end at {_currentMetaData.SimulationStartTime}");
        }

        private async Task<SimulationResponse> CallLmm(string context, Image map, LmmModelType lmmModelType)
        {
            var req = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Simulation),
                UserPrompt = context,
                Images = new List<Image> { map },
                Temperature = 0.7f,
                ThinkingLevel = GeminiThinkingLevel.low
            };

            var json = await LmmFactory.Instance.GetProvider(lmmModelType).GenerateAsync(req);

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
                    
                    _aggregatedLog.Add($"Check '{data.Reason}': Roll: {checkResult.roll} vs difficulty {data.Payload} = {(checkResult.IsSuccess ? "Success" : "Failure")}");
                    OnUpdate?.Invoke($"🎲 Check '{data.Reason}': Roll: {checkResult.roll} vs difficulty {data.Payload} = {(checkResult.IsSuccess ? "Success" : "Failure")}");
                    return true;

                default:
                    return false;
            }
        }

        private async Task HandleAdvancedExpansion(string type, ExpandRetrieveBreakData data, List<int> activeLocIds)
        {
            if (type == "RetrieveLocation")
            {
                if(data.TargetId.HasValue)
                    activeLocIds.Add(data.TargetId.Value);
                return;
            }
            
            var tcs = new TaskCompletionSource<string>();
            Action<string> onComp = (s) => tcs.TrySetResult(s);
            GeneratorTool.OnComplete += onComp;

            try
            {
                var generatorRequest = $"Generate Type: {type}. Target cell: {data.TargetCell ?? "none"} Target Index: {data.TargetId}. Full Context: {data.GenerationInformation}";
                
                GeneratorTool.CallWithContext([.._cachedLocations], [.._cachedObjects], generatorRequest);
                var json = await tcs.Task;

                if (JsonUtils.TryDeserialize<ToolResponseContainer>(json, out var res))
                {
                    var dataAdded = false;
                    if (res.Result.Mutable.Locations != null)
                    {
                        foreach (var newLoc in res.Result.Mutable.Locations)
                        {
                            var existing = _cachedLocations.FirstOrDefault(l => l.Id == newLoc.Id);
                            if (existing == null)
                            {
                                _cachedLocations.Add(newLoc);
                                activeLocIds.Add(newLoc.Id);
                                _aggregatedLog.Add($"New Location Created: {newLoc.Id}");
                            }
                            else
                            {
                                var idx = _cachedLocations.IndexOf(existing);
                                _cachedLocations[idx] = newLoc;
                            }
                        }
                    }
                    
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
            
            var context = WorldStateHelper.FormatLocationData(_cachedLocations, _cachedObjects.Where(data => _structuredObjectsIds.Contains(data.Id)).ToList());
            var events = _worldState.History.Texts
                .TakeLast(1);
    
            var preNarrativeData =
                $"Last events{events}\nFull Execution Log:\n{string.Join("\n", _structuredOutputTruncated)}\nContext: {context}";
            var narrativeReq = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Narrative),
                UserPrompt = preNarrativeData,
                Temperature = 0.7f
            };

            var narrativeJson =
                await LmmFactory.Instance.GetProvider(LmmModelType.Smart).GenerateAsync(narrativeReq);
            var narrativeText = JsonUtils.TryDeserialize<NarrativeResponse>(narrativeJson, out var nr)
                ? nr.Text
                : "Narrative failed.";

            if (nr.IsUnsafe)
            {
                var unsafeNarrativeReq = new LmmRequest
                {
                    SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.UnsafeNarrative),
                    UserPrompt = $"User request: {_currentMetaData.UserQuery}\nContext:{context}\n First stage generation: {narrativeText}",
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