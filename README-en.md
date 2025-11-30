# RiMCP_hybrid

- Author: Acutus (h7lu)

* This project provides a set of retrieval and navigation tools for RimWorld source code and XML definitions. The core goal is to combine lexical retrieval, semantic retrieval, and graph structure navigation to build a service that can be called by AI assistants. The project can be used as a command-line tool or integrated into assistants like Claude Desktop or VS Code Copilot as an MCP server. The current source code includes index building, hybrid retrieval strategies, cross-layer graph relationships, and complete source code positioning and return functions.

## Idea Introduction

- The project is mainly divided into three parts: index building pipeline, retrieval tools, MCP server related

### Index Building Pipeline

- The indexing process starts from raw data, which mainly includes two types of content: one is C# source code files, the other is XML definition files (Defs). The goal of indexing is not to store the entire repository intact, but to extract the smallest retrievable and understandable units. These units should retain enough context for reading while being small enough for precise matching and semantic vector generation. Therefore, the first step is to scan the file system, read files one by one (glob) and classify them, recording basic metadata such as file path, encoding, timestamp as input identifiers for index items. Elements include classes, functions, variable definitions, and xmldef.

- After reading the files, long texts need to be split into several blocks. For C# code, this is usually split according to syntax symbols: fragments are based on classes, methods, properties, or comment blocks, while recording the start and end offsets of each fragment in the source file for subsequent precise backtracking. For XML Def, the string representation of the entire definition node is usually retained as a block because Def itself is relatively independent and self-contained. When splitting, a title for each block will be generated (such as symbol name or Def name), summary fragments (for quick preview), and body text for retrieval. The splitting strategy will affect recall quality: blocks that are too large may cause context corruption, blocks that are too small may lose relevant information.

- After text blocks are generated, the indexing process parallelizes sending each block into two different storage layers: inverted index for traditional lexical retrieval (implemented with Lucene here) and vector index for semantic retrieval. When writing to the inverted index, structured fields such as the block's title, type (C# or XML), path, source offset, tags are stored together, these fields support both filtering and positioning results back to source files. The vector part requires converting text blocks into vector representations first, which is usually done by external embedding services. The project supports batch parameter configuration for embedding generation to adjust batch processing size under different memory or CPU conditions. After generating vectors, these vectors are written to the vector index and mapped with corresponding block IDs. Since C#'s huggingface support is like eating shit, I specially started a Python server in the background at 127.0.0.1:5000 for embedding services, which also saves cold start time for each query. Speaking of vector operations, the project uses .NET's SIMD acceleration features, using the System.Numerics.Tensors library to make vector dot product operations run very fast, fully utilizing hardware parallel computing on modern CPUs.

- Another key part of indexing is graph relationship building. The static analyzer will look for meaningful reference relationships when parsing source code and XML, such as inheritance, method calls, field references, XML to C# bindings (some Def specifies using some component or class), etc. Each time a relationship is discovered, it is written as a directed edge in the graph storage. The graph's node and edge attributes are stored in tsv, and additionally, two (row and column) compressed sparse matrix representations are used to store, so that writing can be done in O(1) time, reading downward relationships, reading upward relationships. At the same time, since binary bit storage is used instead of complete edge information text, I compressed the original B-tree database size of 6.2gb to 400mb. These edges supplement the shortcomings of text similarity, making cross-layer queries (such as "which Defs use this C# component") answerable efficiently.

### Retrieval Recall Part

- There are four retrieval recall tools in total: rough_search, two-way dependency tree search uses, used_by, and whole code recall get_item. My core idea is to minimize the direct return of useless source code blocks as much as possible to reduce noise in the large model context. The typical search path of a large model agent is: first rough search for a question, get a list of candidate elements, then the large model selects the most interesting element name, recalls the whole code; or find dependency relationships through get_uses and get_used_by, get element list, select the most interesting element name to recall the whole code. If the complete code is directly displayed in the 5 results of rough search, and only one segment of useful code, then the remaining useless information filling the large model context will cause its performance to decline faster. But if only one result is returned, there is a certain probability that rough search cannot find the correct result (very likely the correct result is second!), and except for the second rough search, the large model cannot retreat with other more vague retrieval conditions, get search result candidates. In this case, the large model can only desperately try different searches repeatedly, or simply adopt wrong information, leading to rising user blood pressure. Rather than that, it's better to split the search into two steps, fully utilize the large model's judgment ability, reduce unnecessary information noise.

- Rough search is divided into two steps: first use Lucene fuzzy search and BM25 literal similarity sorting to quickly index all about 80,000 elements, select the most similar 1000. Then implement semantic similarity scoring based on vector similarity for these 1000, select 5 candidates. This can guarantee speed, and does not miss the correct answer as easily as imagined. At first I planned to add the literal similarity score and vector semantic similarity score, but did not get the advantages of each (literal fast and stable, vector strong understanding ability), instead making noise drown out information; for example, when searching "pawn hunger tick", if the last step only uses vector sorting, totalNutritionConsumptionPerday can rank third, but if some literal matching degree is weighted, then a bunch of xxxxx.Tick() will pile up in front causing nothing to be searched out.

- Graph retrieval has no special design, after all, my three graph databases together are fast enough, and the registration of each edge attribute can also conveniently pre-filter results by type, reduce noise. By the way, during testing, the large model once suddenly searched for the referenced relationship of Verse.Thing, MCP directly returned 26,000 pieces of data, causing context overflow. Later I rewrote the return content sorting to ensure sorting stability, based on this to do a simple pagination mechanism, which is considered to use the actual running request times and token amount as the cost to ensure some security.

- In terms of deployment and performance, the project provides adjustment parameters for embedding generation and vector index batch size to balance speed and resource usage under different memory and hardware configurations. In actual operation, Lucene retrieval delay is usually small, while semantic reordering and vector search will vary with candidate set and vector model size, so in resource-limited environments, you can choose to turn off embedding or reduce batch to ensure stable operation.

- To start this system for interaction or integration, the most direct way is to run the MCP server component, the service process exposes interfaces with standard input output or JSON-RPC protocol, external AI assistants can send retrieval and navigation requests to it and receive structured responses. You can also run various tool commands in command line mode for index building, hybrid retrieval, or graph query, this way is convenient for offline testing and scripting.

### Subsequent Iteration Direction

- Although the rough search stage's Lucene -> embedding sounds good, taking the advantages of both, but in actual testing and experiments, it is found that Lucene filters out more correct options than expected. Therefore, in this process, the sacrificed accuracy is not worth the few seconds of retrieval speed gain we get, on the contrary, since the LLM cannot get the desired information, it may continuously execute searches, leading to greater overhead. In this case, I decided to abandon the hybrid two-stage retrieval architecture, directly adopt full vector retrieval, and for recall speed, Faiss can provide enough algorithmic acceleration. Of course, our Lucene and BM25 reordering is not completely useless: in the remaining three tools, since the input needs to be accurate element names, many times it will lead to recall failure, we can use this set of efficient and fast retrieval and literal similarity scoring to replace the original direct matching, provide more robustness, improve the smoothness in the Agent workflow. This experience is also a lesson: in the face of the powerful understanding ability of the model, any artificial engineering method may become a bondage, not only cannot integrate the corresponding advantages, but also use its native shortcomings to greatly limit the model's play. In engineering design, traditional engineering methods should take a back seat, cannot treat the model as a bridge to fill some defects, stuff the model into the original architecture to perform simple predictable tasks, limit the model's information input and operation permissions. On the contrary, it should take a back seat, provide auxiliary information for it, so that it can give full play to the model's ability.

- In addition, in some experiments, I intensively tested the get_uses and get_used_by tools, found that although these two tools recall stably and completely, but for many classes, because our relationship extraction is too detailed, Rimworld's source code is complex enough, when extracting upstream and downstream relationships, it may return ultra-large amounts of data. This situation is not limited to one or two core defined classes, such as ThingComp and Pawn, but will appear on many code elements that implement specific functions. This brings a lot of noise to the large model's judgment. Therefore, I plan to design a set of weighing algorithms, attach priority weights to the edges of the graph, and sort the results of querying dependency relationships in reverse order, so that important links can be ranked to the front with higher probability, indirectly reducing the noise in the information obtained by the large language model. The weight of the edge may be related to the type of edge, the importance index of the node (for the importance index of the node, I plan to use the pure classic Google PageRank algorithm, and I assume that the computational overhead should not be huge), and even there may be weighting factors calculated from the literal similarity of the two names. However, I have some concerns that this set of weight algorithms, because it is artificially intuitively designed, may not reflect the theoretical importance, and may even have negative optimization on retrieval efficiency. But if seeking non-artificial design optimal weighting methods, it is even impossible to define the problem, let alone from an algorithmic perspective. I don't want to decide weighting by patting my head, so it seems that a lot of experiments in the future are unavoidable. 😩🤌 Mama Mia.

- There are also some small todos, such as some very enthusiastic friends suggested, hoping to be able to call remote embedding model api services, instead of only local models, there are suggestions to start the 5000 port embedding server with one key during initialization, and there are friends who hope to use SSE transmission instead of Stdio to avoid some drawbacks of the Stdio method, and also convenient for LAN transmission. These are all very good suggestions, the relevant changes have been put on the agenda, please be patient.
- **Update**: The remote embedding API feature has been implemented! You can now connect to external embedding services like OpenAI using the `--api-key` and `--model-name` parameters. A big thank you to user asvc_33 for their contribution!

- I sincerely thank the RimWorld modder circle for their enthusiastic support, I love you all. This project follows the MIT license, which means I hope my code can be freely used by all creatures within the solar system. In the process of communication and discussion, I also gained a lot. Therefore, thank you for your support.

---

## Quick Commands and Instructions (Indexing and Forced Rebuild)

Build or update index, generate inverted index, vector embeddings, and graph relationship data.

### Minimal Index Build Command (run in project root or RimWorldCodeRag directory):

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData"
```

### Embedding Generation Batch Size Example (adjust on machines with limited memory):

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --python-batch 128 --embedding-server "http://127.0.0.1:5000"
```

### Using Persistent Embedding Server (avoid cold start for each batch):

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --embedding-server "http://127.0.0.1:5000"
```

### New: Using a Remote Embedding API (e.g., OpenAI):

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --embedding-server "https://api.openai.com/v1/embeddings" --api-key "sk-..." --model-name "text-embedding-3-small"
```

### With Forced Rebuild (ignore incremental judgment, rebuild all indexes):

The `--force` argument now supports more granular control, allowing you to specify which parts of the index to rebuild, saving time.

```bash
# Force a rebuild of all indexes (equivalent to the old --force), assuming local embedding server
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force all --embedding-server "http://127.0.0.1:5000" --python-batch 512

# Force a rebuild of only the Lucene lexical index
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force lucene

# Force a rebuild of only the vector embedding index
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force embed --embedding-server "http://127.0.0.1:5000" --python-batch 512

# Force a rebuild of only the graph relationship index
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force graph
```

**Note:** --force forces emptying/refreshing existing indexes and rebuilding from scratch, suitable for complete rebuild after fixing field storage or splitting rule changes. Regular updates can remove --force to start incremental build, faster and retain unchanged data.
**Tip:** The best batch size is related to vram. On my Geforce rtx4060 laptop + 16gb vram, batch size of 256~512 is more appropriate. Exceeding this value will cause some data to be dynamically migrated to cpu, greatly reducing embedding efficiency. Everyone can experiment several times to find the best batch size.
**Tip:** Using --embedding-server can connect to a running embedding server, avoiding the overhead of reloading the model for each batch. Need to run the embedding server first: `.\scripts\start-embedding-server.ps1`

---

## CLI Query Commands (Examples and Usage)

### Hybrid Retrieval (Fast Recall + Semantic Reordering) Example:

```bash
cd src\RimWorldCodeRag
dotnet run -- rough-search --query "weapon gun" --kind def --max-results 10
```

### Find Symbol Usage (return other symbols referenced by this symbol, use --kind to limit layer):

```bash
dotnet run -- get-uses --symbol "xml:Gun_Revolver" --kind csharp
```

### Find Who Uses It (return list of symbols that depend on this symbol):

```bash
dotnet run -- get-used-by --symbol "RimWorld.CompProperties_Power" --kind xml
```

### Get Complete Source Code (return original file fragments by symbol, can attach line limit):

```bash
dotnet run -- get-item --symbol "RimWorld.Building_Door" --max-lines 200
dotnet run -- get-item --symbol "xml:Door"
```

### Common Parameter Instructions:

- `--kind` supports `csharp/cs` or `xml/def`, used to query only in one layer (C# or XML)
- `--max-results` controls the number of candidates returned, `--max-lines` controls the upper limit of source code return lines

---

## MCP Server Complete Setup Guide (5 Minute Quick Start)

### Prerequisites

- **.NET 8.0 SDK** (download from [microsoft.com/net](https://dotnet.microsoft.com/download))
- **Python 3.9+** (download from [python.org](https://python.org))
- **RimWorld Game Files** Copy Def data from your RimWorld installation directory: C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Data; C# source code: export via ILSpy or dnspy, store in project root/RimWorldData
- **Cross-platform Support**: Windows (PowerShell), Linux/macOS (Shell scripts)

### 1. Set Up Project Structure

```bash
# Clone or download project
cd RiMCP_hybrid/

# Place RimWorld data (required)
# Copy Def data from your RimWorld installation directory: C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Data
# C# source code: export via ILSpy or dnspy
# Place in: RimWorldData/ (same level as this README)
# If I upload RimWorld source code directly in the repository, Tynan will sue me to death, understand?

# Place embedding model
# mkdir -p src/RimWorldCodeRag/models/
# Download model like e5-base-v2 to: src/RimWorldCodeRag/models/e5-base-v2/
# Note: This project has special optimizations for e5-base-v2 (adding "query: " and "passage: " prefixes)
# Other models can also work but performance may slightly decrease, actually I don't know, from the experience of other modders, there is no big impact
```

### 2. Set Up Embedding Environment (One-time, run before building)

```bash
# Set up Python virtual environment and download model (required, run before building)
# Windows PowerShell:
.\scripts\setup-embedding-env.ps1

# Linux/macOS:
./scripts/setup-embedding-env.sh
```

### 3. Build Project

```bash
# Build all components (since .gitignore excludes build outputs)
dotnet build
```

### 4. Build Index (One-time Setup)

```bash
# Create search index from RimWorld data
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData"
```

### 5. Start Services

```bash
# Terminal 1: Start embedding server (keep running)
# Windows PowerShell:
.\scripts\start-embedding-server.ps1

# Linux/macOS:
./scripts/start-embedding-server.sh

# Terminal 2: Start MCP server
cd src\RimWorldCodeRag.McpServer
dotnet run
```

## MCP Server Detailed Configuration

### Environment Variable Configuration

Set these variables before running the MCP server:

```powershell
# Required: Point to the index folder path created in step 3
$env:RIMWORLD_INDEX_ROOT = "c:\path\to\RiMCP_hybrid\index"

# Optional: Embedding server URL (default: http://127.0.0.1:5000)
$env:EMBEDDING_SERVER_URL = "http://127.0.0.1:5000"
```

### Alternative: appsettings.json Configuration

Edit `src/RimWorldCodeRag.McpServer/appsettings.json`:

```json
{
  "McpServer": {
    "IndexRoot": "c:/path/to/RiMCP_hybrid/index",
    "EmbeddingServerUrl": "http://127.0.0.1:5000"
  }
}
```

## Test MCP Server

### Basic Test

```bash
cd src\RimWorldCodeRag.McpServer
# Windows PowerShell:
.\test-mcp.ps1

# Linux/macOS (if available):
./test-mcp.sh
```

### Manual JSON-RPC Test

```powershell
# Start server in one terminal
dotnet run

# Test in another terminal
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}' | dotnet run
```

## VS Code Integration

Create or edit `%APPDATA%\Code\User\globalStorage\mcp-servers.json`:

```json
{
  "mcpServers": {
    "rimworld-code-rag": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "c:/path/to/RiMCP_hybrid/src/RimWorldCodeRag.McpServer"
      ],
      "env": {
        "RIMWORLD_INDEX_ROOT": "c:/path/to/RiMCP_hybrid/index",
        "EMBEDDING_SERVER_URL": "http://127.0.0.1:5000"
      }
    }
  }
}
```

## MCP Tool Instructions

All tools are implemented and working:

### 1. **rough_search** - Hybrid Semantic Search

Use natural language queries to search RimWorld code symbols and XML definitions. Returns a list of matching project names and metadata. Then use the get_item tool to get the complete source code of any interesting results. If the search does not return relevant results, try simplifying the query to focus on basic keywords.

### 2. **get_uses** - Dependency Analysis (Downstream)

Find what a symbol depends on - show call relationships and implementation logic. Very suitable for understanding how functions work by tracing what other code/symbols are used. Then use the get_item tool to check the complete source code of any interesting dependencies.

### 3. **get_used_by** - Reverse Dependency Analysis (Upstream)

Find what uses a symbol - show reverse dependencies and call relationships. Very suitable for understanding impact scope and usage patterns by tracing who calls or references the symbol. Then use the get_item tool to check the complete source code of any interesting callers.

### 4. **get_item** - Precise Source Code Retrieval

Retrieve the complete source code and metadata of a specific symbol. Use this tool after finding interesting symbols from rough_search, get_uses, or get_used_by results. Returns complete class definitions, method implementations, or XML definitions with detailed metadata.

## MCP Server Troubleshooting

### "Index not found" Error

- Make sure you ran the index build step: `dotnet run -- index --root "..\..\RimWorldData"`
- Check if the `RIMWORLD_INDEX_ROOT` environment variable points to the `index/` folder

### "Embedding server connection failed" Error

- Start the embedding server first:
  - Windows PowerShell: `.\scripts\start-embedding-server.ps1`
  - Linux/macOS: `./scripts/start-embedding-server.sh`
- Wait for "Model loaded successfully" message
- Check if running on port 5000

### Build Failure

- Make sure .NET 8.0 SDK is installed
- Run `dotnet build` from project root

### No Search Results

- Verify RimWorldData folder contains game files
- Try simpler search queries (e.g. "pawn" instead of "pawn hunger system")

## Performance Notes

- **Cold Start**: ~2-5 seconds (load index)
- **Hot Query**: 0.5-1 second
- **Memory Usage**: Vector index ~300MB
- **Recommended GPU** for embedding server (significant acceleration)

## Update Instructions

When RimWorld updates:

1. Update `RimWorldData/` folder with new game files
2. Rebuild index: `dotnet run -- index --root "..\..\RimWorldData" --force`

## Related Documentation

- Implementation details: `docs/` directory
- MCP Protocol: https://modelcontextprotocol.io/

---

## Suggested Steps in Workflow

### First Use:

First build the complete index (without --force or use --force if you want to ensure clean state), then run several typical queries to confirm results.

### After Code or Parsing Rule Updates:

If only a small number of files changed, use incremental indexing; if splitting or storage fields were modified, use --force for complete rebuild.

### Before Handing MCP Service to AI Assistant:

Use CLI locally to verify common queries, ensure get-item can return correct source code fragments, and check that graph queries (get-uses/get-used-by) results are reasonable.

### Cross-platform Support:

- Windows: Use PowerShell scripts (.ps1)
- Linux/macOS: Use shell scripts (.sh)
- Both platforms support the same functions and parameters

### Daily Maintenance:

Put index building or incremental updates into CI process, run verification scripts after key branch changes.

---

## Troubleshooting Quick Reference

- **Cannot Find Symbol:** Check symbol format, XML use `xml:DefName`, C# use `Namespace.ClassName`; rebuild index if necessary and verify
- **Embedding Generation Failure or OOM:** Reduce `--python-batch`, or use smaller model or only inverted index in GPU-less environment
- **MCP No Response:** Check if startup directory and command are correct, confirm `dotnet run` can start server alone in command line, and check console logs to locate errors
- **Remote API Errors:** When using `--api-key`, ensure you also provide `--embedding-server` (the API URL) and `--model-name`.
