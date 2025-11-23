using System;
using System.IO;
using System.Linq;
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

        var chunker = new Chunker(_config, _metadataStore);
        IReadOnlyList<ChunkRecord> changedChunks;
        if (_config.ForceFullRebuild)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[index] Force rebuild requested; ignoring incremental change detection.");
            Console.ResetColor();
            changedChunks = Array.Empty<ChunkRecord>();
        }
        else
        {
            changedChunks = chunker.BuildChunks();
        }

        var requiresRebuild = _config.ForceFullRebuild || changedChunks.Count > 0 || !LuceneIndexExists() || !VectorIndexExists() || !GraphExists();

        if (!requiresRebuild)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[index] No changes detected. Existing artifacts remain current.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("[index] Capturing full snapshot...");
        var fullChunks = chunker.BuildFullSnapshot();

        if (_config.ForceFullRebuild)
        {
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
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"[index] Writing {fullChunks.Count} chunks to Lucene index...");
        using (var lucene = new LuceneWriter(_config.LuceneIndexPath))
        {
            lucene.Reset();
            lucene.IndexDocuments(fullChunks);
            lucene.Commit();
        }
        Console.ResetColor();

        IEmbeddingGenerator embeddingGenerator;

        // Prefer embedding server if configured

        if (!string.IsNullOrWhiteSpace(_config.EmbeddingServerUrl)||!string.IsNullOrWhiteSpace(_config.ApiKey)||!string.IsNullOrWhiteSpace(_config.ModelName))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[index] Using embedding server api at {_config.EmbeddingServerUrl}, model: {_config.ModelName}");
            Console.ResetColor();
            embeddingGenerator = new ApiEmbeddingGenerator(_config.EmbeddingServerUrl,_config.ApiKey, _config.ModelName);
            
        }
        else if (!string.IsNullOrWhiteSpace(_config.EmbeddingServerUrl))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[index] Using embedding server at {_config.EmbeddingServerUrl}");
            Console.ResetColor();
            embeddingGenerator = new ServerBatchEmbeddingGenerator(_config.EmbeddingServerUrl, _config.PythonBatchSize);
        }
        else if (!string.IsNullOrWhiteSpace(_config.PythonScriptPath) && File.Exists(_config.PythonScriptPath))
        {
            if (string.IsNullOrWhiteSpace(_config.ModelPath))
            {
                throw new InvalidOperationException("Model path is required when using the Python embedding bridge.");
            }

            var pythonExec = string.IsNullOrWhiteSpace(_config.PythonExecutablePath) ? "python" : _config.PythonExecutablePath;
            if (!Directory.Exists(_config.ModelPath))
            {
                throw new DirectoryNotFoundException($"Model directory not found: {_config.ModelPath}");
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[index] Using Python embedding subprocess via {_config.PythonScriptPath}");
            Console.ResetColor();
            embeddingGenerator = new PythonEmbeddingGenerator(pythonExec, _config.PythonScriptPath, _config.ModelPath, _config.PythonBatchSize);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_config.ModelPath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[index] Python bridge not configured; ignoring model path and using hash embeddings.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[index] No model path configured, using hash embeddings.");
                Console.ResetColor();
            }
            embeddingGenerator = new HashEmbeddingGenerator();
        }

        var vectorWriter = new VectorWriter(_config.VectorIndexPath, embeddingGenerator);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("[index] Rebuilding vector store...");
        Console.ResetColor();
        await vectorWriter.WriteAsync(fullChunks, cancellationToken);

        Console.WriteLine("[index] Building graph snapshot...");
        var graphBuilder = new GraphBuilder(_config.GraphPath, _config.MaxDegreeOfParallelism);
        graphBuilder.BuildGraph(fullChunks);

        _metadataStore.Save();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[index] Completed.");
        Console.ResetColor();
    }

    private bool LuceneIndexExists()
    {
        var path = _config.LuceneIndexPath;
        return Directory.Exists(path) && Directory.EnumerateFiles(path).Any();
    }

    private bool VectorIndexExists()
    {
        var path = Path.Combine(_config.VectorIndexPath, "vectors.jsonl");
        return File.Exists(path);
    }

    private bool GraphExists()
    {
        var basePath = GraphBuilder.NormalizeBasePath(_config.GraphPath);
        var csr = basePath + ".csr.bin";
        var csc = basePath + ".csc.bin";
        var nodes = basePath + ".nodes.tsv";
        return File.Exists(csr) && File.Exists(csc) && File.Exists(nodes);
    }
}
