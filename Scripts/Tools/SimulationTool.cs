using Godot;
using RPG.AI;
using RPG.AI.Core;
using RPG.Core;
using RPG.Models;
using RPG.Models.Simulation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RPG.Core.Helpers;

namespace RPG.Tools
{
    public partial class SimulationTool : Node, ITool
    {
        private class LocationSearchResult
        {
            [System.Text.Json.Serialization.JsonPropertyName("location_ids")]
            public List<int> LocationIds { get; set; } = new();
        }
        
        private class ContextSearchResult
        {
            [System.Text.Json.Serialization.JsonPropertyName("think")]
            public string Think { get; set; }
            
            [System.Text.Json.Serialization.JsonPropertyName("search_queries")]
            public List<string> Queries { get; set; }
        }
        
        [Export] public MapGenerator MapGen;
        [Export] public LocationGeneratorTool LocationGeneratorTool;
        [Export] public QueryTool QueryTool;
        public string ToolName => "Simulation";

        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;

        // Локальный кеш состояния для текущего хода
        private WorldState _worldState;
        private List<ObjectData> _cachedObjects;
        private List<LocationData> _cachedLocations;
        private List<CellData> _cachedCells;
        private string _currentTime;

        // Агрегированный лог (История запросов и ответов внутри тула)
        private List<string> _aggregatedLog = new();

        public async void Call(string parameters)
        {
            try
            {
                _aggregatedLog.Clear();
                _aggregatedLog.Add(parameters); // 1. Запрос пользователя

                _worldState = StateManager.Instance.CurrentWorld;

                // Инициализация кеша
                _cachedObjects = new List<ObjectData>(_worldState.Objects);
                _cachedLocations = new List<LocationData>(_worldState.Locations);
                _cachedCells = new List<CellData>(_worldState.Cells);

                OnUpdate?.Invoke("🔄 Initializing Simulation...");

                // --- 2. Определение активной зоны (ВЫЗЫВАЕТСЯ ТОЛЬКО ОДИН РАЗ) ---
                var initialLocIds = await DetermineActiveLocations(parameters);
                if (initialLocIds == null || initialLocIds.Count == 0)
                {
                    OnFail?.Invoke("Failed to determine active location context.");
                    return;
                }

                // Создаем основной список ID активных локаций. 
                // Дальше работаем только с ним, пополняя его при Expand.
                var activeLocationIds = new List<int>(initialLocIds);
                
                // Формируем список индексов ячеек на основе активных локаций
                var activeZoneIndices = RefreshActiveZoneIndices(activeLocationIds);

                var structuredOutput = new List<SimulationResponse>();

                // --- 3. Определяем текущее глобальное время ---
                string worldCurrentTime = _worldState.GetCurrentWorldTime(_cachedLocations, _cachedCells);
                _currentTime = worldCurrentTime;

                // --- 4. Предсимуляция (Time Sync) ---
                string oldestCellTime = worldCurrentTime;
                foreach (var idx in activeZoneIndices)
                {
                    var cell = _cachedCells.FirstOrDefault(c => c.Index == idx);
                    if (cell != null && !string.IsNullOrEmpty(cell.LastUpdateTime))
                    {
                        oldestCellTime = TimeHelper.GetMin(oldestCellTime, cell.LastUpdateTime);
                    }
                }

                // Логика параметров времени
                string simStartTime = worldCurrentTime;
                string simEndTime = worldCurrentTime;
                bool isPreSimulation = false;
                int turnDurationMinutes = 15;

                if (TimeHelper.Compare(oldestCellTime, worldCurrentTime) < 0)
                {
                    isPreSimulation = true;
                    simStartTime = oldestCellTime;
                    simEndTime = worldCurrentTime;
                    OnUpdate?.Invoke($"⏳ Catching up time: {oldestCellTime} -> {worldCurrentTime}");
                }
                else
                {
                    simStartTime = worldCurrentTime;
                    simEndTime = TimeHelper.AddMinutes(worldCurrentTime, turnDurationMinutes);
                }

                // --- 5 - 18. Основной цикл симуляции ---
                bool simulationRunning = true;
                int safetyBreaker = 0;
                
                // Генерируем карту на основе индексов ячеек (визуализация)
                var currentMap = await GenerateCurrentMap(activeZoneIndices);

                while (simulationRunning && safetyBreaker < 10)
                {
                    safetyBreaker++;

                    // Переключение из режима предсимуляции в обычный режим
                    if (isPreSimulation && safetyBreaker > 1)
                    {
                        isPreSimulation = false;
                        simStartTime = _currentTime;
                        simEndTime = TimeHelper.AddMinutes(_currentTime, turnDurationMinutes);
                    }

                    OnUpdate?.Invoke($"⚙️ Simulation Step {safetyBreaker} (Time: {_currentTime})...");

                    // Подготовка контекста.
                    // ВАЖНО: Передаем activeLocationIds явно. BuildSimulationContext не ищет локации заново,
                    // а берет строго те, что есть в списке activeLocationIds.
                    string context = BuildSimulationContext(activeLocationIds, activeZoneIndices, simStartTime, simEndTime);

                    var request = new LmmRequest
                    {
                        SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Simulation),
                        UserPrompt = context,
                        Images = new List<Image> { currentMap },
                        Temperature = 0.7f
                    };

                    var provider = LmmFactory.Instance.GetProvider(LmmModelType.Smart);
                    var responseJson = await provider.GenerateAsync(request);

                    if (!JsonUtils.TryDeserialize<SimulationResponse>(responseJson, out var simResponse))
                    {
                        GD.PrintErr($"Failed to parse simulation response: {responseJson}");
                        break;
                    }
                    OnUpdate?.Invoke($"Simulation break: {simResponse.BreakPoint}" );
                    OnUpdate?.Invoke($"Reason: {simResponse.BreakDescription}" );
                    // --- Логирование ---
                    
                    structuredOutput.Add(simResponse);
                    _aggregatedLog.Add(JsonUtils.Serialize(simResponse));

                    // 6 / 12 / 18. Применение действий (Actions)
                    foreach (var step in simResponse.Structured)
                    {
                        ApplyActions(step.Actions);
                        _currentTime = step.Time;
                        // Обновляем время ячеек в активной зоне
                        foreach (var idx in activeZoneIndices) UpdateCellTime(idx, _currentTime);
                    }

                    // Обработка Break Points
                    switch (simResponse.BreakPoint)
                    {
                        case "Time":
                            simulationRunning = false;
                            OnUpdate?.Invoke("✅ Simulation finished by Time.");
                            break;

                        case "Expansion":
                            OnUpdate?.Invoke("🚧 Expansion triggered...");
                            string targetIndex = ParseCellIndexFromText(simResponse.BreakDescription);

                            if (string.IsNullOrEmpty(targetIndex))
                            {
                                GD.PrintErr("Expansion triggered but no index found.");
                                simulationRunning = false;
                                break;
                            }

                            bool cellExists = _cachedCells.Any(c => c.Index == targetIndex);

                            if (cellExists)
                            {
                                // 7a. Ячейка существует, ищем её локацию
                                var ownerLoc = _cachedLocations.FirstOrDefault(l => l.CellIndices.Contains(targetIndex));
                                if (ownerLoc != null)
                                {
                                    // Добавляем ID в массив активных локаций, если его там нет
                                    if (!activeLocationIds.Contains(ownerLoc.Id))
                                    {
                                        activeLocationIds.Add(ownerLoc.Id);
                                        // Обновляем индексы ячеек для карты и времени
                                        activeZoneIndices = RefreshActiveZoneIndices(activeLocationIds);
                                        OnUpdate?.Invoke($"Included existing location {ownerLoc.Id} to active zone.");
                                    }
                                }
                            }
                            else
                            {
                                // 7b. Ячейка не существует, вызываем генератор
                                OnUpdate?.Invoke($"Generating new location at {targetIndex}...");
                                string genParams = $"Generate location connected to {targetIndex}. Context: {simResponse.BreakDescription}";

                                var tcs = new TaskCompletionSource<string>();
                                Action<string> onGenComplete = (res) => tcs.TrySetResult(res);
                                LocationGeneratorTool.OnComplete += onGenComplete;
                                LocationGeneratorTool.Call(genParams);
                                string genResultJson = await tcs.Task;
                                LocationGeneratorTool.OnComplete -= onGenComplete;

                                if (JsonUtils.TryDeserialize<ToolResponseContainer>(genResultJson, out var genContainer))
                                {
                                    var newLoc = genContainer.Result.Mutable.Locations.FirstOrDefault();
                                    if (newLoc != null)
                                    {
                                        // Интегрируем новые данные в кеш
                                        _cachedLocations.Add(newLoc);
                                        _cachedObjects.AddRange(genContainer.Result.Mutable.Objects);
                                        _cachedCells.AddRange(genContainer.Result.Mutable.Cells);

                                        _aggregatedLog.Add($"Generation finished. New Location ID: {newLoc.Id}");

                                        // ВАЖНО: Добавляем новую локацию в список активных ID
                                        activeLocationIds.Add(newLoc.Id);
                                        
                                        // Обновляем индексы ячеек
                                        activeZoneIndices = RefreshActiveZoneIndices(activeLocationIds);

                                        OnUpdate?.Invoke($"New location {newLoc.Id} created and added to simulation context.");
                                    }
                                }
                            }
                            break;

                        case "Check": // Проверка успеха
                            OnUpdate?.Invoke("🎲 Skill Check...");
                            int probability = ParseIntFromText(simResponse.BreakDescription);
                            int roll = GD.RandRange(1, 100);
                            bool success = probability < roll;
                            _aggregatedLog.Add(success.ToString().ToLower());
                            OnUpdate?.Invoke($"Check: {probability}% vs Roll {roll} -> {(success ? "Success" : "Fail")}");
                            break;

                        default:
                            simulationRunning = false;
                            break;
                    }

                    // 9 / 14. Обновляем карту перед следующим шагом (используя обновленный activeZoneIndices)
                    currentMap = await GenerateCurrentMap(activeZoneIndices);
                }

                // --- 19. Финальное описание (Narrative) ---
                OnUpdate?.Invoke("✍️ Writing Narrative...");
                string narrative = await GenerateNarrative();

                var translatedText = await Translate(narrative);
                OnUpdate?.Invoke(translatedText);

                // --- 20. Формирование ответа ---
                var result = new ToolResponseContainer
                {
                    ToolName = ToolName,
                    Result = new ToolResultContent
                    {
                        Mutable = new MutableData
                        {
                            Locations = _cachedLocations,
                            Objects = _cachedObjects,
                            Cells = _cachedCells
                        },
                        Immutable = new ImmutableData
                        {
                            Text = new TextEntry
                            {
                                Text = narrative,
                                Locations = activeLocationIds,
                                SimulationLog = structuredOutput
                            },
                        }
                    }
                };

                OnComplete?.Invoke(JsonUtils.Serialize(result));
            }
            catch (Exception ex)
            {
                GD.PrintErr(ex);
                OnFail?.Invoke(JsonUtils.Serialize(new { error = ex.Message }));
            }
        }

        // Этот метод вызывается ТОЛЬКО ОДИН РАЗ в начале Call
        private async Task<List<int>> DetermineActiveLocations(string userPrompt)
        {
            OnUpdate?.Invoke("🧐 Analyzing context to find active location...");

            string systemPrompt = PromptLibrary.Instance.GetPrompt(PromptType.SimulationContext);
            var extractionRequest = new LmmRequest
            {
                SystemInstruction = systemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.1f
            };

            var contextResponseJson = await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(extractionRequest);

            ContextSearchResult contextQueries;
            if (JsonUtils.TryDeserialize<ContextSearchResult>(contextResponseJson, out var contextResponse))
            {
                contextQueries = contextResponse;
            }
            else
            {
                return null;
            }

            if (contextQueries.Queries == null || contextQueries.Queries.Count == 0)
            {
                OnFail?.Invoke("No specific queries generated for context search.");
                return null;
            }

            OnUpdate?.Invoke($"🔎 Queued queries: {contextQueries.Queries.Count}");

            var queryBatchInput = new
            {
                queries = contextQueries.Queries,
                format = "{ \"location_ids\": [int] } // Return a SINGLE JSON object merging ALL unique location IDs found across all queries."
            };

            string queryToolParams = JsonUtils.Serialize(queryBatchInput);
            var tcs = new TaskCompletionSource<string>();

            Action<string> onComplete = (json) => tcs.TrySetResult(json);
            Action<string> onFail = (err) => tcs.TrySetException(new Exception(err));

            try
            {
                QueryTool.OnComplete += onComplete;
                QueryTool.OnFail += onFail;
                QueryTool.Call(queryToolParams);
                string queryResponseJson = await tcs.Task;

                if (JsonUtils.TryDeserialize<ToolResponseContainer>(queryResponseJson, out var container)
                    && container.Result?.Temporary?.Result != null)
                {
                    if (JsonUtils.TryDeserialize<LocationSearchResult>(container.Result.Temporary.Result, out var locResult))
                    {
                        if (locResult.LocationIds != null && locResult.LocationIds.Count > 0)
                        {
                            return locResult.LocationIds.Distinct().ToList();
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"DetermineActiveLocations failed: {ex.Message}");
                return null;
            }
            finally
            {
                QueryTool.OnComplete -= onComplete;
                QueryTool.OnFail -= onFail;
            }
        }

        // --- Context Building Logic (Updated to strictly use activeLocationIds) ---

        private string BuildSimulationContext(List<int> activeLocationIds, List<string> activeZoneCells, string startTime, string endTime)
        {
            var sb = new System.Text.StringBuilder();

            // Сортируем ID для детерминированного порядка вывода
            activeLocationIds.Sort();

            // Проходим только по явно заданным activeLocationIds. 
            // Не пытаемся угадать локации через индексы ячеек, используем только то, что получили на входе.
            foreach (var locId in activeLocationIds)
            {
                var location = _cachedLocations.FirstOrDefault(l => l.Id == locId);
                if (location == null) continue;

                sb.AppendLine($"***Location({locId})");

                // Получаем иерархию объектов только для этой локации
                var hierarchies = WorldStateHelper.GetLocationHierarchies(locId, _cachedLocations, _cachedObjects);

                if (hierarchies.Count == 0)
                {
                    sb.AppendLine("(No visible objects)");
                }

                foreach (var hierarchy in hierarchies)
                {
                    if (hierarchy.Count == 0) continue;
                    var rootObj = hierarchy[0];

                    string locationInfo = (rootObj.CellIndices != null && rootObj.CellIndices.Count > 0)
                        ? $" [At: {string.Join(", ", rootObj.CellIndices)}]"
                        : "";
                    string historyInfo = (rootObj.History != null && rootObj.History.Count > 0)
                        ? $" Status: [{string.Join(", ", rootObj.History)}]"
                        : "";

                    sb.AppendLine($"-Object Hierarchy({rootObj.Id}){locationInfo}{historyInfo}");

                    var childrenMap = hierarchy
                        .Where(o => o.ParentObjectId.HasValue)
                        .GroupBy(o => o.ParentObjectId.Value)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    if (childrenMap.ContainsKey(rootObj.Id))
                    {
                        foreach (var child in childrenMap[rootObj.Id])
                        {
                            PrintChildrenRecursive(sb, child, childrenMap, 1);
                        }
                    }
                }
            }
            sb.AppendLine("***end");

            return $@"
Simulation Start Time: {startTime}
Target End Time: {endTime}
Current Local Time (Last Step): {_currentTime}
Active Zone Cells: {JsonUtils.Serialize(activeZoneCells)}
Visible Objects Hierarchy (Strictly Restricted to Active Locations):
{sb.ToString()}
Request Log: {JsonUtils.Serialize(_aggregatedLog)}
";
        }

        private void PrintChildrenRecursive(System.Text.StringBuilder sb, ObjectData current, Dictionary<int, List<ObjectData>> map, int indentLevel)
        {
            string indent = new string(' ', indentLevel * 2);
            string historyInfo = JsonUtils.Serialize(current);
            sb.AppendLine($"{indent}- [ID: {current.Id}] \n{historyInfo}\n");

            if (map.ContainsKey(current.Id))
            {
                foreach (var child in map[current.Id])
                {
                    PrintChildrenRecursive(sb, child, map, indentLevel + 1);
                }
            }
        }

        // --- Helpers ---

        private List<string> RefreshActiveZoneIndices(List<int> locationIds)
        {
            // Извлекаем все ячейки, принадлежащие активным локациям
            return _cachedLocations
                .Where(l => locationIds.Contains(l.Id))
                .SelectMany(l => l.CellIndices)
                .Distinct()
                .ToList();
        }

        private void ApplyActions(List<SimulationAction> actions)
        {
            if (actions == null) return;
            foreach (var act in actions)
            {
                try
                {
                    switch (act.Type)
                    {
                        case "move_cell":
                            var moveCell = JsonSerializer.Deserialize<ActionMoveToCell>(act.ActionData.GetRawText());
                            var objC = _cachedObjects.FirstOrDefault(o => o.Id == moveCell.ObjectId);
                            if (objC != null)
                            {
                                objC.CellIndices = new List<string> { moveCell.SetId };
                                objC.ParentObjectId = null;
                            }
                            break;
                        case "move_object":
                            var moveObj = JsonSerializer.Deserialize<ActionMoveToObject>(act.ActionData.GetRawText());
                            var objO = _cachedObjects.FirstOrDefault(o => o.Id == moveObj.ObjectId);
                            if (objO != null)
                            {
                                objO.ParentObjectId = moveObj.SetId;
                                objO.CellIndices.Clear();
                            }
                            break;
                        case "expand_history":
                            var histExp = JsonSerializer.Deserialize<ActionExpandHistory>(act.ActionData.GetRawText());
                            var objH = _cachedObjects.FirstOrDefault(o => o.Id == histExp.ObjectId);
                            if (objH != null) objH.History.Add(histExp.NewText);
                            break;
                        case "update_group":
                            var grpUpd = JsonSerializer.Deserialize<ActionGroupUpdate>(act.ActionData.GetRawText());
                            foreach (var loc in _cachedLocations)
                            {
                                foreach (var grp in loc.Groups)
                                {
                                    if (grp.CellIndices.Intersect(grpUpd.CellsId).Any())
                                        grp.Description = grpUpd.NewText;
                                }
                            }
                            break;
                    }
                }
                catch (Exception e) { GD.PrintErr($"Failed to apply action {act.Type}: {e.Message}"); }
            }
        }

        private void UpdateCellTime(string cellIndex, string newTime)
        {
            var cell = _cachedCells.FirstOrDefault(c => c.Index == cellIndex);
            if (cell != null) cell.LastUpdateTime = newTime;
            else _cachedCells.Add(new CellData { Index = cellIndex, LastUpdateTime = newTime });
        }

        private async Task<Image> GenerateCurrentMap(List<string> activeZone)
        {
            if (activeZone.Count == 0) return null;
            var bounds = GetBounds(activeZone);
            var free = new List<string>();
            var occupied = new List<string>();
            var occupiedIndices = _cachedObjects.SelectMany(o => o.CellIndices).ToHashSet();

            foreach (var idx in activeZone)
            {
                if (occupiedIndices.Contains(idx)) occupied.Add(idx);
                else free.Add(idx);
            }
            return await MapGen.GenerateMapImage(free, occupied, bounds);
        }

        private Rect2I GetBounds(List<string> indices)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            foreach (var idx in indices)
            {
                var c = GridCoordinate.Parse(idx);
                if (c.X < minX) minX = c.X;
                if (c.X > maxX) maxX = c.X;
                if (c.Y < minY) minY = c.Y;
                if (c.Y > maxY) maxY = c.Y;
            }
            return new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private async Task<string> GenerateNarrative()
        {
            var request = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Narrative),
                UserPrompt = $"Full Execution Log:\n{JsonUtils.Serialize(_aggregatedLog)}",
                Temperature = 0.7f
            };
            var json = await LmmFactory.Instance.GetProvider(LmmModelType.Smart).GenerateAsync(request);
            if (JsonUtils.TryDeserialize<NarrativeResponse>(json, out var resp)) return resp.Text;
            return "Simulation completed, but narrative generation failed.";
        }
        
        private async Task<string> Translate(string text)
        {
            var request = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Translator),
                UserPrompt = text,
                Temperature = 1f
            };
            return await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(request);
        }

        private string ParseCellIndexFromText(string text)
        {
            int start = text.IndexOf('$');
            int end = text.LastIndexOf('$');
            if (start != -1 && end != -1 && end > start) return text.Substring(start + 1, end - start - 1);
            return null;
        }

        private int ParseIntFromText(string text)
        {
            string clean = new string(text.Where(char.IsDigit).ToArray());
            if (int.TryParse(clean, out int val)) return val;
            return 50;
        }
    }
}