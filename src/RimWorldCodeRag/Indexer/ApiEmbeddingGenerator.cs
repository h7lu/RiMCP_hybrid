using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Retrieval;

namespace RimWorldCodeRag.Indexer;

internal sealed class ApiEmbeddingResponse
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("data")]
    public List<ApiEmbeddingData>? Data { get; set; }

    [JsonPropertyName("usage")]
    public ApiEmbeddingUsage? Usage { get; set; }
}

internal sealed class ApiEmbeddingData
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

internal sealed class ApiEmbeddingUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

internal sealed class ApiEmbeddingGenerator : IEmbeddingGenerator, IQueryEmbeddingGenerator
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _modelName;

    public int PreferredBatchSize { get; private set; } = 32;
    public bool Available { get; private set; }
    public ApiEmbeddingGenerator(string? apiUrl, string? apiKey, string? modelName, int batchSize = 32)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)||string.IsNullOrWhiteSpace(apiKey)||string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Api information required, url&key&modelname");
        }
        _httpClient = new HttpClient();
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _modelName = modelName;
        PreferredBatchSize = batchSize;
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public async ValueTask<float[]> GenerateEmbeddingAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        var result = await GenerateEmbeddingsAsync([chunk], cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    public async ValueTask<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<float[]>();
        }


        var allVectors = new List<float[]>(chunks.Count);


        // 分批处理以避免API限制和超时
        for (int i = 0; i < chunks.Count; i += PreferredBatchSize)
        {
            var size = Math.Min(PreferredBatchSize, chunks.Count - i);
            IReadOnlyList<ChunkRecord> batch = chunks.Slice(i, size);

            var texts = batch.Select(chunk =>
            {
                var text = !string.IsNullOrWhiteSpace(chunk.Preview) ? chunk.Preview : chunk.Text;
                return $"passage: {text?.Trim() ?? ""}";
            }).ToList();

            var batchVectors = await ProcessBatchAsync(texts, cancellationToken);
            allVectors.AddRange(batchVectors);

            // 添加延迟以避免API限流
            if (i + PreferredBatchSize < chunks.Count)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return allVectors;
    }

    private async Task<IReadOnlyList<float[]>> ProcessBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = _modelName,
            input = texts,

        };

        using var requestContent = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");
        const int maxRetries = 3;
        for (int retry = 0; retry <= maxRetries; retry++)
        {
            try
            {
                using var response = await _httpClient.PostAsync(_apiUrl, requestContent, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadFromJsonAsync<ApiEmbeddingResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (responseBody == null || responseBody.Data == null)
                    {
                        return Array.Empty<float[]>();
                    }

                    var results = new List<float[]?>(responseBody.Data.Count);
                    foreach (var item in responseBody.Data)
                    {
                        results.Add(item.Embedding);
                    }

                    return results.Where(r => r is not null).Select(r => r!).ToList();
                }
                else if ((int)response.StatusCode == 429) // Rate limit
                {
                    if (retry < maxRetries)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, retry));
                        await Task.Delay(retryAfter, cancellationToken);
                        continue;
                    }
                }

                // 其他错误
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"API request failed with status {response.StatusCode}: {errorContent}");
            }
            catch when (retry < maxRetries)
            {
                // 网络错误重试
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), cancellationToken);
            }
        }

        throw new InvalidOperationException("Failed to get embeddings after all retries");
    }

    public async ValueTask<float[]> EmbedAsync(string query, CancellationToken cancellationToken = default)
    {

        var results = await ProcessBatchAsync([query], cancellationToken);

        return results[0];
    }
    // 响应模型
    public class EmbeddingResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("data")]
        public List<EmbeddingData>? Data { get; init; }

        [JsonPropertyName("usage")]
        public UsageInfo? Usage { get; init; }

        public class EmbeddingData
        {
            [JsonPropertyName("object")]
            public string? Object { get; init; }

            [JsonPropertyName("embedding")]
            public float[]? Embedding { get; init; }

            [JsonPropertyName("index")]
            public int Index { get; init; }
        }

        public class UsageInfo
        {
            [JsonPropertyName("prompt_tokens")]
            public int PromptTokens { get; init; }

            [JsonPropertyName("completion_tokens")]
            public int CompletionTokens { get; init; }

            [JsonPropertyName("total_tokens")]
            public int TotalTokens { get; init; }
        }
    }


}

public static class ExtensionUtils
{
    public static IReadOnlyList<T> Slice<T>(this IReadOnlyList<T> src, int index, int size)
    {
        return src is T[] array
                ? new ArraySegment<T>(array, index, size)
                : src.Skip(index).Take(size).ToList();
    }


}
