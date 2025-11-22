using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Retrieval;

public sealed class RoughSearchResult
{
    public required string SymbolId { get; init; }
    public required string Path { get; init; }
    public required LanguageKind Language { get; init; }
    public required SymbolKind SymbolKind { get; init; }
    public string? Signature { get; init; }
    public required string Preview { get; init; }
    public required string Source { get; init; }
    public double Score { get; init; }
    public string? Namespace { get; init; }
    public string? ContainingType { get; init; }
    public int SpanStart { get; init; }
    public int SpanEnd { get; init; }
}