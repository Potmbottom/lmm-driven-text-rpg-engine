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
    }

    public partial class ToolController : Node
    {
        public event Action<string> OnUIUpdate;
        public event Action OnTurnComplete;

        // Список инструментов теперь настраивается в редакторе или ищется в детях
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
        }

        private void RegisterTool(ITool tool)
        {
            if (_tools.ContainsKey(tool.ToolName)) return;
            _tools[tool.ToolName] = tool;

            // Подписываемся сразу при регистрации, чтобы логи проходили всегда
            tool.OnUpdate += (msg) => OnUIUpdate?.Invoke(msg);

            GD.Print($"Controller registered tool: {tool.ToolName}");
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

            ProcessNextStep();
        }

        private async void ProcessNextStep()
        {
            string systemPrompt = PromptLibrary.Instance.GetPrompt(PromptType.NextTool, string.Join(Environment.NewLine, _executionLog));
            string contextStr = string.Join("\n---\n", _executionLog);

            var request = new LmmRequest
            {
                SystemInstruction = systemPrompt,
                UserPrompt = contextStr,
                Temperature = 0.0f
            };

            OnUIUpdate?.Invoke("⚙️...");

            var provider = LmmFactory.Instance.GetProvider(LmmModelType.Fast);
            string lmmResponseJson = await provider.GenerateAsync(request);

            if (string.IsNullOrEmpty(lmmResponseJson))
            {
                OnUIUpdate?.Invoke("\n[Error] AI silent.");
                OnTurnComplete?.Invoke();
                return;
            }

            if (!JsonUtils.TryDeserialize<ToolRequest>(lmmResponseJson, out var toolRequest))
            {
                GD.PrintErr($"Failed to parse decision: {lmmResponseJson}");
                OnTurnComplete?.Invoke();
                return;
            }

            OnUIUpdate?.Invoke($"Calling: {toolRequest.Tool}");
            // OnUIUpdate?.Invoke($"Reason: {toolRequest.Params}"); // Можно раскомментировать, если нужно видеть параметры вызова

            if (_tools.TryGetValue(toolRequest.Tool, out ITool tool))
            {
                Action<string> completeHandler = null;

                // OnUpdate больше не трогаем здесь, он подписан в RegisterTool
                completeHandler = (jsonResult) =>
                {
                    tool.OnComplete -= completeHandler;

                    _executionLog.Add(jsonResult);

                    if (toolRequest.Tool == "final_tool" || toolRequest.Tool == "LocationGeneration") OnTurnComplete?.Invoke();
                    else ProcessNextStep();
                };

                tool.OnComplete += completeHandler;
                tool.Call(toolRequest.Params);
            }
            else
            {
                GD.PrintErr($"Tool missing: {toolRequest.Tool}");
                if (_tools.ContainsKey("final_tool"))
                    _tools["final_tool"].Call("Error: Tool not found");
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