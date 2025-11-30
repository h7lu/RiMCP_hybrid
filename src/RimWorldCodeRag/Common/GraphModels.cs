using System;

namespace RimWorldCodeRag.Common;

//这里主要定义一些和图检索有关的数据结构，总是用string我不舒服


// 图谱边的类型，表示元素之间的关系
public enum EdgeKind
{

    //继承
    Inherits = 1,
    
    //实现接口
    Implements = 2,

    //方法调用
    Calls = 3,
    
    //类型引用
    References = 4,
    
    //Def父子继承
    XmlInherits = 10,
   
    //Def之间的引用
    XmlReferences = 11,
   
    //Def 绑定到 C# 类
    XmlBindsClass = 20,
    
    //组件使用
    XmlUsesComp = 21,
    
   
    //这算是反向边了，需要一个单独的步骤生成
    //被 Def 使用
    CSharpUsedByDef = 30
}

//图检索
public enum GraphDirection
{
    Uses,
    UsedBy
}

// 图查询配置
public sealed class GraphQueryConfig
{
    //起始节点 ID
    public required string SymbolId { get; init; }
    
    public required GraphDirection Direction { get; init; }
    
    //参数：过滤的节点类型。如果是空，则不过滤
    public string? Kind { get; init; }
    
    public int Page { get; init; } = 1;

    //最大深度（固定为1. 本来想做多跳，后来细想了一下，多跳返回的结果太大，没意义地填充上下文，不如让大模型多次单跳，减少信息的噪声）
    public int MaxDepth { get; init; } = 1;
}


//查询结果
public sealed record GraphQueryResult
{
    //目标节点 ID
    public required string SymbolId { get; init; }
    
    //边类型
    public required EdgeKind EdgeKind { get; init; }
   
    public int Distance { get; init; } = 1;

    public double Score { get; set; }
    public double PageRank { get; set; }
    public int DuplicateCount { get; set; }
}

public sealed class PagedGraphQueryResult
{
    public required IReadOnlyList<GraphQueryResult> Results { get; init; }
    public required int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}
