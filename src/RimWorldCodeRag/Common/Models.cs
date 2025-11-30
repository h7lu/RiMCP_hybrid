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

public sealed class GraphEdge
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required EdgeKind Kind { get; init; }
}
