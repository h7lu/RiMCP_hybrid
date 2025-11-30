using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldCodeRag.Common;
using RimWorldCodeRag.Retrieval;

namespace RimWorldCodeRag.Indexer;

internal sealed class IndexingPipeline
{
    private readonly IndexingConfig _config;
    private readonly MetadataStore _metadataStore;

    public IndexingPipeline(IndexingConfig config)
    {
        _config = config;
        _metadataStore = new MetadataStore(Path.Combine(config.MetadataPath, "mtimes.json"));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _metadataStore.EnsureLoaded();
        HandleForceRebuild();

        var chunker = new Chunker(_config, _metadataStore);
        var changedChunks = chunker.BuildChunks();

        var requiresRebuild = changedChunks.Count > 0 || !LuceneIndexExists() || !VectorIndexExists() || !GraphExists();

        if (!requiresRebuild)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[index] No changes detected. Existing artifacts remain current.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("[index] Capturing full snapshot...");
        var fullChunks = chunker.BuildFullSnapshot();

        // Update metadata for all processed files
        foreach (var path in fullChunks.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _metadataStore.SetTimestamp(path, File.GetLastWriteTimeUtc(path));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[index] failed to update metadata for {path}: {ex.Message}");
            }
        }

        if (fullChunks.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"[index] Writing {fullChunks.Count} chunks to Lucene index...");
            using (var lucene = new LuceneWriter(_config.LuceneIndexPath))
            {
                if (_config.ForceRebuildLucene) lucene.Reset();
                lucene.IndexDocuments(fullChunks);
                lucene.Commit();
            }
            Console.ResetColor();
        }

        if (_config.ForceRebuildEmbeddings || !VectorIndexExists())
        {
            IEmbeddingGenerator? embeddingGenerator;

            // Prefer embedding server if configured
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[index] Using remote embedding API at {_config.EmbeddingServerUrl}, model: {_config.ModelName}");
                Console.ResetColor();
                embeddingGenerator = new ApiEmbeddingGenerator(_config.EmbeddingServerUrl, _config.ApiKey, _config.ModelName);
            }
            else if (!string.IsNullOrWhiteSpace(_config.EmbeddingServerUrl))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[index] Using local embedding server at {_config.EmbeddingServerUrl}");
                Console.ResetColor();
                embeddingGenerator = new ServerBatchEmbeddingGenerator(_config.EmbeddingServerUrl, _config.PythonBatchSize);
            }
            else if (!string.IsNullOrWhiteSpace(_config.PythonScriptPath) && File.Exists(_config.PythonScriptPath))
            {
                if (string.IsNullOrWhiteSpace(_config.ModelPath))
                {
                    throw new InvalidOperationException("Model path is required when using the Python embedding bridge.");
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[index] Using local Python bridge with model at {_config.ModelPath}");
                Console.ResetColor();
                embeddingGenerator = new PythonEmbeddingGenerator(_config.PythonExecutablePath!, _config.PythonScriptPath, _config.ModelPath, _config.PythonBatchSize);
            }
            else
            {
                embeddingGenerator = null;
            }

            if (embeddingGenerator != null)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"[index] Generating embeddings for {fullChunks.Count} chunks...");
                await GenerateEmbeddingsAsync(fullChunks, embeddingGenerator, _config.VectorIndexPath, cancellationToken);
                Console.ResetColor();
            }
        }

        var graphBuilder = new GraphBuilder(_config.GraphPath, _config.MaxDegreeOfParallelism);
        graphBuilder.BuildGraph(fullChunks);

        _metadataStore.Save();
    }

    private async Task GenerateEmbeddingsAsync(IReadOnlyList<ChunkRecord> chunks, IEmbeddingGenerator generator, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vectors.jsonl");
        using var writer = new StreamWriter(path);

        var processed = 0;
        var batchSize = generator.PreferredBatchSize;

        for (var i = 0; i < chunks.Count; i += batchSize)
        {
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            if (batch.Count == 0) continue;

            var vectors = await generator.GenerateEmbeddingsAsync(batch, cancellationToken);

            for (var j = 0; j < batch.Count; j++)
            {
                var chunk = batch[j];
                var vector = vectors[j];
                var json = JsonSerializer.Serialize(new
                {
                    id = chunk.Id,
                    path = chunk.Path,
                    signature = chunk.Signature,
                    preview = chunk.Preview,
                    vector = vector
                });
                await writer.WriteLineAsync(json);
                processed++;
            }
            Console.Write($"\r[index] Generated {processed}/{chunks.Count} embeddings...");
        }
        Console.WriteLine();
    }

    private void HandleForceRebuild()
    {
        if (_config.ForceRebuildLucene)
        {
            Console.WriteLine("[index] Forcing Lucene rebuild.");
            if (Directory.Exists(_config.LuceneIndexPath)) Directory.Delete(_config.LuceneIndexPath, true);
        }
        if (_config.ForceRebuildEmbeddings)
        {
            Console.WriteLine("[index] Forcing embeddings rebuild.");
            if (Directory.Exists(_config.VectorIndexPath)) Directory.Delete(_config.VectorIndexPath, true);
        }
        if (_config.ForceRebuildGraph)
        {
            Console.WriteLine("[index] Forcing graph rebuild.");
            var graphDir = Path.GetDirectoryName(_config.GraphPath);
            if (!string.IsNullOrEmpty(graphDir) && Directory.Exists(graphDir))
            {
                foreach (var file in Directory.GetFiles(graphDir, "graph.*"))
                {
                    File.Delete(file);
                }
            }
        }
    }

    private bool LuceneIndexExists() => Directory.Exists(_config.LuceneIndexPath) && Directory.EnumerateFiles(_config.LuceneIndexPath).Any();
    private bool VectorIndexExists() => Directory.Exists(_config.VectorIndexPath) && File.Exists(Path.Combine(_config.VectorIndexPath, "vectors.jsonl"));
    private bool GraphExists() => File.Exists(_config.GraphPath + ".nodes.tsv");
}
