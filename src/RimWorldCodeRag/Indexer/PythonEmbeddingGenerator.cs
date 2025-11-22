using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

internal sealed class PythonEmbeddingGenerator : IEmbeddingGenerator
{
    private const int DefaultMaxLength = 256;

    private readonly string _pythonExecutable;
    private readonly string _scriptPath;
    private readonly string _modelPath;
    private readonly int _batchSize;
    private readonly int _maxLength;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public PythonEmbeddingGenerator(string pythonExecutable, string scriptPath, string modelPath, int batchSize = 32, int maxLength = DefaultMaxLength)
    {
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("Python embedding script not found", scriptPath);
        if (!Directory.Exists(modelPath)) throw new DirectoryNotFoundException($"Model directory not found: {modelPath}");

        _pythonExecutable = pythonExecutable;
        _scriptPath = scriptPath;
        _modelPath = modelPath;
        _batchSize = Math.Max(1, batchSize);
        _maxLength = maxLength;
    }

    public int PreferredBatchSize => _batchSize;

    public async ValueTask<float[]> GenerateEmbeddingAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        var result = await GenerateEmbeddingsAsync(new[] { chunk }, cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    public async ValueTask<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var request = new PythonEmbeddingRequest
        {
            Mode = "passage",
            Items = chunks.Select(c => new PythonEmbeddingItem
            {
                Id = c.Id,
                Text = string.IsNullOrWhiteSpace(c.Text) ? null : c.Text,
                Preview = string.IsNullOrWhiteSpace(c.Preview) ? null : c.Preview
            }).ToList()
        };

        var requestPath = Path.Combine(Path.GetTempPath(), $"rimrag-emb-{Guid.NewGuid():N}-in.json");
        var responsePath = Path.Combine(Path.GetTempPath(), $"rimrag-emb-{Guid.NewGuid():N}-out.json");

        try
        {
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
            psi.ArgumentList.Add(_maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
                    // Ignore kill failures.
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
            if (response?.Vectors is null)
            {
                throw new InvalidOperationException("Python embedding response missing vectors");
            }

            if (response.Vectors.Count != chunks.Count)
            {
                throw new InvalidOperationException($"Expected {chunks.Count} vectors but received {response.Vectors.Count}");
            }

            return response.Vectors;
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
        public string? Id { get; init; }
        public string? Text { get; init; }
        public string? Preview { get; init; }
    }

    private sealed class PythonEmbeddingResponse
    {
        public List<float[]> Vectors { get; init; } = new();
    }
}
