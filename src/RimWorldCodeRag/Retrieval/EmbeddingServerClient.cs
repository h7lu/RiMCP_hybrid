using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldCodeRag.Retrieval;


//持续的Python嵌入服务器的HTTP客户端。可以避免冷启动
internal sealed class EmbeddingServerClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly EmbeddingCache _cache;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public EmbeddingServerClient(string baseUrl = "http://127.0.0.1:5000", int cacheSize = 100)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120) 
        };
        _cache = new EmbeddingCache(cacheSize);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        //先看一眼缓存有没有
        if (_cache.TryGet(query, out var cachedEmbedding))
        {
            return cachedEmbedding;
        }

        var request = new ServerEmbeddingRequest
        {
            Mode = "query",
            Items = new List<ServerEmbeddingItem>
            {
                new() { Text = query, Preview = query }
            }
        };

        var vectors = await PostEmbedRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (vectors.Count == 0)
        {
            throw new InvalidOperationException("Embedding server returned no vectors");
        }

        var embedding = vectors[0];
        _cache.Add(query, embedding);
        return embedding;
    }

    public async Task<List<float[]>> EmbedBatchAsync(IEnumerable<ServerEmbeddingItem> items, string mode = "passage", CancellationToken cancellationToken = default)
    {
        var request = new ServerEmbeddingRequest
        {
            Mode = mode,
            Items = items.ToList()
        };

        return await PostEmbedRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<float[]>> PostEmbedRequestAsync(ServerEmbeddingRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{_baseUrl}/embed", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<ServerEmbeddingResponse>(responseStream, _jsonOptions, cancellationToken).ConfigureAwait(false);

        if (result?.Vectors is null)
        {
            throw new InvalidOperationException("Embedding server returned null vectors");
        }

        return result.Vectors;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

internal sealed class ServerEmbeddingRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "passage";

    [JsonPropertyName("items")]
    public List<ServerEmbeddingItem> Items { get; set; } = new();
}

internal sealed class ServerEmbeddingItem
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("preview")]
    public string? Preview { get; set; }
}

internal sealed class ServerEmbeddingResponse
{
    [JsonPropertyName("vectors")]
    public List<float[]> Vectors { get; set; } = new();
}
