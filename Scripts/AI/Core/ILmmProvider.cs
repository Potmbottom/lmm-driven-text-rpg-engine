using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace RPG.AI.Core
{
    public interface ILmmProvider
    {
        Task<string> GenerateAsync(LmmRequest request);
        IAsyncEnumerable<string> StreamGenerateAsync(LmmRequest request);
        Task<float[]> GetEmbeddingAsync(string text);
    }

    public class LmmRequest
    {
        public string SystemInstruction { get; set; }
        public string UserPrompt { get; set; }
        public List<Image> Images { get; set; } 
        public float Temperature { get; set; } = 1f;
    }

    public enum LmmModelType
    {
        Fast,
        Smart,
        Local,
        Embedding
    }
}