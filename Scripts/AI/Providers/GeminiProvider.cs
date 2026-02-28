using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Godot;
using RPG.AI.Core;
using RPG.Core;
using HttpClient = System.Net.Http.HttpClient;
using System.Text.Json.Serialization;

namespace RPG.AI.Providers
{
    public enum GeminiThinkingLevel
    {
        high, medium, low, minimal
    }
    
    public class GeminiProvider : ILmmProvider
    {
        public event Action<string> OnUpdate;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly HttpClient _httpClient;

        private const string EmbeddingModel = "gemini-embedding-001";
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        private int _sendTokens;
        private int _receivedTokens;

        public GeminiProvider(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
        }

        public void PrintTokens()
        {
            OnUpdate.Invoke($"[ToolComplete] Tokens send {_sendTokens}. Tokens received {_receivedTokens} . Total {_sendTokens + _receivedTokens}");
            _receivedTokens = 0;
            _sendTokens = 0;
        }

        public async Task<string> GenerateAsync(LmmRequest request)
        {
            var url = $"{BaseUrl}/{_modelName}:generateContent?key={_apiKey}";
            var jsonBody = BuildGeminiRequestBody(request);
            var content = new StringContent(jsonBody, Encoding.UTF8, request.Format);

            var response = await _httpClient.PostAsync(url, content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                GD.PrintErr($"Gemini Error: {response.StatusCode} - {responseString}");
                return null;
            }

            if (JsonUtils.TryDeserialize<GeminiResponseRoot>(responseString, out var root))
            {
                ProcessUsageMetadata(root.UsageMetadata);
                return ExtractTextFromRoot(root);
            }

            return null;
        }

        public async IAsyncEnumerable<string> StreamGenerateAsync(LmmRequest request)
        {
            var url = $"{BaseUrl}/{_modelName}:streamGenerateContent?key={_apiKey}";
            var jsonBody = BuildGeminiRequestBody(request);
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                GD.PrintErr($"Gemini Stream Error: {error}");
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            string line;
            var buffer = new StringBuilder();

            while ((line = await reader.ReadLineAsync()) != null)
            {
                buffer.Append(line);
                var currentBuffer = buffer.ToString().Trim();

                if (currentBuffer.StartsWith("{") && currentBuffer.EndsWith("}"))
                {
                    var cleanJson = currentBuffer.TrimStart('[').TrimEnd(']').TrimEnd(',');

                    if (JsonUtils.TryDeserialize<GeminiResponseRoot>(cleanJson, out var chunkObj))
                    {
                        ProcessUsageMetadata(chunkObj.UsageMetadata);

                        var textChunk = ExtractTextFromRoot(chunkObj);
                        if (!string.IsNullOrEmpty(textChunk))
                        {
                            yield return textChunk;
                        }

                        buffer.Clear();
                    }
                }
                else if (currentBuffer == "[" || currentBuffer == "]")
                {
                    buffer.Clear();
                }
            }
        }

        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            var url = $"{BaseUrl}/{EmbeddingModel}:embedContent?key={_apiKey}";

            var payload = new
            {
                content = new { parts = new[] { new { text = text } } }
            };

            var content = new StringContent(JsonUtils.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                GD.PrintErr($"Gemini Embedding Error: {response.StatusCode} - {responseString}");
                return null;
            }

            if (JsonUtils.TryDeserialize<EmbeddingResponseRoot>(responseString, out var root))
            {
                return root.Embedding?.Values;
            }

            return null;
        }

        private void ProcessUsageMetadata(UsageMetadata metadata)
        {
            if (metadata != null)
            {
                _sendTokens += metadata.PromptTokenCount;
                _receivedTokens += metadata.CandidatesTokenCount;
                var message = $"[System] Tokens send {metadata.PromptTokenCount}. Tokens received {metadata.CandidatesTokenCount} . Total {metadata.TotalTokenCount}";
                OnUpdate?.Invoke(message);
            }
        }

        private string BuildGeminiRequestBody(LmmRequest req)
        {
            var parts = new List<object>();
            parts.Add(new { text = req.UserPrompt });

            if (req.Images != null && req.Images.Count > 0)
            {
                foreach (var img in req.Images)
                {
                    if (img == null) continue;
                    var pngBytes = img.SavePngToBuffer();
                    var base64Data = Convert.ToBase64String(pngBytes);

                    parts.Add(new
                    {
                        inline_data = new
                        {
                            mime_type = "image/png",
                            data = base64Data
                        }
                    });
                }
            }

            var geminiReq = new
            {
                contents = new[]
                {
                    new { role = "user", parts = parts }
                },
                system_instruction = new
                {
                    parts = new[] { new { text = req.SystemInstruction ?? "You are a helpful assistant." } }
                },
                
                generationConfig = new
                {
                    temperature = req.Temperature,
                    responseMimeType = req.Format, 
                    thinkingConfig = new
                    {
                        includeThoughts = false,
                        thinkingLevel = req.ThinkingLevel.ToString()
                    }
                }
            };
            return JsonUtils.Serialize(geminiReq);
        }

        private string ExtractTextFromRoot(GeminiResponseRoot root)
        {
            if (root?.Candidates != null && root.Candidates.Count > 0)
            {
                var parts = root.Candidates[0].Content?.Parts;
                if (parts != null && parts.Count > 0)
                {
                    foreach (var part in parts)
                    {
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            return part.Text;
                        }
                    }
                }
            }
            return "";
        }

        // --- DTOs ---

        private class EmbeddingResponseRoot
        {
            [JsonPropertyName("embedding")]
            public EmbeddingData Embedding { get; set; }
        }

        private class EmbeddingData
        {
            [JsonPropertyName("values")]
            public float[] Values { get; set; }
        }

        private class GeminiResponseRoot
        {
            public List<Candidate> Candidates { get; set; }
            
            [JsonPropertyName("usageMetadata")]
            public UsageMetadata UsageMetadata { get; set; }
        }

        private class UsageMetadata
        {
            [JsonPropertyName("promptTokenCount")]
            public int PromptTokenCount { get; set; }

            [JsonPropertyName("candidatesTokenCount")]
            public int CandidatesTokenCount { get; set; }

            [JsonPropertyName("totalTokenCount")]
            public int TotalTokenCount { get; set; }
        }

        private class Candidate
        {
            public Content Content { get; set; }
        }

        private class Content
        {
            public List<Part> Parts { get; set; }
        }

        private class Part
        {
            [JsonPropertyName("text")]
            public string Text { get; set; }
            [JsonPropertyName("thought")]
            public bool IsThought { get; set; }
        }
    }
}