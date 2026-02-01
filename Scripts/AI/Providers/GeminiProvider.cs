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

namespace RPG.AI.Providers
{
    public class GeminiProvider : ILmmProvider
    {
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly HttpClient _httpClient;
        
        // Адрес для модели эмбеддингов
        private const string EmbeddingModel = "text-embedding-004";
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        public GeminiProvider(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(10); 
        }

        public async Task<string> GenerateAsync(LmmRequest request)
        {
            var url = $"{BaseUrl}/{_modelName}:generateContent?key={_apiKey}";
            var jsonBody = BuildGeminiRequestBody(request);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                GD.PrintErr($"Gemini Error: {response.StatusCode} - {responseString}");
                return null;
            }

            return ParseGeminiResponse(responseString);
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
            StringBuilder buffer = new StringBuilder();

            while ((line = await reader.ReadLineAsync()) != null)
            {
                buffer.Append(line);
                string currentBuffer = buffer.ToString().Trim();

                if (currentBuffer.StartsWith("{") && currentBuffer.EndsWith("}"))
                {
                    string cleanJson = currentBuffer.TrimStart('[').TrimEnd(']').TrimEnd(',');

                    if (JsonUtils.TryDeserialize<GeminiResponseRoot>(cleanJson, out var chunkObj))
                    {
                        string textChunk = ExtractTextFromRoot(chunkObj);
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

        // --- DTOs for Embedding ---
        private class EmbeddingResponseRoot
        {
            [System.Text.Json.Serialization.JsonPropertyName("embedding")]
            public EmbeddingData Embedding { get; set; }
        }

        private class EmbeddingData
        {
            [System.Text.Json.Serialization.JsonPropertyName("values")]
            public float[] Values { get; set; }
        }

        // --- Helpers ---

        private string BuildGeminiRequestBody(LmmRequest req)
        {
            // Собираем части сообщения (текст + картинки)
            var parts = new List<object>();

            // 1. Добавляем текст
            parts.Add(new { text = req.UserPrompt });

            // 2. Добавляем картинки, если есть
            if (req.Images != null && req.Images.Count > 0)
            {
                foreach (var img in req.Images)
                {
                    byte[] pngBytes = img.SavePngToBuffer();
                    string base64Data = Convert.ToBase64String(pngBytes);

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

            // Формируем структуру Gemini API
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
                    responseMimeType = "application/json"
                }
            };
            return JsonUtils.Serialize(geminiReq);
        }

        private string ParseGeminiResponse(string json)
        {
            if (JsonUtils.TryDeserialize<GeminiResponseRoot>(json, out var root))
            {
                return ExtractTextFromRoot(root);
            }

            return null;
        }

        private string ExtractTextFromRoot(GeminiResponseRoot root)
        {
            if (root?.Candidates != null && root.Candidates.Count > 0)
            {
                var parts = root.Candidates[0].Content?.Parts;
                if (parts != null && parts.Count > 0)
                {
                    return parts[0].Text;
                }
            }

            return "";
        }

        private class GeminiResponseRoot
        {
            public List<Candidate> Candidates { get; set; }
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
            public string Text { get; set; }
        }
    }
}