using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Retrieval;

internal interface IQueryEmbeddingGenerator
{
    ValueTask<float[]> EmbedAsync(string query, CancellationToken cancellationToken = default);
}

internal sealed class HashQueryEmbeddingGenerator : IQueryEmbeddingGenerator
{
    private readonly int _dimensions;

    public HashQueryEmbeddingGenerator(int dimensions)
    {
        _dimensions = Math.Max(1, dimensions);
    }

    public ValueTask<float[]> EmbedAsync(string query, CancellationToken cancellationToken = default)
    {
        if (_dimensions <= 0)
        {
            return ValueTask.FromResult(Array.Empty<float>());
        }

        var vector = new float[_dimensions];
        foreach (var token in EnumerateTokens(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucket = (int)(Hash(token) % (uint)_dimensions);
            vector[bucket] += 1f;
        }

        Normalize(vector);
        return ValueTask.FromResult(vector);
    }

    private static IEnumerable<string> EnumerateTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        foreach (var token in TextUtilities.SplitIdentifier(query).Concat(query.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            var lowered = token.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(lowered))
            {
                yield return lowered;
            }
        }
    }

    private static uint Hash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash;
        }
    }

    private static void Normalize(float[] vector)
    {
        double sumSquares = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        if (sumSquares <= double.Epsilon)
        {
            return;
        }

        var norm = (float)(1.0 / Math.Sqrt(sumSquares));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= norm;
        }
    }
}

internal sealed class PythonQueryEmbeddingGenerator : IQueryEmbeddingGenerator
{
    private const int DefaultMaxLength = 256;

    private readonly string _pythonExecutable;
    private readonly string _scriptPath;
    private readonly string _modelPath;
    private readonly int _maxLength;
    private readonly EmbeddingCache _cache;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public PythonQueryEmbeddingGenerator(string pythonExecutable, string scriptPath, string modelPath, int maxLength = DefaultMaxLength, int cacheSize = 100)
    {
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("Python embedding script not found", scriptPath);
        if (!Directory.Exists(modelPath)) throw new DirectoryNotFoundException($"Model directory not found: {modelPath}");

        _pythonExecutable = pythonExecutable;
        _scriptPath = scriptPath;
        _modelPath = modelPath;
        _maxLength = maxLength;
        _cache = new EmbeddingCache(cacheSize);
    }

    public async ValueTask<float[]> EmbedAsync(string query, CancellationToken cancellationToken = default)
    {
        // Check cache first
        if (_cache.TryGet(query, out var cachedEmbedding))
        {
            return cachedEmbedding;
        }

        var requestPath = Path.Combine(Path.GetTempPath(), $"rimrag-qry-{Guid.NewGuid():N}-in.json");
        var responsePath = Path.Combine(Path.GetTempPath(), $"rimrag-qry-{Guid.NewGuid():N}-out.json");

        try
        {
            var request = new PythonEmbeddingRequest
            {
                Mode = "query",
                Items = new List<PythonEmbeddingItem>
                {
                    new()
                    {
                        Text = query,
                        Preview = query
                    }
                }
            };

            await using (var fs = File.Create(requestPath))
            {
                await JsonSerializer.SerializeAsync(fs, request, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }

            var psi = new ProcessStartInfo
            {
                FileName = _pythonExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(_scriptPath);
            psi.ArgumentList.Add("encode");
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_modelPath);
            psi.ArgumentList.Add("--input");
            psi.ArgumentList.Add(requestPath);
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(responsePath);
            psi.ArgumentList.Add("--max-length");
            psi.ArgumentList.Add(_maxLength.ToString(CultureInfo.InvariantCulture));

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start python process");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var registration = cancellationToken.Register(() =>
            {
#if NET6_0_OR_GREATER
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore process kill failures.
                }
#else
                try
                {
                    process.Kill();
                }
                catch
                {
                }
#endif
            });

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Python embedding process failed (exit {process.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
            }

            await using var responseStream = File.OpenRead(responsePath);
            var response = await JsonSerializer.DeserializeAsync<PythonEmbeddingResponse>(responseStream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            if (response?.Vectors is null || response.Vectors.Count == 0)
            {
                throw new InvalidOperationException("Python embedding response missing vectors");
            }

            var embedding = response.Vectors[0];
            
            // Cache the embedding for future queries
            _cache.Add(query, embedding);
            
            return embedding;
        }
        finally
        {
            SafeDelete(requestPath);
            SafeDelete(responsePath);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private sealed class PythonEmbeddingRequest
    {
        public string Mode { get; init; } = "passage";
        public required List<PythonEmbeddingItem> Items { get; init; }
    }

    private sealed class PythonEmbeddingItem
    {
        public string? Text { get; init; }
        public string? Preview { get; init; }
    }

    private sealed class PythonEmbeddingResponse
    {
        public List<float[]> Vectors { get; init; } = new();
    }
}

// 当没有可用的嵌入路径时使用此生成器作为回退：不生成向量，转为词法检索。
internal sealed class NoEmbeddingQueryEmbeddingGenerator : IQueryEmbeddingGenerator
{
    private static bool _warned = false;

    public ValueTask<float[]> EmbedAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!_warned)
        {
            try
            {
                Console.Error.WriteLine("[warning] Embedding not configured: falling back to lexical-only search.");
            }
            catch
            {
                // 写 stderr 失败时静默忽略
            }

            _warned = true;
        }

        // 返回空向量，调用方会据此跳过语义匹配，从而仅使用词法检索结果
        return ValueTask.FromResult(Array.Empty<float>());
    }
}