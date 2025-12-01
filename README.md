# RiMCP_hybrid

- 作者：五步蛇

[English](README-en.md)

- 这个项目提供了一套面向 RimWorld 源代码与 XML 定义的检索与导航工具，核心目标是把词法检索、语义检索和图结构导航结合起来，构建一个能被 AI 助手调用的服务。项目既可以作为一个命令行工具使用，也可以以 MCP 服务器的形式被集成到像 Claude Desktop 或 VS Code Copilot 这样的助手中。当前源码包含索引构建、混合检索策略、跨层图关系以及完整源码定位与返回的功能实现。

## 思路介绍

- 项目主要分为三部分：索引构建管线，检索工具，mcp 服务器相关

### 索引构建管线

- 索引过程从原始数据开始，原始数据主要包括两类内容：一类是 C# 源代码文件，另一类是 XML 定义文件（Defs）。索引的目标不是把整个仓库原封不动地存入，而是把可检索、可理解的最小单元提取出来。这些单元既要保留足够的上下文以便阅读，也应足够小以便精确匹配和生成语义向量。因此第一步是扫描文件系统，逐文件读取（glob）并分类，记录文件路径、编码、时间戳等基础元数据作为索引项的输入标识。元素有类，函数，变量定义和 xmldef。

- 在读取文件后，需要把长文本切分成若干块。对 C# 代码，这通常按照语法符号来切分：分片基于类、方法、属性或注释块，同时记录每个片段在源文件中的起止偏移，以便后续精确回溯。对 XML Def，通常保留整个定义节点的字符串表示为一个块，因为 Def 本身语义相对独立且自包含。切分时会生成每个块的标题（例如符号名或 Def 名），摘要性片段（便于快速预览），以及用于检索的正文文本。切分策略会影响召回质量：块过大可能导致上下文腐化，块过小又可能丢失相关信息。

- 文本块生成后，索引流程并行地把每个块送入两个不同的存储层：用于传统词法检索的倒排索引（这里用 Lucene 实现）和用于语义检索的向量索引。写入倒排索引时，会把块的标题、类型（C# 或 XML）、路径、源偏移、标签等结构化字段一起存储，这些字段既支持过滤，也支持把结果定位回源文件。向量部分要求先把文本块转换成向量表示，这通常由外部的嵌入服务完成。项目对嵌入生成支持批量参数配置，以便在不同显存或 CPU 条件下调整批处理大小。生成向量后，这些向量会被写入向量索引，并与对应的块 ID 建立映射。由于 c#的 huggingface 支持就像吃狗屎，我专门起了一个 Python 服务器在后台 127.0.0.1:5000 进行嵌入服务，这样也能节省每次查询时冷启动的时间。说到向量运算，项目用上了 .NET 的 SIMD 加速特性，通过 System.Numerics.Tensors 库让向量点积运算跑得飞快，在现代 CPU 上能发挥硬件并行计算的全部潜力。

- 索引的另一个关键部分是图关系构建。静态分析器会在解析源码与 XML 时寻找有意义的引用关系，例如继承、方法调用、字段引用、XML 到 C# 的绑定（某个 Def 指定了使用某个组件或类）等。每发现一条关系，都会把它作为图的有向边写入图存储。图的节点和边属性存储在 tsv 中，而额外使用了两份（行和列）压缩稀疏矩阵表示来存储，这样可以在 O(1)时间写入，读取向下关系，读取向上关系。同时，由于使用二进制 bit 存储而不是完整的边信息文本，我把原先 b 树数据库的 6.2gb 大小压缩到了 400mb。这些边补充了文本相似度的不足，使得跨层查询（比如"哪些 Def 使用了这个 C# 组件"）可以高效地回答。

### 检索召回部分

- 检索召回一共有四个工具：粗搜 rough_search，两个方向的依赖树搜索 uses，used_by，和整段代码召回 get_item. 我的核心思路是尽量减少无用源代码块的直接返回，以减少大模型上下文中的噪声。大模型 agent 的典型搜索路径是：先对一个问题粗搜，得到一个候选元素列表，然后大模型选择最感兴趣的元素名字，召回整块代码；或者通过 get_uses 和 get_used_by 找依赖关系，得到元素列表，选择最感兴趣的元素名字召回整块代码。若是直接一步到位在粗搜的 5 个结果中现实完整代码，而其中有用的代码又只有一段，那么剩下无用信息填充大模型上下文就会导致其性能更快衰竭。但是如果因此只返回一个结果，则有一定概率粗搜无法找到正确的结果（很有可能正确结果就在第二名！），而除开第二次粗搜大模型又无法用其他更模糊的检索条件后退一步，得到搜索结果候选栏。这种情况下，大模型只能绝望地一遍遍尝试不同的搜索，或者干脆采用错误的信息，导致用户血压上升。与其如此，不如将搜索拆成两步，充分运用大模型的判断能力，减少无必要的信息噪声。

- 粗搜分成两步：先使用 Lucene 模糊搜索和 BM25 字面相似度排序对所有大约八万个元素进行快速索引，选取最相似的 1000 个。然后对这 1000 个实施基于向量相似度的语义相似度打分，选出候选的五个内容。这种能保证快，并且并没有想象中的那么容易漏掉正确答案。一开始我打算将字面相似度分数和向量语义相似度分数相加，但是并没有得到他们俩各自的优点（字面快而稳定，向量理解能力强），反而使得噪声淹没了信息；例如，搜索"pawn hunger tick"时，若最后一步只使用向量排序，totalNutritionConsumptionPerday 就能排在第三，而若加权一些字面的匹配度，则一大堆 xxxxx.Tick()就会堆在前面导致啥也搜不出来。

- 图检索没什么特别的设计，毕竟我的三个图数据库合在一起足够快，同时每条边属性的注册也能便利根据种类的结果预先筛选，减少噪音。顺便，在测试的时候，大模型曾经突发奇想搜索了 Verse.Thing 的被引用关系，MCP 直接返回了两万六千条数据，导致上下文溢出。后来我重写了返回内容的排序，确保排序稳定性，以此基础做了一个简单的分页机制，算是用实际运行的请求次数和 token 量为代价确保了一些安全。

- 部署与性能方面，项目对嵌入生成与向量索引的批量大小提供了调整参数，用以在不同显存与硬件配置下平衡速度与资源占用。实际运行时，Lucene 的检索延迟通常很小，而语义重排序和向量搜索会随着候选集和向量模型大小有所变化，因此在资源受限的环境里可以选择关闭嵌入或调小批次来保证稳定运行。

- 要启动这个系统用于交互或集成，最直接的方式是运行 MCP 服务器组件，服务进程以标准输入输出或 JSON-RPC 协议暴露接口，外部的 AI 助手可以向其发送检索与导航请求并接收结构化响应。也可以在命令行模式下运行各个工具命令来进行索引构建、混合检索或图查询，这种方式便于离线测试和脚本化使用。

### 后续迭代方向

- 虽然粗搜阶段的 Lucene -> embedding 听起来不错，同时取用了两者的优点，但是在实际测试和实验中发现，Lucene 筛掉的正确选项比预想中的更多。因此，在这个过程中牺牲的准确性并不值得我们获取的那几秒的检索速度加成，反而，由于 LLM 无法得到想要的信息，它有可能不断执行搜索，导致更大的开销。在这个情况下，我决定放弃混合双阶段检索的架构，直接采用全量向量检索，至于召回速度方面，我相信 Faiss 可以提供足够的算法加速。当然，我们的 Lucene 和 BM25 的重排并不是完全没用：在剩余三个工具中，由于输入都需要是准确的元素名称，很多时候会导致召回失败，我们可以使用这一套高效快速的检索和字面相似度评分替代原先的直接匹配，提供更多的鲁棒性，提升 Agent 工作流中的流畅度。这个经验同时也是一个教训：在模型的强大理解能力面前，任何人造的工程方法都有可能成为束缚，非但不能集成相应的优势，还会用其原生的不足极大限制模型的发挥。在工程设计上，传统工程方法不能将模型当成填补某个缺陷的桥梁，在原有架构中塞入模型执行简单可预测的任务，限制模型的信息输入和操作权限。相反，应该退居二线，为其提供辅助的信息，这样才能充分发挥模型的能力。

- 另外一些实验中我高强度测试了 get_uses 和 get_used_by 工具，发现这两个工具虽然召回稳定而完整，但是对于很多的类来说，由于我们的关系提取太过细致，Rimworld 的源码又足够复杂，提取上下游关系时有可能返回超大量数据。这个情况并不局限于一两个核心定义的类，如 ThingComp 和 Pawn 之类，而是在很多实现具体功能的代码元素上都会出现。这为大模型的判断带来了不少的噪声。因此，我计划设计一套权重算法，为图的边附上优先级权重，查询依赖关系的结果按照倒序排序，就能更高概率将重要的链接排到前面，变相降低大模型获得的信息中的噪声。边的权重也许会和边的种类，节点的重要性指数（节点的重要性指数上我计划使用经典的谷歌 Pagerank 算法，应该算力开销不会无比巨大），甚至可能有加权因子是由两者名字的字面相似程度计算而来。然而，我有一些担心这套权重算法由于是人工直观设计，并不能反映理论上的重要性，甚至会对检索效率产生负面优化。但是若寻求非人工设计的最优解加权方式，却甚至无法定义问题所在，从算法角度更是无可下手。我不想拍脑袋决定加权，这样看来，未来大量的实验应该是避免不了的。😩🤌 Mama Mia.

- 还有一些小代办，比如一些十分热情的朋友反映的，希望可以调用远程嵌入模型 api 服务，而不是仅限于本地模型，有建议初始化时一键启动 5000 端口嵌入服务器的，还有朋友希望能够用 SSE 传输代替 Stdio ，来避免 Stdio 方式的一些弊端，也方便局域网传输。这些都是非常好的建议，相关的改动已经提上日程，请大家耐心等待（在做了，在做了.jpg）。
- **v1.1.0 更新**：远程嵌入 API 的功能已经实现了。现在你可以通过 `--api-key` 和 `--model-name` 参数来连接到像 OpenAI 这样的外部嵌入服务了。感谢 asvc_33 大佬的贡献！
- **v1.1.2 更新**：我事实上并没有放弃lucene方法，相反，我做了一个实验，稍微改变了bm25的运行方式，现在bm25会在各元素代码全文（而不是原先的元素名称）中匹配字面相似度，这是更合理并且普遍的一种做法。经过试验，其实大约是发挥了应有的效果，同时召回并不算慢。这样看来，faiss的必要性减弱了。
- **v1.2.0 更新**：我给图生成设计了一套边的权重生成公式。目前为：
  ```math
    $$(Pr\cdot 10^7)\sqrt{d}\cdot w$$
  ```
  这是个很简单的公式，其中Pr代表目标节点的Pagerank原始分数，d代表同一个连接在这个元素中的出现次数，w代表节点的属性所自带的权重（这个权重是我自己编的，比如继承为2.0，函数调用为0.7，并没有什么特别的依据）。$10^7$的目的是防止浮点数误差，因为Pr值一般数量级都较小。
- **1.2.1 更新**：在不断的实验中我发现了图生成目前有非常大的问题，比如会对父类的关系也全盘继承导致返回大量结果，噪声很大，同时会漏捕捉静态成员和反射调用的情况（实际上xml和c#的连接大多数都是这种方式，然而此前全部漏分析了）。这需要对我们解析代码的方式做出一些深度的重构，目前我先修复了重复抓取边的问题，将原先将近五千万条边降到了只有23万条，是一个对可用性比较大的提升。这也解释了我此前的疑惑：为什么实际生产中大模型极少调用get-uses和get-used-by工具？因为他们实在太难用了！

- 我真心特别感谢边缘世界 modder 圈子各位的热情支持，我爱你们。这个项目遵循 MIT 协议，意思就是我希望我的代码可以任意为太阳系内的所有生物自由使用。在交流和探讨过程中，我也收获了很多。因此，谢谢大家的支持。

---

## 快速命令与说明

构建或更新索引，生成倒排索引、向量嵌入和图关系数据。

### 最小索引构建命令（在项目根或 RimWorldCodeRag 目录下运行）：

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData"
```

### 嵌入生成批次大小示例（在显存受限的机器上调整）：

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --python-batch 128 --embedding-server "http://127.0.0.1:5000"
```

### 使用持久嵌入服务器（避免每次批次冷启动）：

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --embedding-server "http://127.0.0.1:5000"
```

### 新增功能：使用远程嵌入 API（例如 OpenAI）：

```bash
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --embedding-server "https://api.openai.com/v1/embeddings" --api-key "sk-1234567890abcdefghijklmnopqrstuvwxyz" --model-name "text-embedding-3-small"
```

### 带强制重建（忽略增量判断，重新构建全部索引）：

`--force` 参数现在支持更精细的控制，可以指定重建索引的特定部分，从而节省时间。

```bash
# 强制重建所有索引（等同于旧的 --force）这里假设embedding-server使用本地嵌入服务
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force all --embedding-server "http://127.0.0.1:5000" --python-batch 512

# 仅强制重建 Lucene 词法索引
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force lucene

# 仅强制重建向量嵌入索引
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force embed --embedding-server "http://127.0.0.1:5000" --python-batch 512

# 仅强制重建图关系索引
cd src\RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData" --force graph
```

**注意：** --force 强制清空/刷新已有索引并从头构建，适用于修复字段存储或切分规则变更后的完全重建。常规更新可去掉 --force 以启动增量构建，更快且保留未变更的数据。

**提示：** 最佳 batch 大小和 vram 有关。在我的 Geforce rtx4060 laptop + 16gb vram 上，256~512 的 batch 大小是比较合适的。超过这个值会导致部分数据被动态迁移至 cpu，极大降低嵌入效率。各位可以多试验几次，找到最佳 batch 大小。

**提示：** 使用 --embedding-server 可以连接到已运行的嵌入服务器，避免每次批次重新加载模型的开销。需要先运行嵌入服务器：`.\scripts\start-embedding-server.ps1`

---

## CLI 查询命令

### 混合检索示例：

```bash
cd src\RimWorldCodeRag
dotnet run -- rough-search --query "weapon gun" --kind def --lexical-k 2000 --max-results 10
# 此处的lexical-k参数默认1000，代表第一阶段字面相似度粗筛得到的结果，语义相似度的检索会在这些当中选择。越大的k，越有可能得到预期的结果，相应的，耗时也会越长。
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

## MCP 服务器完整设置指南

### 前置要求

- **.NET 8.0 SDK**（从 [microsoft.com/net](https://dotnet.microsoft.com/download) 下载）
- **Python 3.9+**（从 [python.org](https://python.org) 下载）
- **RimWorld 游戏文件** 从你的 RimWorld 安装目录复制 Def 数据：C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Data；C#源码：通过 ILSpy 或者 dnspy 导出，存进项目根目录/RimWorldData
- **跨平台支持**：Windows (PowerShell)、Linux/macOS (Shell 脚本)

### 1. 设置项目结构

```bash
# 克隆或下载项目
cd RiMCP_hybrid/

# 放置 RimWorld 数据（必需）
# 从你的 RimWorld 安装目录复制Def数据：C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Data
# C#源码：通过ILSpy或者dnspy导出
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

### 4. 构建索引（一次性，在实际服务前运行）

```bash
# 从 RimWorld 数据创建搜索索引
cd src/RimWorldCodeRag
dotnet run -- index --root "..\..\RimWorldData"
```

### 5. 启动服务

```bash
# 终端 1：启动嵌入服务器（保持后台运行，默认127.0.0.1:5000端口）
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
- **远程 API 错误：** 当使用 `--api-key` 时，请确保同时提供了 `--embedding-server` (API 的 URL) 和 `--model-name`。
