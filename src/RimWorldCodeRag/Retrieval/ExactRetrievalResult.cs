namespace RimWorldCodeRag.Retrieval;

using RimWorldCodeRag.Common;

//get_item工具的结果，通过标题返回整段代码
public sealed class ExactRetrievalResult
{

    public required string SymbolId { get; init; }

    public required string Path { get; init; }

    public required LanguageKind Language { get; init; }//csharp or xml

    public required SymbolKind SymbolKind { get; init; }//def, class, method, property, field

    public string? Namespace { get; init; }

    public string? ContainingType { get; init; }

    public string? Signature { get; init; }

    public string? DefType { get; init; }

    public required string SourceCode { get; init; }

    public required string FullCode { get; init; }

    public bool Truncated { get; init; }

    public int TotalLines { get; init; }

    public int DisplayedLines { get; init; }
}
