using RPG.Core;
using RPG.Models;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RPG.AI;
using RPG.AI.Core;

namespace RPG.Tools
{
    public partial class FinalTool : Node, ITool
    {
        public string ToolName => "FinalTool";

        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;

        public async void Call(string parameters)
        {
            try
            {
                var container = JsonSerializer.Deserialize<ToolResponseContainer>(parameters);

                if (container == null)
                {
                    OnFail?.Invoke("Failed to parse ToolResponseContainer");
                    return;
                }

                string narrativeResult = "";

                // Execute logic based on which tool produced the data
                var textCombined = container.Result.Immutable.Text.Text;
                switch (container.ToolName)
                {
                    case "Simulation":
                    case "Generation":
                        narrativeResult = await CallTranslate(textCombined);
                        break;

                    case "QueryTool":
                        narrativeResult = await CallTranslate(container.Result.Temporary.Result);
                        break;

                    default:
                        narrativeResult = $"Processing completed by unknown tool: {container.ToolName}";
                        break;
                }

                // Final response wrapper
                var response = new ToolResponseContainer
                {
                    ToolName = ToolName,
                    Result = new ToolResultContent()
                };

                string jsonResponse = JsonSerializer.Serialize(response);
                OnUpdate?.Invoke(narrativeResult);
                OnComplete?.Invoke(jsonResponse);
            }
            catch (Exception ex)
            {
                OnFail?.Invoke($"FinalTool Error: {ex.Message}");
            }
        }

        private async Task<string> CallTranslate(string value)
        {
            var translateReq = new LmmRequest
            {
                SystemInstruction = PromptLibrary.Instance.GetPrompt(PromptType.Translator),
                UserPrompt = $"{value}",
                Temperature = 1f
            };
            return await LmmFactory.Instance.GetProvider(LmmModelType.Fast).GenerateAsync(translateReq);
        }
    }
}