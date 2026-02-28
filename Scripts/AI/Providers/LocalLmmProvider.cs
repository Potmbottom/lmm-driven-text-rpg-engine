using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Godot;
using RPG.AI.Core;
using RPG.Core;
using System.Text.Json.Serialization;
using RPG.AI.Models;
using HttpClient = System.Net.Http.HttpClient;

namespace RPG.AI.Providers
{
    public class LocalLmmProvider : ILmmProvider
    {
        public event Action<string> OnUpdate;
        private readonly string _baseUrl;
        private readonly HttpClient _httpClient;
        private const string DEFAULT_MODEL = "local-model"; 

        public LocalLmmProvider(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            if (!_baseUrl.EndsWith("/v1"))
            {
                _baseUrl += "/v1";
            }

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
        }

        public async Task<string> GenerateAsync(LmmRequest request)
        {
            var url = $"{_baseUrl}/chat/completions";
            var jsonBody = BuildOpenAiRequestBody(request, stream: false);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            try 
            {
                var response = await _httpClient.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    GD.PrintErr($"Local LLM Error: {response.StatusCode} - {responseString}");
                    return null;
                }

                if (JsonUtils.TryDeserialize<ProviderModels.OpenAiResponseRoot>(responseString, out var root))
                {
                    ProcessUsage(root.Usage);
                    if (root.Choices != null && root.Choices.Count > 0)
                    {
                        return root.Choices[0].Message?.Content;
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"Local LLM Connection Failed: {ex.Message}");
            }

            return null;
        }

        public async IAsyncEnumerable<string> StreamGenerateAsync(LmmRequest request)
        {
            var url = $"{_baseUrl}/chat/completions";
            var jsonBody = BuildOpenAiRequestBody(request, stream: true);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                GD.PrintErr($"Local LLM Stream Error: {response.StatusCode}");
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6).Trim();
                    if (data == "[DONE]") break;

                    if (JsonUtils.TryDeserialize<ProviderModels.OpenAiStreamResponseRoot>(data, out var chunkObj))
                    {
                        var textChunk = chunkObj.Choices?[0].Delta?.Content;
                        if (!string.IsNullOrEmpty(textChunk))
                        {
                            yield return textChunk;
                        }
                    }
                }
            }
        }

        public Task<float[]> GetEmbeddingAsync(string text)
        {
            GD.PrintErr("Embeddings not yet implemented for Local Provider");
            return Task.FromResult<float[]>(null);
        }

        public void PrintTokens()
        {
            //TODO
        }

        private void ProcessUsage(ProviderModels.OpenAiUsage usage)
        {
            if (usage != null)
            {
                var message = $"[Local] Tokens: {usage.PromptTokens} sent, {usage.CompletionTokens} received. Total: {usage.TotalTokens}";
                OnUpdate?.Invoke(message);
            }
        }

        private string BuildOpenAiRequestBody(LmmRequest req, bool stream)
        {
            var messages = new List<object>();
            if (!string.IsNullOrEmpty(req.SystemInstruction))
            {
                messages.Add(new { role = "system", content = req.SystemInstruction });
            }
            
            messages.Add(new { role = "user", content = req.UserPrompt });

            var openAiReq = new
            {
                model = DEFAULT_MODEL,
                messages = messages,
                temperature = req.Temperature,
                stream = stream,
            };

            return JsonUtils.Serialize(openAiReq);
        }
    }
}