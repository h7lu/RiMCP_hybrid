namespace RimWorldCodeRag.Common;

public enum LanguageKind
{
    CSharp,
    Xml
}

public enum SymbolKind
{
    Unknown,
    Namespace,
    Type,
    Method,
    Property,
    Field,
    Constructor,
    XmlDef
}

public sealed class ChunkRecord
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required LanguageKind Language { get; init; }
    public required string Text { get; init; }
    public required string Preview { get; init; }
    public required string[] Identifiers { get; init; }
    public required string[] KeywordIdentifiers { get; init; }
    public required SymbolKind SymbolKind { get; init; }
    public required string SymbolName { get; init; }
    public required string Namespace { get; init; }
    public required string ContainingType { get; init; }
    public required int SpanStart { get; init; }
    public required int SpanEnd { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public string? Signature { get; init; }
    public string[] XmlLinks { get; init; } = Array.Empty<string>();
    
    // For XML Defs: the DefType (e.g., "ThingDef", "RecipeDef").
    // For C# code: null.
    public string? DefType { get; init; }
}

public sealed class IndexingConfig
{
    public required string SourceRoot { get; init; }
    public required string LuceneIndexPath { get; init; }
    public required string VectorIndexPath { get; init; }
    public required string GraphPath { get; init; }
    public required string MetadataPath { get; init; }
    public string? ModelPath { get; init; }
    
    // Embedding server config
    public string? EmbeddingServerUrl { get; set; }
    
    public string? ApiKey { get; set; }
    public string? ModelName { get; set; }

    // Subprocess fallback config
    public string? PythonExecutablePath { get; set; }
    public string? PythonScriptPath { get; set; }
    public int PythonBatchSize { get; init; } = 1024;
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public bool Incremental { get; init; } = true;
    public bool ForceFullRebuild { get; init; }
}

public sealed class GraphEdge
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required EdgeKind Kind { get; init; }
}
