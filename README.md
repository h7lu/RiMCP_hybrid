# RiMCP_hybrid

- 作者：五步蛇

* 这个项目提供了一套面向 RimWorld 源代码与 XML 定义的检索与导航工具，核心目标是把词法检索、语义检索和图结构导航结合起来，构建一个能被 AI 助手调用的服务。项目既可以作为一个命令行工具使用，也可以以 MCP 服务器的形式被集成到像 Claude Desktop 或 VS Code Copilot 这样的助手中。当前源码包含索引构建、混合检索策略、跨层图关系以及完整源码定位与返回的功能实现。

* 项目主要分为三部分：索引构建管线，检索工具，mcp 服务器相关

## 索引构建管线

- 索引过程从原始数据开始，原始数据主要包括两类内容：一类是 C# 源代码文件，另一类是 XML 定义文件（Defs）。索引的目标不是把整个仓库原封不动地存入，而是把可检索、可理解的最小单元提取出来。这些单元既要保留足够的上下文以便阅读，也应足够小以便精确匹配和生成语义向量。因此第一步是扫描文件系统，逐文件读取（glob）并分类，记录文件路径、编码、时间戳等基础元数据作为索引项的输入标识。元素有类，函数，变量定义和 xmldef。

- 在读取文件后，需要把长文本切分成若干块。对 C# 代码，这通常按照语法符号来切分：分片基于类、方法、属性或注释块，同时记录每个片段在源文件中的起止偏移，以便后续精确回溯。对 XML Def，通常保留整个定义节点的字符串表示为一个块，因为 Def 本身语义相对独立且自包含。切分时会生成每个块的标题（例如符号名或 Def 名），摘要性片段（便于快速预览），以及用于检索的正文文本。切分策略会影响召回质量：块过大可能导致上下文腐化，块过小又可能丢失相关信息。

- 文本块生成后，索引流程并行地把每个块送入两个不同的存储层：用于传统词法检索的倒排索引（这里用 Lucene 实现）和用于语义检索的向量索引。写入倒排索引时，会把块的标题、类型（C# 或 XML）、路径、源偏移、标签等结构化字段一起存储，这些字段既支持过滤，也支持把结果定位回源文件。向量部分要求先把文本块转换成向量表示，这通常由外部的嵌入服务完成。项目对嵌入生成支持批量参数配置，以便在不同显存或 CPU 条件下调整批处理大小。生成向量后，这些向量会被写入向量索引，并与对应的块 ID 建立映射。由于 c#的 huggingface 支持就像吃狗屎，我专门起了一个 Python 服务器在后台 127.0.0.1:5000 进行嵌入服务，这样也能节省每次查询时冷启动的时间。说到向量运算，项目用上了 .NET 的 SIMD 加速特性，通过 System.Numerics.Tensors 库让向量点积运算跑得飞快，在现代 CPU 上能发挥硬件并行计算的全部潜力。

- 索引的另一个关键部分是图关系构建。静态分析器会在解析源码与 XML 时寻找有意义的引用关系，例如继承、方法调用、字段引用、XML 到 C# 的绑定（某个 Def 指定了使用某个组件或类）等。每发现一条关系，都会把它作为图的有向边写入图存储。图的节点和边属性存储在 tsv 中，而额外使用了两份（行和列）压缩稀疏矩阵表示来存储，这样可以在 O(1)时间写入，读取向下关系，读取向上关系。同时，由于使用二进制 bit 存储而不是完整的边信息文本，我把原先 b 树数据库的 6.2gb 大小压缩到了 400mb。这些边补充了文本相似度的不足，使得跨层查询（比如"哪些 Def 使用了这个 C# 组件"）可以高效地回答。

## 检索召回部分

- 检索召回一共有四个工具：粗搜 rough_search，两个方向的依赖树搜索 uses，used_by，和整段代码召回 get_item. 我的核心思路是尽量减少无用源代码块的直接返回，以减少大模型上下文中的噪声。大模型 agent 的典型搜索路径是：先对一个问题粗搜，得到一个候选元素列表，然后大模型选择最感兴趣的元素名字，召回整块代码；或者通过 uses 和 used_by 找依赖关系，得到元素列表，选择最感兴趣的元素名字召回整块代码。若是直接一步到位在粗搜的 5 个结果中现实完整代码，而其中有用的代码又只有一段，那么剩下无用信息填充大模型上下文就会导致其性能更快衰竭。但是如果因此只返回一个结果，则有一定概率粗搜无法找到正确的结果（很有可能正确结果就在第二名！），而除开第二次粗搜大模型又无法用其他更模糊的检索条件后退一步，得到搜索结果候选栏。这种情况下，大模型只能绝望地一遍遍尝试不同的搜索，或者干脆采用错误的信息，导致用户血压上升。与其如此，不如将搜索拆成两步，充分运用大模型的判断能力，减少无必要的信息噪声。

- 粗搜分成两步：先使用 lucene 模糊搜索和 bm25 字面相似度排序对所有大约八万个元素进行快速索引，选取最相似的 1000 个。然后对这 1000 个实施基于向量相似度的语义相似度打分，选出候选的五个内容。这种能保证快，并且并没有想象中的那么容易漏掉正确答案。一开始我打算将字面相似度分数和向量语义相似度分数相加，但是并没有得到他们俩各自的优点（字面快而稳定，向量理解能力强），反而使得噪声淹没了信息；例如，搜索"pawn hunger tick"时，若最后一步只使用向量排序，totalNutritionConsumptionPerday 就能排在第三，而若加权一些字面的匹配度，则一大堆 xxxxx.Tick()就会堆在前面导致啥也搜不出来。

- 图检索没什么特别的设计，毕竟我的三个图数据库合在一起足够快，同时每条边属性的注册也能便利根据种类的结果预先筛选，减少噪音。顺便，在测试的时候，大模型曾经突发奇想搜索了 Verse.Thing 的被引用关系，mcp 直接返回了两万六千条数据，导致上下文溢出。后来我重写了返回内容的排序，确保排序稳定性，以此基础做了一个简单的分页机制，算是用实际运行的请求次数和 token 量为代价确保了一些安全。

- 部署与性能方面，项目对嵌入生成与向量索引的批量大小提供了调整参数，用以在不同显存与硬件配置下平衡速度与资源占用。实际运行时，Lucene 的检索延迟通常很小，而语义重排序和向量搜索会随着候选集和向量模型大小有所变化，因此在资源受限的环境里可以选择关闭嵌入或调小批次来保证稳定运行。

- 要启动这个系统用于交互或集成，最直接的方式是运行 MCP 服务器组件，服务进程以标准输入输出或 JSON-RPC 协议暴露接口，外部的 AI 助手可以向其发送检索与导航请求并接收结构化响应。也可以在命令行模式下运行各个工具命令来进行索引构建、混合检索或图查询，这种方式便于离线测试和脚本化使用。

---

## 快速命令与说明（索引与强制重建）

构建或更新索引，生成倒排索引、向量嵌入和图关系数据。

### 最小索引构建命令（在项目根或 RimWorldCodeRag 目录下运行）：

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData"
```

### 带强制重建（忽略增量判断，重新构建全部索引）：

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force
```

### 嵌入生成批次大小示例（在显存受限的机器上调整）：

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --python-batch 128
```

**注意：** --force 强制清空/刷新已有索引并从头构建，适用于修复字段存储或切分规则变更后的完全重建。常规更新可去掉 --force 以启动增量构建，更快且保留未变更的数据。
**提示：** 最佳 batch 大小和 vram 有关。在我的 Geforce rtx4060 laptop + 16gb vram 上，256~512 的 batch 大小是比较合适的。超过这个值会导致部分数据被动态迁移至 cpu，极大降低嵌入效率。各位可以多试验几次，找到最佳 batch 大小。

---

## CLI 查询命令（示例与用法）

### 混合检索（快速召回 + 语义重排序）示例：

```bash
cd src\RimWorldCodeRag
dotnet run -- rough-search --query "weapon gun" --kind def --max-results 10
```

### 查找某个符号使用（返回该符号引用的其他符号，用 --kind 限制层）：

```bash
dotnet run -- get-uses --symbol "xml:Gun_Revolver" --kind csharp
```

### 查找被谁使用（返回依赖该符号的符号列表）：

```bash
dotnet run -- get-used-by --symbol "RimWorld.CompProperties_Power" --kind xml
```

### 获取完整源码（按符号返回原始文件片段，可附带行数限制）：

```bash
dotnet run -- get-item --symbol "RimWorld.Building_Door" --max-lines 200
dotnet run -- get-item --symbol "xml:Door"
```

### 常用参数说明：

- `--kind` 支持 `csharp/cs` 或 `xml/def`，用于只在某一层（C# 或 XML）查询
- `--max-results` 控制返回候选数，`--max-lines` 控制源码返回行数上限

---

## MCP 服务器完整设置指南（5 分钟快速开始）

### 前置要求

- **.NET 8.0 SDK**（从 [microsoft.com/net](https://dotnet.microsoft.com/download) 下载）
- **Python 3.9+**（从 [python.org](https://python.org) 下载）
- **RimWorld 游戏文件**（复制 `RimWorldXXXX_Data/StreamingAssets/Data` 到项目根目录为 `RimWorldData/`）
- **跨平台支持**：Windows (PowerShell)、Linux/macOS (Shell 脚本)

### 1. 设置项目结构

```bash
# 克隆或下载项目
cd RiMCP_hybrid/

# 放置 RimWorld 数据（必需）
# 从你的 RimWorld 安装目录复制：RimWorldXXXX_Data/StreamingAssets/Data/*
# 放置到：RimWorldData/（与此 README 同级）
# 如果我在仓库里直接上传边缘世界源码，泰南会告死我，懂吗

# 放置嵌入模型
# mkdir -p src/RimWorldCodeRag/models/
# 下载模型如 e5-base-v2 到：src/RimWorldCodeRag/models/e5-base-v2/
# 注意：此项目对 e5-base-v2 有特殊优化（添加 "query: " 和 "passage: " 前缀）
# 其他模型也可工作但可能性能略有下降，其实我不清楚，就一些其他modder的使用体验来看，并没有多大影响
```

### 2. 设置嵌入环境（一次性，在构建前运行）

```bash
# 设置 Python 虚拟环境并下载模型（必需，在构建前运行）
# Windows PowerShell:
.\scripts\setup-embedding-env.ps1

# Linux/macOS:
./scripts/setup-embedding-env.sh
```

### 3. 构建项目

```bash
# 构建所有组件（因为 .gitignore 排除了构建输出）
dotnet build
```

### 4. 构建索引（一次性设置）

```bash
# 从 RimWorld 数据创建搜索索引
cd src/RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData"
```

### 5. 启动服务

```bash
# 终端 1：启动嵌入服务器（保持运行）
# Windows PowerShell:
.\scripts\start-embedding-server.ps1

# Linux/macOS:
./scripts/start-embedding-server.sh

# 终端 2：启动 MCP 服务器
cd src\RimWorldCodeRag.McpServer
dotnet run
```

## MCP 服务器详细配置

### 环境变量配置

在运行 MCP 服务器前设置这些变量：

```powershell
# 必需：指向第 3 步创建的索引文件夹路径
$env:RIMWORLD_INDEX_ROOT = "c:\path\to\RiMCP_hybrid\index"

# 可选：嵌入服务器 URL（默认：http://127.0.0.1:5000）
$env:EMBEDDING_SERVER_URL = "http://127.0.0.1:5000"
```

### 替代方案：appsettings.json 配置

编辑 `src/RimWorldCodeRag.McpServer/appsettings.json`：

```json
{
  "McpServer": {
    "IndexRoot": "c:/path/to/RiMCP_hybrid/index",
    "EmbeddingServerUrl": "http://127.0.0.1:5000"
  }
}
```

## 测试 MCP 服务器

### 基础测试

```bash
cd src\RimWorldCodeRag.McpServer
# Windows PowerShell:
.\test-mcp.ps1

# Linux/macOS (if available):
./test-mcp.sh
```

### 手动 JSON-RPC 测试

```powershell
# 在一个终端启动服务器
dotnet run

# 在另一个终端测试
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}' | dotnet run
```

## VS Code 集成

创建或编辑 `%APPDATA%\Code\User\globalStorage\mcp-servers.json`：

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

## MCP 工具说明

所有工具都已实现并可以工作：

### 1. **rough_search** - 混合语义搜索

使用自然语言查询搜索 RimWorld 代码符号和 XML 定义。返回匹配项目名称列表及元数据。随后使用 get_item 工具获取任何感兴趣结果的完整源代码。如果搜索没有返回相关结果，请尝试简化查询以聚焦基本关键词。

### 2. **get_uses** - 依赖分析（下游）

查找符号依赖什么 - 显示调用关系和实现逻辑。非常适合通过追踪使用了哪些其他代码/符号来理解功能的工作原理。随后使用 get_item 工具检查任何感兴趣依赖项的完整源代码。

### 3. **get_used_by** - 反向依赖分析（上游）

查找什么使用了符号 - 显示反向依赖和调用关系。非常适合通过追踪谁调用或引用了符号来理解影响范围和使用模式。随后使用 get_item 工具检查任何感兴趣调用者的完整源代码。

### 4. **get_item** - 精确源代码检索

检索特定符号的完整源代码和元数据。从 rough_search、get_uses 或 get_used_by 结果中找到感兴趣的符号后使用此工具。返回完整的类定义、方法实现或 XML 定义及详细元数据。

## MCP 服务器故障排查

### "Index not found" 错误

- 确保运行了索引构建步骤：`dotnet run -- index --root "..\..\RimWorldData"`
- 检查 `RIMWORLD_INDEX_ROOT` 环境变量是否指向 `index/` 文件夹

### "Embedding server connection failed" 错误

- 先启动嵌入服务器：
  - Windows PowerShell: `.\scripts\start-embedding-server.ps1`
  - Linux/macOS: `./scripts/start-embedding-server.sh`
- 等待"Model loaded successfully"消息
- 检查是否在端口 5000 运行

### 构建失败

- 确保安装了 .NET 8.0 SDK
- 从项目根目录运行 `dotnet build`

### 无搜索结果

- 验证 RimWorldData 文件夹包含游戏文件
- 尝试更简单的搜索查询（例如 "pawn" 而不是 "pawn hunger system"）

## 性能说明

- **冷启动**：~2-5 秒（加载索引）
- **热查询**：0.5-1 秒
- **内存使用**：向量索引约 300MB
- **推荐 GPU** 用于嵌入服务器（显著加速）

## 更新说明

RimWorld 更新时：

1. 使用新游戏文件更新 `RimWorldData/` 文件夹
2. 重新构建索引：`dotnet run -- index --root "..\..\RimWorldData" --force`

## 相关文档

- 实现详情：`docs/` 目录
- MCP 协议：https://modelcontextprotocol.io/

---

## 在工作流中使用的建议步骤

### 第一次使用：

先完整构建索引（无 --force 或用 --force 如果你想确保干净状态），然后运行若干典型查询确认结果。

### 代码或解析规则更新后：

如果只是少量文件改动，使用增量索引；若修改了切分或存储字段，使用 --force 完整重建。

### 在将 MCP 服务交给 AI 助手前：

在本地用 CLI 验证常用查询，确保 get-item 能返回正确源码片段，并检查图查询（get-uses/get-used-by）的结果合理性。

### 跨平台支持：

- Windows: 使用 PowerShell 脚本（.ps1）
- Linux/macOS: 使用 shell 脚本（.sh）
- 两个平台都支持相同的功能和参数

### 日常维护：

把索引构建或增量更新放入 CI 流程中，关键分支变更后运行验证脚本。

---

## 故障排查速查

- **无法找到符号：** 检查符号格式，XML 用 `xml:DefName`，C# 用 `Namespace.ClassName`；必要时重新构建索引并验证
- **嵌入生成失败或 OOM：** 减小 `--python-batch`，或在无 GPU 的环境下使用较小模型或仅使用倒排索引
- **MCP 无响应：** 检查启动目录与命令是否正确，确认 `dotnet run` 能在命令行单独启动服务器，并查看控制台日志以定位错误
