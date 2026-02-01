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

namespace RPG.Tools
{
    public partial class QueryTool : Node, ITool
    {
        public string ToolName => "QueryTool";

        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;

        // Внутренние модели для общения с LMM внутри инструмента
        private class OrchestratorDecision
        {
            [JsonPropertyName("tool_name")] public string ToolName { get; set; }
            [JsonPropertyName("think")] public string Think { get; set; }
            [JsonPropertyName("payload")] public string Payload { get; set; }
        }

        private class SelectionResult
        {
            [JsonPropertyName("selected_id")] public int? SelectedId { get; set; }
            [JsonPropertyName("selected_index")] public int? SelectedIndex { get; set; }
        }

        private class FinalResponse
        {
            [JsonPropertyName("result")] public object Result { get; set; }
        }

        // Состояние выполнения
        private List<string> _aggregatedHistory = new();
        private HashSet<int> _extractedEventIndices = new();
        private const int MAX_STEPS = 15;
        
        public async void Call(string parameters)
        {
            try
            {
                _aggregatedHistory.Clear();
                _extractedEventIndices.Clear();
                
                // Добавляем первичный запрос в историю. Модель сама разберет JSON если он там есть.
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
            int stepCount = 0;
            bool isComplete = false;

            while (!isComplete && stepCount < MAX_STEPS)
            {
                stepCount++;
                
                // 1. Запрос к Оркестратору
                string historyContext = string.Join("\n---\n", _aggregatedHistory);
                var request = new LmmRequest
                {
                    SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.QueryOrchestrator),
                    UserPrompt = historyContext,
                    Temperature = 0.2f
                };

                string responseJson = await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(request);
                
                if (!JsonUtils.TryDeserialize<OrchestratorDecision>(responseJson, out var decision))
                {
                    _aggregatedHistory.Add($"[SYSTEM_ERROR]: Failed to parse decision JSON: {responseJson}");
                    continue;
                }

                OnUpdate?.Invoke($"🤔 {decision.Think}");
                OnUpdate?.Invoke($"🛠️ Executing: {decision.ToolName}...");

                // 2. Выполнение выбранного инструмента
                string toolResult = await ExecuteSubTool(decision);

                // 3. Запись результата
                _aggregatedHistory.Add($"[THINK]: {decision.Think} \n[TOOL_RESULT ({decision.ToolName})]: {toolResult}");

                // 4. Проверка на завершение
                if (decision.ToolName.ToLower() == "final")
                {
                    isComplete = true;
                    ProcessFinalResult(toolResult);
                }
            }

            if (!isComplete && stepCount >= MAX_STEPS)
            {
                OnUpdate?.Invoke("⚠️ Loop limit reached. Generating partial result...");
                string finalRes = await GenerateFinalResponse();
                ProcessFinalResult(finalRes);
            }
        }

        private async Task<string> ExecuteSubTool(OrchestratorDecision decision)
        {
            var world = StateManager.Instance.CurrentWorld;

            switch (decision.ToolName.ToLower())
            {
                case "get_location_by_id":
                    if (int.TryParse(decision.Payload, out int locId))
                    {
                        var loc = world.Locations.FirstOrDefault(l => l.Id == locId);
                        return loc != null ? JsonUtils.Serialize(loc) : "Location not found.";
                    }
                    return "Invalid ID format.";

                case "get_object_by_id":
                    if (int.TryParse(decision.Payload, out int objId))
                    {
                        var obj = world.Objects.FirstOrDefault(o => o.Id == objId);
                        return obj != null ? JsonUtils.Serialize(obj) : "Object not found.";
                    }
                    return "Invalid ID format.";

                // --- НОВЫЙ ИНСТРУМЕНТ: Рекурсивный поиск вложенных объектов ---
                case "get_nested_objects":
                    if (int.TryParse(decision.Payload, out int parentId))
                    {
                        var nestedObjects = GetRecursiveChildObjects(parentId, world);
                        return nestedObjects.Count > 0 
                            ? JsonUtils.Serialize(nestedObjects) 
                            : "No nested objects found (Empty inventory/container).";
                    }
                    return "Invalid ID format for nested objects.";

                case "get_location_by_cell":
                    string cellIndex = decision.Payload.Trim();
                    var locByCell = world.Locations.FirstOrDefault(l => l.CellIndices.Contains(cellIndex));
                    return locByCell != null ? JsonUtils.Serialize(locByCell) : $"Location with cell {cellIndex} not found.";

                case "get_recent_events":
                    if (int.TryParse(decision.Payload, out int count))
                    {
                        var events = world.History.Texts.TakeLast(Math.Min(count, world.History.Texts.Count)).ToList();
                        
                        int total = world.History.Texts.Count;
                        for (int i = 0; i < events.Count; i++) 
                            _extractedEventIndices.Add(total - events.Count + i);
                            
                        return events.Count > 0 ? JsonUtils.Serialize(events) : "No recent events.";
                    }
                    return "Invalid count format.";

                case "find_object_by_similarity":
                    return await HandleVectorSearch(decision.Payload, SearchType.Object);

                case "find_location_by_similarity":
                    return await HandleVectorSearch(decision.Payload, SearchType.Location);

                case "find_text_by_similarity":
                    return await HandleVectorSearch(decision.Payload, SearchType.Event);

                case "find_object_in_location":
                    // Ожидаемый формат: "LocationID | Description"
                    var parts = decision.Payload.Split('|');
                    if (parts.Length < 2) return "Invalid payload format. Use: 'LocationID | ObjectDescription'";
                    
                    if (!int.TryParse(parts[0].Trim(), out int targetLocId)) return "Invalid Location ID.";
                    string searchDesc = parts[1].Trim();

                    var targetLoc = world.Locations.FirstOrDefault(l => l.Id == targetLocId);
                    if (targetLoc == null) return "Location not found.";

                    // Получаем ID всех объектов, которые принадлежат этой локации (напрямую или иерархически)
                    var allowedObjectIds = GetObjectsInLocation(targetLoc, world);
                    
                    if (allowedObjectIds.Count == 0) return "Location contains no objects.";

                    // Выполняем векторный поиск с фильтрацией по ID
                    return await HandleVectorSearch(searchDesc, SearchType.Object, allowedObjectIds);

                case "final":
                    // Запускаем генерацию финального ответа
                    return await GenerateFinalResponse();

                default:
                    return $"Unknown tool: {decision.ToolName}";
            }
        }

        /// <summary>
        /// Рекурсивно находит все объекты, вложенные в указанный ParentID.
        /// </summary>
        private List<ObjectData> GetRecursiveChildObjects(int rootParentId, WorldState world)
        {
            var result = new List<ObjectData>();
            var openList = new Queue<int>();
            var visited = new HashSet<int>();

            openList.Enqueue(rootParentId);
            visited.Add(rootParentId); // Чтобы не уйти в бесконечный цикл если структура зациклена

            while (openList.Count > 0)
            {
                int currentParent = openList.Dequeue();

                // Ищем прямых потомков
                var children = world.Objects
                    .Where(o => o.ParentObjectId == currentParent)
                    .ToList();

                foreach (var child in children)
                {
                    if (!visited.Contains(child.Id))
                    {
                        visited.Add(child.Id);
                        result.Add(child);
                        openList.Enqueue(child.Id); // Добавляем в очередь для поиска "внуков"
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Возвращает список ID всех объектов, находящихся в указанной локации.
        /// Учитывает объекты, привязанные к ячейкам локации, и их дочерние объекты.
        /// </summary>
        private List<int> GetObjectsInLocation(LocationData loc, WorldState world)
        {
            var allowedIds = new HashSet<int>();
            var locCellIndices = new HashSet<string>(loc.CellIndices);

            // 1. Находим корневые объекты, которые находятся в ячейках, принадлежащих локации
            foreach (var obj in world.Objects)
            {
                if (obj.CellIndices != null && obj.CellIndices.Any(c => locCellIndices.Contains(c)))
                {
                    allowedIds.Add(obj.Id);
                }
            }

            // 2. Итеративно добавляем дочерние объекты (иерархия)
            // Если родитель объекта уже в списке разрешенных, то и сам объект добавляется
            bool addedNew;
            do
            {
                addedNew = false;
                foreach (var obj in world.Objects)
                {
                    if (allowedIds.Contains(obj.Id)) continue; // Уже добавлен

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
            // 1. Поиск по базе с опциональной фильтрацией по ID
            var results = await StateManager.Instance.VectorDB.Search(query, type, limit: 5, allowedIds: allowedIds);

            // Фильтруем события, которые уже были извлечены (чтобы не дублировать контекст)
            if (type == SearchType.Event)
            {
                results = results.Where(r => !_extractedEventIndices.Contains(r.Id)).ToList();
            }

            if (results.Count == 0) return "No matches found in VectorDB.";

            // Если результат один - возвращаем сразу
            if (results.Count == 1)
            {
                return await FetchEntityById(results[0].Id, type);
            }

            // 2. Селектор (LMM выбирает лучший вариант из найденных кандидатов)
            string candidatesJson = JsonUtils.Serialize(results.Select(r => new { id = r.Id, content = r.Content, score = r.HybridScore }));
            
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
            // Формируем финальный ответ по запросу пользователя на основе всей истории
            var request = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.QueryFinalizer),
                UserPrompt = string.Join("\n---\n", _aggregatedHistory),
                Temperature = 0.5f
            };

            return await LmmFactory.Instance.GetProvider(LmmModelType.Smart).GenerateAsync(request);
        }

        private void ProcessFinalResult(string finalJson)
        {
            string finalResultString = "";

            if (JsonUtils.TryDeserialize<FinalResponse>(finalJson, out var response) && response.Result != null)
            {
                finalResultString = response.Result.ToString();
                if (response.Result is System.Text.Json.JsonElement element)
                {
                    finalResultString = element.ToString();
                }
            }
            else
            {
                finalResultString = finalJson;
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

            OnUpdate?.Invoke("✅ Query Complete.");
            OnUpdate?.Invoke(finalResultString);
            OnComplete?.Invoke(JsonUtils.Serialize(container));
        }
    }
}