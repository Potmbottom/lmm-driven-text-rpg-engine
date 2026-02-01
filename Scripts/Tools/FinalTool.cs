using RPG.Core;
using System;
using System.Text.Json;

namespace RPG.Tools
{
    public class FinalTool : ITool
    {
        public string ToolName => "FinalTool";

        public event Action<string> OnUpdate;
        public event Action<string> OnComplete;
        public event Action<string> OnFail;

        public void Call(string parameters)
        {
            // Формируем ответ
            var response = new 
            {
                tool = ToolName,
                result = "Cycle successfully terminated"
            };

            string jsonResponse = JsonUtils.Serialize(response);
            OnUpdate?.Invoke(jsonResponse);
            OnComplete?.Invoke(jsonResponse);
        }
    }
}