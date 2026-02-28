using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RPG.AI.Models;

public static class ProviderModels
{
    public class OpenAiResponseRoot
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice> Choices { get; set; }
        [JsonPropertyName("usage")]
        public OpenAiUsage Usage { get; set; }
    }

    public class OpenAiStreamResponseRoot
    {
        [JsonPropertyName("choices")]
        public List<OpenAiStreamChoice> Choices { get; set; }
    }

    public class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage Message { get; set; }
    }

    public class OpenAiStreamChoice
    {
        [JsonPropertyName("delta")]
        public OpenAiMessage Delta { get; set; }
    }

    public class OpenAiMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }
        [JsonPropertyName("content")]
        public string Content { get; set; }
    }

    public class OpenAiUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}