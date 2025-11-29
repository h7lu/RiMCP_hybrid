using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using RimWorldCodeRag.Common;

namespace RimWorldCodeRag.Indexer;

public static class SparseGraphWriter
{
    private const string CsrMagic = "CSR1";
    private const string CscMagic = "CSC1";
    private const int FormatVersion = 1;

    public static (Dictionary<string, int> NodeToIndex, Dictionary<int, string> IndexToNode) Write(string basePath, IReadOnlyList<ChunkRecord> nodes, IReadOnlyCollection<GraphEdge> edges)
    {
        if (nodes.Count == 0)
        {
            DeleteIfExists(basePath + ".csr.bin");
            DeleteIfExists(basePath + ".csc.bin");
            DeleteIfExists(basePath + ".nodes.tsv");
            DeleteIfExists(basePath + ".meta.json");
            return (new Dictionary<string, int>(), new Dictionary<int, string>());
        }

        var (nodeToIndex, indexToNode) = BuildNodeIndex(nodes);
        var nodeCount = nodes.Count;

        var rowCounts = new int[nodeCount];
        var colCounts = new int[nodeCount];
        var validEdgeCount = CountEdges(edges, nodeToIndex, rowCounts, colCounts);

        var csrRowPointers = BuildPointers(rowCounts);
        var cscColPointers = BuildPointers(colCounts);

        var csrColumnIndices = new int[validEdgeCount];
        var cscRowIndices = new int[validEdgeCount];
        var csrKinds = new byte[validEdgeCount];
        var cscKinds = new byte[validEdgeCount];

        PopulateMatrices(edges, nodeToIndex, csrRowPointers, cscColPointers, csrColumnIndices, cscRowIndices, csrKinds, cscKinds);

        WriteBinary(basePath + ".csr.bin", CsrMagic, nodeCount, validEdgeCount, csrRowPointers, csrColumnIndices, csrKinds);
        WriteBinary(basePath + ".csc.bin", CscMagic, nodeCount, validEdgeCount, cscColPointers, cscRowIndices, cscKinds);
        WriteNodes(basePath + ".nodes.tsv", nodes);
        WriteMetadata(basePath + ".meta.json", nodeCount, validEdgeCount);

        return (nodeToIndex, indexToNode);
    }

    private static (Dictionary<string, int>, Dictionary<int, string>) BuildNodeIndex(IReadOnlyList<ChunkRecord> nodes)
    {
        var nodeToIndex = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        var indexToNode = new Dictionary<int, string>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var id = nodes[i].Id;
            nodeToIndex[id] = i;
            indexToNode[i] = id;
        }

        return (nodeToIndex, indexToNode);
    }

    private static int CountEdges(
        IReadOnlyCollection<GraphEdge> edges,
        Dictionary<string, int> nodeIndex,
        int[] rowCounts,
        int[] colCounts)
    {
        var count = 0;
        foreach (var edge in edges)
        {
            if (!nodeIndex.TryGetValue(edge.SourceId, out var src))
            {
                continue;
            }

            if (!nodeIndex.TryGetValue(edge.TargetId, out var dst))
            {
                continue;
            }

            rowCounts[src]++;
            colCounts[dst]++;
            count++;
        }

        return count;
    }

    private static int[] BuildPointers(int[] counts)
    {
        var pointers = new int[counts.Length + 1];
        for (var i = 0; i < counts.Length; i++)
        {
            pointers[i + 1] = pointers[i] + counts[i];
        }

        return pointers;
    }

    private static void PopulateMatrices(
        IReadOnlyCollection<GraphEdge> edges,
        Dictionary<string, int> nodeIndex,
        int[] csrRowPointers,
        int[] cscColPointers,
        int[] csrColumnIndices,
        int[] cscRowIndices,
        byte[] csrKinds,
        byte[] cscKinds)
    {
        var rowPositions = new int[csrRowPointers.Length - 1];
        var colPositions = new int[cscColPointers.Length - 1];
        Array.Copy(csrRowPointers, rowPositions, rowPositions.Length);
        Array.Copy(cscColPointers, colPositions, colPositions.Length);

        foreach (var edge in edges)
        {
            if (!nodeIndex.TryGetValue(edge.SourceId, out var src))
            {
                continue;
            }

            if (!nodeIndex.TryGetValue(edge.TargetId, out var dst))
            {
                continue;
            }

            var kind = EncodeKind(edge.Kind);

            var rowPos = rowPositions[src]++;
            csrColumnIndices[rowPos] = dst;
            csrKinds[rowPos] = kind;

            var colPos = colPositions[dst]++;
            cscRowIndices[colPos] = src;
            cscKinds[colPos] = kind;
        }
    }

    private static byte EncodeKind(EdgeKind kind)
    {
        return kind switch
        {
            EdgeKind.Calls => 1,
            EdgeKind.References => 2,
            EdgeKind.Inherits => 3,
            EdgeKind.XmlReferences => 4,
            EdgeKind.XmlInherits => 10,
            EdgeKind.XmlBindsClass => 20,
            EdgeKind.XmlUsesComp => 21,
            EdgeKind.CSharpUsedByDef => 30,
            _ => 0
        };
    }

    private static void WriteBinary(string path, string magic, int nodeCount, int edgeCount, int[] pointers, int[] indices, byte[] kinds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes(magic));
        writer.Write(FormatVersion);
        writer.Write(nodeCount);
        writer.Write(edgeCount);

        foreach (var pointer in pointers)
        {
            writer.Write(pointer);
        }

        foreach (var index in indices)
        {
            writer.Write(index);
        }

        writer.Write(kinds.Length);
        writer.Write(kinds);
    }

    private static void WriteNodes(string path, IReadOnlyList<ChunkRecord> nodes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        for (var i = 0; i < nodes.Count; i++)
        {
            writer.Write(i.ToString(CultureInfo.InvariantCulture));
            writer.Write('\t');
            writer.WriteLine(nodes[i].Id);
        }
    }

    private static void WriteMetadata(string path, int nodeCount, int edgeCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.Append("  \"nodes\": ");
        builder.Append(nodeCount.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(",");
        builder.Append("  \"edges\": ");
        builder.Append(edgeCount.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("}");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
