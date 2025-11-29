using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldCodeRag.Indexer;

public static class PageRankCalculator
{
    private const double DampingFactor = 0.85;
    private const int MaxIterations = 20;
    private const double Epsilon = 1e-6;

    public static Dictionary<int, double> Calculate(
        int nodeCount,
        IReadOnlyList<int> csrRowPointers,
        IReadOnlyList<int> csrColumnIndices,
        IReadOnlyList<int> cscColPointers,
        IReadOnlyList<int> cscRowIndices)
    {
        var pageRank = new double[nodeCount];
        var initialRank = 1.0 / nodeCount;
        for (var i = 0; i < nodeCount; i++)
        {
            pageRank[i] = initialRank;
        }

        var outDegree = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            outDegree[i] = csrRowPointers[i + 1] - csrRowPointers[i];
        }

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            var newPageRank = new double[nodeCount];
            double danglingSum = 0;

            for (var i = 0; i < nodeCount; i++)
            {
                if (outDegree[i] == 0)
                {
                    danglingSum += pageRank[i];
                }
            }

            for (var i = 0; i < nodeCount; i++)
            {
                double rankSum = 0;
                var start = cscColPointers[i];
                var end = cscColPointers[i + 1];

                for (var j = start; j < end; j++)
                {
                    var incomingNode = cscRowIndices[j];
                    if (incomingNode >= 0 && incomingNode < nodeCount && outDegree[incomingNode] > 0)
                    {
                        rankSum += pageRank[incomingNode] / outDegree[incomingNode];
                    }
                }

                newPageRank[i] = (1 - DampingFactor) / nodeCount
                               + DampingFactor * (rankSum + danglingSum / nodeCount);
            }

            double diff = 0;
            for (var i = 0; i < nodeCount; i++)
            {
                diff += Math.Abs(newPageRank[i] - pageRank[i]);
            }

            pageRank = newPageRank;

            if (diff < Epsilon)
            {
                break;
            }
        }

        return pageRank.Select((score, index) => new { score, index })
                       .ToDictionary(item => item.index, item => item.score);
    }
}
