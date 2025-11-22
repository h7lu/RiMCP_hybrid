using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Indexer;

namespace RimWorldCodeRag.Retrieval;

//两种embedding策略，后台程序服务器和本地子进程。这两种对indexing管线没有影响，但是子进程的方法会导致每个检索都需要重新加载embedding，浪费半分钟时间，所以生产时尽量拉本地服务器
internal sealed class AdaptiveEmbeddingGenerator : IQueryEmbeddingGenerator, IDisposable
{
    private readonly EmbeddingServerClient? _serverClient;
    private readonly PythonQueryEmbeddingGenerator? _subprocessGenerator;
    private bool _serverAvailable;

    public AdaptiveEmbeddingGenerator(
        string? serverUrl = null,
        string? pythonExecutable = null,
        string? scriptPath = null,
        string? modelPath = null,
        int maxLength = 256,
        int cacheSize = 100)
    {
        if (!string.IsNullOrEmpty(serverUrl))
        {
            _serverClient = new EmbeddingServerClient(serverUrl, cacheSize);
            _serverAvailable = true;
        }

        if (!string.IsNullOrEmpty(pythonExecutable) && 
            !string.IsNullOrEmpty(scriptPath) && 
            !string.IsNullOrEmpty(modelPath))
        {
            _subprocessGenerator = new PythonQueryEmbeddingGenerator(
                pythonExecutable, 
                scriptPath, 
                modelPath, 
                maxLength, 
                cacheSize);
        }
    }

    public async ValueTask<float[]> EmbedAsync(string query, CancellationToken cancellationToken = default)
    {
        if (_serverAvailable && _serverClient != null)
        {
            try
            {
                return await _serverClient.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[embedding] Server unavailable, falling back to subprocess: {ex.Message}");
                _serverAvailable = false;
            }
        }

        if (_subprocessGenerator != null)
        {
            return await _subprocessGenerator.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("No embedding generator available (server unreachable and no subprocess configured)");
    }

    public void Dispose()
    {
        _serverClient?.Dispose();
    }
}

//服务器
internal sealed class ServerBatchEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly EmbeddingServerClient _client;
    private readonly int _batchSize;

    public ServerBatchEmbeddingGenerator(string serverUrl, int batchSize = 128)
    {
        _client = new EmbeddingServerClient(serverUrl, cacheSize: 0); // No cache for batch operations
        _batchSize = Math.Max(1, batchSize);
    }

    public int PreferredBatchSize => _batchSize;

    public async ValueTask<float[]> GenerateEmbeddingAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        var item = new ServerEmbeddingItem
        {
            Text = string.IsNullOrWhiteSpace(chunk.Text) ? null : chunk.Text,
            Preview = string.IsNullOrWhiteSpace(chunk.Preview) ? null : chunk.Preview
        };

        var vectors = await _client.EmbedBatchAsync(new[] { item }, "passage", cancellationToken).ConfigureAwait(false);
        return vectors.Count > 0 ? vectors[0] : Array.Empty<float>();
    }

    public async ValueTask<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var items = chunks.Select(c => new ServerEmbeddingItem
        {
            Text = string.IsNullOrWhiteSpace(c.Text) ? null : c.Text,
            Preview = string.IsNullOrWhiteSpace(c.Preview) ? null : c.Preview
        }).ToList();

        return await _client.EmbedBatchAsync(items, "passage", cancellationToken).ConfigureAwait(false);
    }
}
