namespace RimWorldCodeRag.Indexer;

public sealed class IndexingConfig
{
    public required string SourceRoot { get; init; }
    public required string LuceneIndexPath { get; init; }
    public required string VectorIndexPath { get; init; }
    public required string GraphPath { get; init; }
    public required string MetadataPath { get; init; }
    public required string ModelPath { get; init; }
    public string? ApiKey { get; init; }
    public string? ModelName { get; init; }
    public string? EmbeddingServerUrl { get; init; }
    public string? PythonExecutablePath { get; init; }
    public string? PythonScriptPath { get; init; }
    public int PythonBatchSize { get; init; }
    public int MaxDegreeOfParallelism { get; init; }
    public bool Incremental { get; init; }
    
    // Granular force rebuild flags
    public bool ForceRebuildLucene { get; set; }
    public bool ForceRebuildEmbeddings { get; set; }
    public bool ForceRebuildGraph { get; set; }

    public bool ForceFullRebuild => ForceRebuildLucene && ForceRebuildEmbeddings && ForceRebuildGraph;
}
