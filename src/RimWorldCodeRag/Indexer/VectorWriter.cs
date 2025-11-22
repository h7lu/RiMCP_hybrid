using System.Buffers;
using System.Linq;
using System.Text.Json;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

internal interface IEmbeddingGenerator
{
    int PreferredBatchSize => 1;

    ValueTask<float[]> GenerateEmbeddingAsync(ChunkRecord chunk, CancellationToken cancellationToken = default);

    async ValueTask<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>(chunks.Count);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await GenerateEmbeddingAsync(chunk, cancellationToken).ConfigureAwait(false);
            results.Add(embedding);
        }

        return results;
    }
}

internal sealed class HashEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly int _dimensions;

    public HashEmbeddingGenerator(int dimensions = 768) // e5-base-v2 default
    {
        _dimensions = dimensions;
    }

    public ValueTask<float[]> GenerateEmbeddingAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        var vector = ArrayPool<float>.Shared.Rent(_dimensions);
        Array.Clear(vector, 0, _dimensions);

        foreach (var token in chunk.KeywordIdentifiers.Concat(chunk.Identifiers))
        {
            var hash = Hash(token);
            var bucket = (int)(hash % (uint)_dimensions);
            vector[bucket] += 1f;
        }

        Normalize(vector, _dimensions);
        var managed = new float[_dimensions];
        Array.Copy(vector, managed, _dimensions);
        ArrayPool<float>.Shared.Return(vector);
        return ValueTask.FromResult(managed);
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

    private static void Normalize(float[] vector, int length)
    {
        double sumSquares = 0;
        for (int i = 0; i < length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        if (sumSquares <= double.Epsilon)
        {
            return;
        }

        var norm = (float)(1.0 / Math.Sqrt(sumSquares));
        for (int i = 0; i < length; i++)
        {
            vector[i] *= norm;
        }
    }
}

internal sealed class VectorWriter
{
    private readonly string _outputDirectory;
    private readonly IEmbeddingGenerator _embeddingGenerator;

    public VectorWriter(string outputDirectory, IEmbeddingGenerator embeddingGenerator)
    {
        _outputDirectory = outputDirectory;
        _embeddingGenerator = embeddingGenerator;
    }

    public async Task WriteAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);
        var vecPath = Path.Combine(_outputDirectory, "vectors.jsonl");
        var mapPath = Path.Combine(_outputDirectory, "vector-map.json");

        var index = new List<Dictionary<string, object?>>();
        var jsonOptions = new JsonSerializerOptions();
        await using var stream = new FileStream(vecPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);

        var batchSize = Math.Max(1, _embeddingGenerator.PreferredBatchSize);
        var batch = new List<ChunkRecord>(batchSize);
        var total = chunks.Count;
        var processed = 0;

        async Task ProcessBatchAsync(List<ChunkRecord> batchChunks)
        {
            if (batchChunks.Count == 0)
            {
                return;
            }

            var embeddings = await _embeddingGenerator.GenerateEmbeddingsAsync(batchChunks, cancellationToken).ConfigureAwait(false);
            if (embeddings.Count != batchChunks.Count)
            {
                throw new InvalidOperationException($"Embedding generator returned {embeddings.Count} vectors for {batchChunks.Count} chunks.");
            }

            for (int i = 0; i < batchChunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = batchChunks[i];
                var embedding = embeddings[i];
                var payload = new
                {
                    id = chunk.Id,
                    path = chunk.Path,
                    signature = chunk.Signature,
                    identifiers = chunk.KeywordIdentifiers,
                    preview = chunk.Preview,
                    vector = embedding
                };

                var offset = writer.BaseStream.Position;
                var json = JsonSerializer.Serialize(payload, jsonOptions);
                await writer.WriteLineAsync(json).ConfigureAwait(false);

                index.Add(new Dictionary<string, object?>
                {
                    ["id"] = chunk.Id,
                    ["path"] = chunk.Path,
                    ["signature"] = chunk.Signature,
                    ["offset"] = offset
                });
            }
        }

        void RenderProgress()
        {
            if (total == 0)
            {
                return;
            }

            var percent = (double)processed / total;
            var display = Math.Min(100, Math.Max(0, (int)Math.Round(percent * 100)));
            Console.Write($"\r[index] Embedding chunks {processed}/{total} ({display}%)");
        }

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Add(chunks[i]);
            if (batch.Count >= batchSize)
            {
                await ProcessBatchAsync(batch).ConfigureAwait(false);
                processed += batch.Count;
                RenderProgress();
                batch.Clear();
            }
        }

        await ProcessBatchAsync(batch).ConfigureAwait(false);
        processed += batch.Count;
        RenderProgress();
        batch.Clear();
        if (total > 0)
        {
            Console.WriteLine();
        }

        await writer.FlushAsync();
        var indexJson = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(mapPath, indexJson, cancellationToken);
    }
}
