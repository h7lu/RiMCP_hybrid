using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

/// <summary>
/// Walks a C# syntax tree using the Roslyn SemanticModel to extract accurate dependency edges.
/// Unlike syntactic analysis, this walker can:
/// - Correctly resolve method overloads
/// - Accurately identify static member references
/// - Understand inheritance and interface implementations
/// - Respect using directives and type aliases
/// </summary>
public sealed class CSharpSemanticWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly string _sourceChunkId;
    private readonly HashSet<string> _validTargetIds;
    private readonly HashSet<string> _emittedEdges = new(StringComparer.Ordinal);

    /// <summary>
    /// The collected graph edges.
    /// </summary>
    public List<GraphEdge> Edges { get; } = new();

    /// <summary>
    /// Creates a new semantic walker.
    /// </summary>
    /// <param name="semanticModel">The Roslyn semantic model for the syntax tree.</param>
    /// <param name="sourceChunkId">The ID of the chunk being analyzed (source of edges).</param>
    /// <param name="validTargetIds">Set of valid target IDs to filter against. If null, all targets are accepted.</param>
    public CSharpSemanticWalker(SemanticModel semanticModel, string sourceChunkId, HashSet<string>? validTargetIds = null)
    {
        _semanticModel = semanticModel;
        _sourceChunkId = sourceChunkId;
        _validTargetIds = validTargetIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Visits base type declarations to extract inheritance edges.
    /// </summary>
    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        ExtractBaseTypeEdges(node.BaseList);
        base.VisitClassDeclaration(node);
    }

    /// <summary>
    /// Visits struct declarations to extract interface implementation edges.
    /// </summary>
    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        ExtractBaseTypeEdges(node.BaseList);
        base.VisitStructDeclaration(node);
    }

    /// <summary>
    /// Visits method invocations to extract method call edges.
    /// This is where we get the precise overload resolution.
    /// </summary>
    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        AddEdge(symbolInfo.Symbol, EdgeKind.Calls);

        // Also check candidate symbols if the exact symbol couldn't be determined
        if (symbolInfo.Symbol == null && symbolInfo.CandidateSymbols.Length > 0)
        {
            // Pick the best candidate (usually the first one with best match)
            AddEdge(symbolInfo.CandidateSymbols[0], EdgeKind.Calls);
        }

        base.VisitInvocationExpression(node);
    }

    /// <summary>
    /// Visits member access expressions (e.g., obj.Property, Type.StaticMember).
    /// Skips expressions that are part of invocations (handled separately).
    /// </summary>
    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // Avoid double-counting method calls, which are handled by VisitInvocationExpression
        if (node.Parent is InvocationExpressionSyntax)
        {
            base.VisitMemberAccessExpression(node);
            return;
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);
        
        if (symbol != null)
        {
            var kind = symbol.Kind switch
            {
                Microsoft.CodeAnalysis.SymbolKind.Method => EdgeKind.Calls,
                Microsoft.CodeAnalysis.SymbolKind.Property => EdgeKind.References,
                Microsoft.CodeAnalysis.SymbolKind.Field => EdgeKind.References,
                Microsoft.CodeAnalysis.SymbolKind.Event => EdgeKind.References,
                _ => EdgeKind.References
            };
            AddEdge(symbol, kind);
        }

        base.VisitMemberAccessExpression(node);
    }

    /// <summary>
    /// Visits object creation expressions (new Type()).
    /// </summary>
    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);
        
        if (symbol is IMethodSymbol constructorSymbol)
        {
            // Reference the containing type, not the constructor itself
            AddEdge(constructorSymbol.ContainingType, EdgeKind.References);
        }

        base.VisitObjectCreationExpression(node);
    }

    /// <summary>
    /// Visits implicit object creation expressions (new()).
    /// </summary>
    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);
        
        if (symbol is IMethodSymbol constructorSymbol)
        {
            AddEdge(constructorSymbol.ContainingType, EdgeKind.References);
        }

        base.VisitImplicitObjectCreationExpression(node);
    }

    /// <summary>
    /// Visits identifier names to catch simple references.
    /// </summary>
    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        // Skip if this is part of a larger expression that's already handled
        if (node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == node)
        {
            base.VisitIdentifierName(node);
            return;
        }

        // Skip if this is an invocation expression target
        if (node.Parent is InvocationExpressionSyntax)
        {
            base.VisitIdentifierName(node);
            return;
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);

        if (symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol)
        {
            AddEdge(symbol, EdgeKind.References);
        }

        base.VisitIdentifierName(node);
    }

    /// <summary>
    /// Visits generic name syntax (e.g., List&lt;T&gt;).
    /// </summary>
    public override void VisitGenericName(GenericNameSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);

        if (symbol is INamedTypeSymbol)
        {
            AddEdge(symbol, EdgeKind.References);
        }

        base.VisitGenericName(node);
    }

    /// <summary>
    /// Visits type syntax in declarations, casts, etc.
    /// </summary>
    public override void VisitQualifiedName(QualifiedNameSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);

        if (symbol is INamedTypeSymbol)
        {
            AddEdge(symbol, EdgeKind.References);
        }

        base.VisitQualifiedName(node);
    }

    private void ExtractBaseTypeEdges(BaseListSyntax? baseList)
    {
        if (baseList == null)
        {
            return;
        }

        foreach (var baseType in baseList.Types)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(baseType.Type);
            var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);

            if (symbol is INamedTypeSymbol namedType)
            {
                var edgeKind = namedType.TypeKind == TypeKind.Interface 
                    ? EdgeKind.Implements 
                    : EdgeKind.Inherits;
                
                AddEdge(symbol, edgeKind);
            }
        }
    }

    private void AddEdge(ISymbol? symbol, EdgeKind kind)
    {
        if (symbol == null)
        {
            return;
        }

        var targetId = SymbolFormatter.GetUniqueId(symbol);
        if (string.IsNullOrEmpty(targetId))
        {
            return;
        }

        // Don't create self-referential edges
        if (string.Equals(targetId, _sourceChunkId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // If we have a valid target ID set, check against it
        // If the set is empty, we accept all targets (will be filtered later)
        if (_validTargetIds.Count > 0 && !_validTargetIds.Contains(targetId))
        {
            // Try with simple ID (without method signature) for method lookups
            var simpleId = SymbolFormatter.GetSimpleId(symbol);
            if (simpleId != null && !_validTargetIds.Contains(simpleId))
            {
                return;
            }
            targetId = simpleId ?? targetId;
        }

        // Deduplicate edges
        var edgeKey = $"{_sourceChunkId}|{targetId}|{kind}";
        if (!_emittedEdges.Add(edgeKey))
        {
            return;
        }

        Edges.Add(new GraphEdge
        {
            SourceId = _sourceChunkId,
            TargetId = targetId,
            Kind = kind
        });
    }
}
