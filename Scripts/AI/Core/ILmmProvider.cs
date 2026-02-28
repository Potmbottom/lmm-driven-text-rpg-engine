using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RPG.AI.Providers;

namespace RPG.AI.Core
{
    public interface ILmmProvider
    {
        event Action<string> OnUpdate;
        Task<string> GenerateAsync(LmmRequest request);
        IAsyncEnumerable<string> StreamGenerateAsync(LmmRequest request);
        Task<float[]> GetEmbeddingAsync(string text);
        void PrintTokens();
    }

    public class LmmRequest
    {
        public string SystemInstruction { get; set; }
        public string UserPrompt { get; set; }
        public List<Image> Images { get; set; } 
        public float Temperature { get; set; } = 1f;
        public GeminiThinkingLevel ThinkingLevel { get; set; }
        public string Format { get; set; } = "application/json";
    }

    public enum LmmModelType
    {
        Fast,
        Smart,
        Local,
        Embedding
    }
}