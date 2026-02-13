using Godot;
using RPG.Models;
using RPG.AI;
using RPG.AI.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Environment = System.Environment;

namespace RPG.Core
{
    public class ToolRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("tool")]
        public string Tool { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("params")]
        public string Params { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("think")]
        public string Think { get; set; }
    }

    public partial class ToolController : Node
    {
        public event Action<string> OnUIUpdate;
        public event Action OnTurnComplete;

        [Export] public Godot.Collections.Array<Node> RegisteredToolsNodes;

        private Dictionary<string, ITool> _tools = new();
        private List<string> _executionLog = new();

        public bool HasPendingChanges => _executionLog.Count > 1;

        public override void _Ready()
        {
            foreach (var node in RegisteredToolsNodes)
            {
                if (node is ITool tool) RegisterTool(tool);
            }

            InitializeModel(LmmModelType.Fast);
            InitializeModel(LmmModelType.Smart);
            InitializeModel(LmmModelType.Local);

            GD.Print("ToolController: AI Models initialized.");
        }

        private void InitializeModel(LmmModelType type)
        {
            var provider = LmmFactory.Instance.GetProvider(type);
            if (provider != null)
            {
                provider.OnUpdate += (msg) => OnUIUpdate?.Invoke(msg);
            }
        }

        private void RegisterTool(ITool tool)
        {
            if (_tools.ContainsKey(tool.ToolName)) return;
            _tools[tool.ToolName] = tool;
            tool.OnUpdate += (msg) => OnUIUpdate?.Invoke(msg);
        }

        public void StartTurn(string userInput)
        {
            if (HasPendingChanges)
            {
                OnUIUpdate?.Invoke("\n[System] Pending changes. /accept or /reject.\n");
                return;
            }

            GD.Print($"--- New Turn ---");
            _executionLog.Clear();
            _executionLog.Add(userInput);

            ProcessStep();
        }

        // 1. Спрашиваем модель и 2. Получаем ToolRequest
        private async void ProcessStep()
        {
            string contextStr = string.Join("\n---\n", _executionLog);
            string systemPrompt = PromptLibrary.Instance.GetPrompt(PromptType.NextTool, contextStr);

            var request = new LmmRequest
            {
                SystemInstruction = systemPrompt,
                UserPrompt = contextStr,
                Temperature = 0.1f
            };

            OnUIUpdate?.Invoke("⚙️ Selecting tool...");

            var provider = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            string lmmResponseJson = await provider.GenerateAsync(request);

            if (string.IsNullOrEmpty(lmmResponseJson) || !JsonUtils.TryDeserialize<ToolRequest>(lmmResponseJson, out var toolRequest))
            {
                GD.PrintErr($"Failed to parse decision: {lmmResponseJson}");
                OnTurnComplete?.Invoke();
                return;
            }

            OnUIUpdate?.Invoke($"Calling: {toolRequest.Tool}");
            OnUIUpdate?.Invoke($"Reason: {toolRequest.Think}");

            ExecuteTool(toolRequest);
        }

        // 3. Выполняем выбранный инструмент
        private void ExecuteTool(ToolRequest request)
        {
            if (_tools.TryGetValue(request.Tool, out ITool tool))
            {
                Action<string> onComplete = null;
                onComplete = (result) =>
                {
                    tool.OnComplete -= onComplete;
                    _executionLog.Add(result);
                    
                    // 4. Сразу вызываем FinalTool с результатом предыдущего шага
                    RunFinalTool(result);
                };

                tool.OnComplete += onComplete;
                tool.Call(request.Params);
            }
            else
            {
                GD.PrintErr($"Tool missing: {request.Tool}");
                OnTurnComplete?.Invoke();
            }
        }

        private void RunFinalTool(string inputParams)
        {
            if (_tools.TryGetValue("FinalTool", out ITool finalTool))
            {
                OnUIUpdate?.Invoke("📝 Finalizing...");
                
                Action<string> onFinalComplete = null;
                onFinalComplete = (result) =>
                {
                    finalTool.OnComplete -= onFinalComplete;
                    _executionLog.Add(result);
                    OnTurnComplete?.Invoke();
                };

                finalTool.OnComplete += onFinalComplete;
                finalTool.Call(inputParams);
            }
            else
            {
                GD.PrintErr("FinalTool not found!");
                OnTurnComplete?.Invoke();
            }
        }

        public void CommitTurn()
        {
            if (!HasPendingChanges) return;
            StateManager.Instance.ApplyChanges(_executionLog);
            _executionLog.Clear();
            OnUIUpdate?.Invoke("\n[System] Saved.\n");
            OnTurnComplete?.Invoke();
        }

        public void DiscardTurn()
        {
            _executionLog.Clear();
            OnUIUpdate?.Invoke("\n[System] Discarded.\n");
            OnTurnComplete?.Invoke();
        }
    }
}