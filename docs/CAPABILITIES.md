# 能力清单（竞品对照 + 我们的取舍与排期）

> 日期：2026-10-12 ｜ 对照对象：Tavo、RikkaHub（含 LastChat fork）、言枢旧版（Lumina.Agent / Lumina.Roleplay）、Agents Anywhere
> 结论先行：**我们的底座（工具循环 / 权限 / 时间线 / 记忆 / 角色卡）已经不弱于任何一家，短板集中在「模型的发现与切换」「文件解析」「脚本执行」「会话分支」四块。**
>
> **实施进度（2026-10-12 更新）**：上面说的四块短板，v1.1 + v1.2 已全部落地到逻辑层，
> 311 项单元测试通过，x86 Debug 与 ARM Release 两个构建都过，桌面实测可用。
> 剩余工作集中在**界面**（模型选择器、Markdown 渲染等）与**真机验证**。
> 详见 §7 实施进度与验证记录。

---

## 0. 一句话结论

| 维度 | 我们的位置 |
| --- | --- |
| **已经领先** | 工具调用 + 审批白名单、时间线过程轨道、提示词装配管线（5 种注入位置）、多 provider 请求构造（含 Anthropic 工具结果合并这种细节）、**内嵌 CPython 3.15（已接入 ARM 包）** |
| **持平** | 世界书、用户人设、记忆池、角色卡导入（PNG/JSON v2+v3）、推理内容渲染、token 统计、**会话分支、群聊、正则转换、备份恢复、模型元数据与自动配置、Office/PDF 解析、TTS、翻译** |
| **仍落后** | 扩展机制（插件 / EJS / JS API）、语音与生图、**Markdown/公式渲染与模型选择器界面（逻辑已就绪，界面未做）**、MCP 的 OAuth 与 SSE 传输 |
| **独有优势** | 内嵌 **CPython 3.15** 与 **设备端 C 扩展编译**（别的移动端 AI 客户端都做不到） |

---

## 1. 调研范围与信息来源

| 对象 | 类型 | 来源 | 本地副本 |
| --- | --- | --- | --- |
| **Tavo** | 商业 App（iOS/Android，角色扮演方向） | 官方文档 `docs.tavoai.dev/llms.txt`（全站目录）+ App Store/Play | 无（闭源） |
| **RikkaHub** | 开源 Android（AGPL-3.0，社区功能最全） | 源码 + 官方文档 | `research\rikkahub` ✅ 已 clone |
| **LastChat** | RikkaHub 的 fork（UI 重做 + 功能增补） | 源码 | `research\LastChat` ✅ 已 clone |
| **PocketPal / MLC Chat** | 端上跑 GGUF 的本地模型客户端 | 第三方评测 | — |
| **言枢旧版** | 我们自己的前身（Lumina.Agent + Lumina.Roleplay） | 源码 | `Yanshuai-AI` ✅ 已 clone |
| **Agents Anywhere** | 远端工作区客户端（UI 参照） | 源码 | `H:\项目\UWP\Agents Anywhere` ✅ |
| **Python UWP（Project-AlterGate）** | CPython 3.15 的 ARM32 W10M 移植 | 源码 + 验证记录 | `H:\项目\UWP\Python UWP` ✅ |
| **Project-Chronos-Collapse** | 设备端 C 编译 + 模块装载运行时 | 源码 | `H:\项目\UWP\Project-Chronos-Collapse` ✅ |

> 说明：Tavo 是闭源的，能力取自官方文档目录（可信度高：它就是给用户看的操作手册）；RikkaHub / LastChat 是源码级对照。rikkahub 的逐文件实现细节另有一份探索报告在生成中，落地时以那份为准。

---

## 2. 能力矩阵

图例：✅ 完整 ｜ ⚠️ 部分/有但弱 ｜ ❌ 无 ｜ — 不适用

### 2.1 模型与 provider

| 能力 | Tavo | RikkaHub | 言枢旧版 | 我们当前 | 目标 |
| --- | --- | --- | --- | --- | --- |
| 多 provider 接入 | ✅ OpenAI/Claude/Gemini/DeepSeek/豆包/OpenRouter/Volink/自定义 | ✅ OpenAI/Gemini/Anthropic/任意兼容端点 | ⚠️ OpenAI 兼容 + Anthropic | ✅ OpenAI 兼容 + Anthropic | 加 Gemini 原生 |
| 按 provider 分组的模型选择器 | ✅ 模型设置页可查看/切换 | ✅ 助手级模型覆盖 + 全局默认 | ⚠️ 下拉 + 刷新按钮 | ❌ 只能手填模型名 | **v1 必做** |
| `/v1/models` 自动拉取 | ⚠️ 未明说 | ✅ | ✅ `ApiModelFetcher` | ✅ `ModelCatalog`（已实现拉取） | 加元数据 |
| 模型元数据（窗口/模态/推理/价格） | ⚠️ 未明说 | ✅ 内置模型清单 | ⚠️ 仅窗口表（22 条） | ⚠️ 仅窗口表（32 条） | **v1.1 扩表** |
| 会话中途切模型 | ⚠️ | ✅ | ❌ | ❌ | v1 |
| 档案固定模型 | ✅ | ✅ | ✅ | ✅ | 保持 |

### 2.2 多模态与文件

| 能力 | Tavo | RikkaHub | 言枢旧版 | 我们当前 | 目标 |
| --- | --- | --- | --- | --- | --- |
| 图片输入（视觉） | ✅ 图片发送 | ✅ | ✅ `VisionEnabled` + `read_image` | ✅ 底层已通 | v1 补 UI |
| PDF 解析 | ❌ | ✅ 内置 MuPDF 源码 | ❌ | ❌ | v1.1（平台 `Windows.Data.Pdf` 渲染 + 压缩） |
| DOCX / PPTX / EPUB | ❌ | ✅ 自写 XmlPullParser + ZipFile | ❌ | ❌ | **v1.1 端上解析** |
| XLSX / XLS | ❌ | ❌ **它没有** | ❌ | ❌ | v1.1 做（**这一项我们要做得比它全**） |
| CSV / 代码 / Markdown | ⚠️ | ✅ | ⚠️ 纯文本附件 | ✅ 纯文本附件 | v1.1 结构化 |
| OCR | ❌ | ✅ 独立 OCR 模型 + 3 天 LRU 缓存 | ❌ | ❌ | 两条路都留：视觉模型直接看 / OCR 模型降级 |
| 附件存储 | 本地 | 本地 | 图片外置 + 文本内联 | ✅ 全部外置 | 保持 |

### 2.3 Agent 与扩展

| 能力 | Tavo | RikkaHub | 言枢旧版 | 我们当前 | 目标 |
| --- | --- | --- | --- | --- | --- |
| 内置工具调用 | ✅ 原生 + TavoJS | ✅ local tools | ✅ 19 个 | ✅ 20 个 | v1.1 补 python |
| 工具权限审批 | ❌ | ❌ | ✅ 白名单 | ✅ 白名单 + 记忆授权 | 保持（这是差异化） |
| MCP | ✅ **反向**（内置 MCP Server 让外部 agent 操作它） | ✅ 正向连接 | ✅ 正向（仅 http） | ✅ 正向（仅 http） | 加 SSE 传输 |
| 插件包机制 | ✅ `.tpg` + manifest + 开发指南 | ✅ Skills | ⚠️ Skills 模板 | ⚠️ Skills 模板 | v1.2 |
| 提示词宏 / 模板 | ✅ 宏 + **EJS** + JS API | ✅ `{{cur_date}}` 等占位符 | ❌ | ✅ 占位符 | v1.2 加条件/循环 |
| 正则转换器 | ✅ | ✅ | ❌ | ❌ | v1.1 |
| 内嵌脚本执行 | ✅ TavoJS（JS） | ✅ workspace（Linux 容器） | ❌ | ❌ | **v1.2：内嵌 CPython** |
| 设备端 C 编译 / 原生扩展 | ❌ | ❌ | ❌ | ❌ | **下个版本（独有）** |

### 2.4 人设、记忆、会话

| 能力 | Tavo | RikkaHub | 言枢旧版 | 我们当前 | 目标 |
| --- | --- | --- | --- | --- | --- |
| 角色卡 PNG/JSON 导入 | ✅ SillyTavern + 8 个站 URL | ✅ v2/v3 | ✅ + 8 个站 | ✅ PNG tEXt/zTXt + JSON v2/v3 | 加 URL 导入 |
| 世界书 | ✅ | ✅ | ✅ | ✅ 关键词/正则/常驻/优先级 | 加编辑页 |
| 提示词注入位置 | ✅ 预设 | ✅ 5 种位置 | ❌ | ✅ 5 种位置 | 保持 |
| 用户人设 | ✅ | ✅ | ✅ 多套 | ✅ 多套 | 保持 |
| 长记忆 | ✅ | ✅ 私有/全局/最近会话 | ⚠️ 三套并行 | ✅ 档案池 + 全局池 | 加管理页 |
| 会话分支 | ⚠️ 仅重生成 | ✅ 任意消息分叉 | ✅ 分支系统 | ❌ | **v1 必做** |
| 消息编辑 / 重生成 | ✅ | ✅ | ✅ | ⚠️ 底层支持，UI 未做 | v1 |
| 群聊（多角色） | ✅ 成员管理 + 回复模式 | ❌ **它明确没有**（那是另一个 fork 的功能） | ❌ | ❌ | v1.1（我们要做的比它多） |
| 消息翻译 | ✅ | ✅ | ❌ | ❌ | v1.1 |

### 2.5 界面、数据、周边

| 能力 | Tavo | RikkaHub | 言枢旧版 | 我们当前 | 目标 |
| --- | --- | --- | --- | --- | --- |
| 自定义主题 | ✅ 主题 + CSS + 导出分享 | ✅ Material You + 调色板 | ✅ 5 套主题 | ⚠️ 令牌体系（未做主题切换） | v1.1 浅色 |
| 局域网 Web 访问 | ❌ | ✅ 内置 HTTP 服务器 | ❌ | ❌ | 不排期（手机端价值低） |
| 备份 / 恢复 | ✅ 本地（含 Key） | ✅ WebDAV / S3 | ✅ zip 12 分片 | ❌ 未实现 | **v1.1** |
| TTS / ASR | ✅ ElevenLabs/Volink/Gemini + 本地 | ✅ | ❌ | ❌ | v1.2 |
| 图片生成 | ✅ `/imagine` | ✅ | ❌ | ❌ | v1.2 |
| 快捷消息 | ✅ | ✅ | ❌ | ❌ | v1.1 |
| 存储空间管理 | ✅ | — | ❌ | ❌ | v1.1 |
| 技术日志（token/触发） | ✅ | ✅ | ✅ API 日志页 | ✅ 脱敏诊断日志 | 保持 |
| 纯本地存储 | ✅ | ✅ | ✅ | ✅ | 保持（差异化） |

---

## 3. 你的新需求：逐条落地方案

### 3.1 多 provider、多 model，随时切换（v1 必做）

**现状**：数据模型已经支持，UI 缺失。
- `ApiProfileFile.Resolve()` 已是三级回退：档案指定 → 全局默认 → 第一个
- `Profile.Api.Model` 可覆盖 provider 的默认模型
- `ChatService.SendAsync` **每次发送都重新解析 provider 与密钥**，所以"切了立刻生效"不需要改逻辑

**要做的**：
1. 引入显式的模型引用类型，替代现在的两个散字段：
   ```csharp
   struct ModelRef { string ApiProfileId; string ModelId; }
   ```
   解析顺序：`Conversation.ModelOverride` → `Profile.ModelRef` → `ApiProfileFile.DefaultId` + 该 provider 的默认模型
2. **模型选择器页面**（设计稿 §6.9 已有雏形，要扩成"全量模型"形态）：
   - 顶部搜索框（跨 provider 过滤）
   - 按 provider 分组（组头显示 provider 名 + 连通状态点 + 模型数）
   - 每行：模型名 + 能力徽标（👁 视觉 / 🧠 推理 / 128k 窗口 / $ 价格档）
   - 行尾：当前选中打勾；长按设为"档案默认"
   - 底部：`跟随档案默认` / `固定到此模型`
3. 会话级切换：把 `Conversation` 加一个 `modelOverride` 字段（一次改动）

### 3.2 `/v1/models` 自动配置模型 + 上下文窗口 + 能力（v1.1）

**现状**：`ModelCatalog.FetchAsync` 已经能拉列表并推导端点（`/chat/completions` → `/models`），但只拿到 id。

**三层元数据来源**（按优先级）：
1. **服务端字段**：
   - OpenRouter 会返回 `context_length`、`pricing.{prompt,completion}`、`architecture.modality`、`supported_parameters`（含 `tools`、`reasoning`）→ 直接可用，这是最理想的情况
   - Anthropic `/v1/models` 返回 `id` + `display_name` + `created_at`（无窗口）
   - OpenAI 只返回 `id` + `created`（几乎没有信息）
2. **本地内置元数据表**（把现在的 32 条窗口表扩成约 200 条）：字段为 `窗口 / 模态 / 是否推理 / 价格档`。命中规则沿用子串匹配（现有实现），新增：
   - 视觉：`vision`、`4o`、`4.1`、`gemini`、`qwen-vl`、`-vl-`、`claude` 全系、`gpt-4-turbo`
   - 推理：`reasoner`、`-r1`、`thinking`、`o1`、`o3`、`o4`、`qwq`
   - 音频：`audio`、`whisper`、`realtime`
3. **用户手动覆盖**：模型行内可编辑窗口与能力开关；覆盖结果按 `provider + modelId` 存进本地缓存文件

**UI 要标出来源**：`服务端` / `内置表` / `手动` / `未知`。未知时明确显示"能力未知，可手动指定"，而不是猜。

### 3.3 多模态（v1 补 UI，底层已通）

**现状**：`ImagePart`、`ChatRequest.VisionEnabled`、`ApiProfile.VisionEnabled`、OpenAI 的 `image_url` 数组形态、Anthropic 的 `image` block、`read_image` 工具（`__IMAGE__:` 标记 → runner 自动插一条带图用户消息）全部已实现并有单测。

**要做的**：图片选择器 + 缩略图预览 + 发送前压缩到大模型可接受尺寸 + 对不支持视觉的模型给明确降级提示（"当前模型不支持图片，已忽略"）。

### 3.4 文件解析（含 Office）：v1.1 可端上做，PDF 是例外

**可行性依据**：UWP 的 .NET Standard 2.0 面里有 `System.IO.Compression.ZipArchive` 与 `System.Xml.Linq`（我在编译产物的 bin 列表里确认过这两个程序集都在）。而 **OOXML 的本质就是 zip + XML**，所以 Office 三件套可以完全在端上解析：

| 格式 | 解析方式 | 产出 |
| --- | --- | --- |
| `.docx` | 解压 → `word/document.xml` → 抽 `<w:t>`，按 `<w:p>` 分段 | 纯文本 |
| `.xlsx` | `xl/sharedStrings.xml` + `xl/worksheets/sheetN.xml` → 按行列还原 | 管道分隔的表（模型对表格友好） |
| `.pptx` | `ppt/slides/slideN.xml` → 抽 `<a:t>`，按页分节 | 带页号的分节文本 |
| `.csv` / `.tsv` | 直接读，按行截断 | 文本 |
| 代码 / md / json / xml | 直接读 | 文本 |
| `.pdf` | **不在端上做** | 见下 |

**PDF：走平台渲染 + 压缩（已确认）**

关键发现：**UWP 自带 PDF 渲染，不用自己写**。`Windows.Data.Pdf.PdfDocument` 从 Windows 8.1 / WP 8.1 起就在，W10M 上可用：

```
PdfDocument.LoadFromFileAsync(file)
  → doc.GetPage(i).RenderToStreamAsync(stream, options)   // 每页渲染成位图
  → BitmapEncoder (Windows.Graphics.Imaging) 重新编码并按需降采样
  → 得到的图片走已有的视觉通道（ImagePart）发给模型
```

所以 PDF 的方案是**双通道**：

| 通道 | 做法 | 什么时候用 |
| --- | --- | --- |
| 文本 | 解 xref + Flate 解压内容流 + 抽文本操作符 | PDF 是文本型 → 直接进上下文，最省 token |
| 图片 | `Windows.Data.Pdf` 渲染页面 → 压缩 → 视觉模型 | PDF 是扫描件或含图表 → 文本抽不出东西时自动回退 |

**图片压缩**（你特别提到的一点，必须做）：手机拍的扫描件单页可能 3~5 MB，直接送模型既慢又可能超限。策略：
- 长边限制在 1568 px（主流视觉模型的有效上限附近）
- `BitmapEncoder` 以 JPEG quality 0.75 重编码
- 单页目标 ≤ 400 KB；多页 PDF 只取前 N 页（默认 5 页，可配），并在上下文里说明"只送了前 5 页"
- 与现有约束对齐：单附件上限 25 MB（`Limits.MaxAttachmentBytes`）

**统一入口**：新增 `Core/Files/DocumentExtractor.cs`（纯逻辑，可单测），按扩展名分派：

| 格式 | 端上怎么做 |
| --- | --- |
| `.docx` | ZipArchive → `word/document.xml` → 抽 `<w:t>`，按 `<w:p>` 分段 |
| `.xlsx` | `xl/sharedStrings.xml` + `xl/worksheets/sheetN.xml` → 还原成管道分隔表 |
| `.pptx` | `ppt/slides/slideN.xml` → 抽 `<a:t>`，按页分节 |
| `.csv` / `.tsv` / 代码 / md / json / xml | 直接读，按行截断 |
| `.pdf` | 平台渲染成图（+ 简易文本抽取） |

依据：UWP 的 .NET Standard 2.0 面里有 `System.IO.Compression.ZipArchive` 与 `System.Xml.Linq`（我在编译产物的 bin 列表里确认过），而 OOXML 的本质就是 zip + XML。

产出统一为 `ExtractedDocument { Text, Images[], PageCount, Truncated, Warnings[] }`，注入上下文时按 token 预算截断（复用现有的 `KnowledgeTotalChars` 上限逻辑）。

### 3.5 内嵌 Python：可行，且是别家做不到的能力（v1.2）

**我核对过的硬事实**（不是推测）：

| 项 | 事实 |
| --- | --- |
| 版本 | **CPython 3.15.0**（`cpython/Include/patchlevel.h`） |
| 包 | `PyUwp.Runtime.0.2.0-preview.1.nupkg`（5.61 MB） |
| 内含 | `python-uwp.dll` 4.9 MB、`z.dll` 68 KB、`Lib/` **622 个纯 Python 文件**、3 个头文件、`buildTransitive/PyUwp.Runtime.targets` |
| 集成方式 | `<PackageReference>`，targets 自动把两个 DLL 与整个 `Lib` 树标成 `DeploymentContent` 打进 appx |
| 平台 | **仅 ARM**（win10-arm）；x86/x64 只给警告 → **Python 功能只能在手机上跑，桌面调不了** |
| 调用 | `PyUwpRuntime_Initialize(runtime_root, local_site)` → `RunUtf8(source)` → `LastOutput()` / `LastError()` → `Finalize()` |
| 线程模型 | 头文件明确要求：**必须在专用引擎线程上调用，不能从 UI 线程同步调用** |
| 真机验证 | 已在 Lumia 950 / W10M 15254.603 上跑通 `Initialize → RunUtf8("answer = 6*7") → EvalInt → Finalize`，返回 42 |
| TCC 扩展编译 | `PyUwpExtensionCompiler.h` + `PyUwpTcc.h` 已存在（可把 C 源码在内存里编译成 Python 扩展）；需要 appx 声明 `codeGeneration` 能力，且 `PyUwpEnableTcc=true` 才链接 |

**能力边界（我在 0.2.0 的 Lib 里逐个查的）**：

| 模块 | 有没有 | 影响 |
| --- | --- | --- |
| `_socket` / `_ssl` / `_hashlib` | ❌ | **Python 脚本不能联网、不能做加密** |
| `zipfile` | ❌ | **不能解压** → pip 装不了（这正是"pip 放下个版本"的原因） |
| `_sqlite3` | ❌ | sqlite3 实际不可用 |
| `json` / `http` / `urllib`（纯 Python 层）/ `subprocess.py` | ✅ | 前三个可用；subprocess 在沙箱里起不了进程 |
| `_socket` 等原生扩展的补法 | — | 走 TCC 扩展编译（就是 Chronos-Collapse 那条线） |

**定位结论**：Python 在手机上的价值不是"联网脚本"，而是**本地计算与数据处理**：解析我们抽出来的 Office 文本、算数据、做格式转换、写小工具、跑模型给的算法。要联网由**宿主代发**（脚本调我们注入的桥，C# 侧用已有的 `HttpTransport` 请求并回填结果）。

**落地方案**：
1. 新工具 `run_python(script, timeout?)`：在引擎线程执行，捕获 stdout/stderr，返回给模型
2. 脚本落盘到 `<档案>/files/scripts/`，可复用、可版本化
3. **注入一个宿主桥模块 `yanshuai`**（纯 Python 文件 + P/Invoke 回调），暴露 `read_file / write_file / list_files / http_request / memory_search / now()`
4. 安全：默认关闭；开启后仍走审批；限制执行时间与输出大小；路径仍受 `PathSandbox` 约束
5. 体积影响：2.6 MB → 约 8 MB（可接受）

**风险（要提前知道）**：
- ARM-only → 无法在桌面验证 Python 路径，每次改动都要真机测
- Python 引擎崩溃会影响宿主进程（UWP 里做不到完全隔离），只能靠限制脚本能力 + 及时 `Finalize`
- 引擎线程模型要接进我们现有的 ThreadPool 与取消机制，`CancellationToken` 传不进 Python，只能靠"到点 Finalize + 重启运行时"

### 3.6 pip（下个版本，你说的对）

**依赖链**（缺一个都跑不起来）：

```
pip 能装包
├── zipfile        ← 解 wheel（现在是缺的）
├── _socket/_ssl   ← 从索引下载（现在缺，且需要 TCC 编译扩展）
├── subprocess     ← 构建 sdist（沙箱里起不了进程，需换路线）
└── 可靠的 site-packages 写入  ← local_site 已有（LocalState 可写目录）
```

**建议路线**：不做"从 PyPI 在线装"，而是做**离线 wheel 安装器**：
1. 用 `PyUwpExtensionCompiler` 先把 `zipfile`（zlib 已有）、`_socket`、`_ssl` 用 TCC 编译出来注入 `sys.modules`
2. pip 换成最小子集：只支持"给定本地 wheel 目录 → 解开 → 放到 `local_site`"（跳过依赖解析与网络）
3. 需要 appx 加 `codeGeneration` 受限能力（侧载没问题）

这件事确实应该独立成一个版本，因为它动的是原生扩展编译这条链，和普通功能开发的风险等级不一样。

---

### 3.7 rikkahub 源码实证：值得直接借鉴的六件事

基于逐文件扒的源码（`research\rikkahub`，1502 个文件），这六条建议直接采用：

1. **模型元数据改用"打分式匹配"**（`ai\...\registry\ModelRegistry.kt` + `ModelDsl.kt`）
   它的匹配器是 `TokenSequenceMatcher` / `NotTokenSequenceMatcher` / `ExactIdMatcher` 三种，支持 `tokens`（必须命中）、`notTokens`（命中就排除）、`exact`（精确 id）、`tokenRegex`，最后取最高分。
   我现在的实现是"最长模式优先"，能解决 `gpt-4` 抢 `gpt-4o-mini` 这类重叠，但解决不了**排除词**场景（例如 `deepseek-r1-distill-qwen-7b` 会被 `qwen` 家族和 `deepseek` 家族同时命中，得靠排除词区分）。**v1.1 升级成"模式 + 排除词 + 权重"**。

2. **模型选择器的 UX 细节**（`app\...\ui\components\ai\ModelList.kt`）
   顶部搜索框 · **收藏模型区（可拖拽排序）** · provider 折叠分组且**折叠状态持久化** · 每行带 类型/模态/能力 三种标签。这四条我已经写进设计稿 §6.9，可以直接照做。

3. **会话配置快照**（`data\model\ConversationConfig.kt`）
   会话创建时从档案拍一份 config 快照（模型 / 推理强度 / 搜索 / MCP / 技能），之后改动只落在会话上；模型被删除后靠 `UIMessage.modelSnapshot` 仍能显示历史用的是哪个模型。我们的会话级覆盖已有，但缺"快照语义"与"模型名快照"，补上就不会出现"档案改了模型导致老会话串味"。

4. **分支的数据结构：`MessageNode{ messages[], selectIndex }`**（`data\model\Conversation.kt`）
   一个节点 = 同一位置的多条候选消息 + 一个选中下标。**编辑 = 往节点追加候选；重生成 = 追加候选；分叉 = 复制到该节点为止另起会话**（标题自动去重 `标题(1)`/`标题(2)`）。
   这比旧言枢的"分支点 + 全量分支数组"简单太多，**建议 v1 直接采用这个模型**，`TimelineProjector` 只需按 `selectIndex` 取消息即可。

5. **Skills 懒加载 + GitHub 导入**（`data\files\SkillManager.kt`、`data\ai\tools\SkillsTools.kt`）
   格式是 `<skillsDir>/<name>/SKILL.md`（YAML frontmatter 写 name/description）；**注入时只放 name + description**，正文由 `use_skill` 工具按需加载（description 截断 1024 字符并做 XML 转义防注入）；支持从 `.md` / `.zip` / **GitHub 仓库（Contents API 递归下载）** 导入，并用 `skills-lock.json` 记录来源与 hash。
   我们的 Skills 现在是"全量模板直接塞"，v1.1 改成懒加载 + GitHub 导入。

6. **工具的供应商内置能力开关**（`ai\...\provider\Model.kt` 的 `BuiltInTools`）
   Gemini 的 google-search / url-context / 生图是按模型单独开关的，不是全局开关。做 Gemini 原生接入时顺手带上。

### 3.8 rikkahub 的六处短板 = 我们的产品机会

| # | 它的短板（源码实证） | 我们的位置 |
| --- | --- | --- |
| 1 | 模型元数据覆盖率极低：98 条模型里**只有 8 条**有上下文窗口，**完全没有价格** | 我们 110+ 条、几乎都有窗口、带价格档 → **领先** |
| 2 | 角色卡导入只映射 4 类字段；`mes_example` / `alternate_greetings` / 内嵌世界书 / `extensions` **全丢** | 我们全部保留 → **领先** |
| 3 | 世界书不支持 SillyTavern 的 `secondaryKeys`、`selectiveLogic(AND/OR/NOT)`、概率触发、角色位 | 我们已支持关键词/正则/常驻/优先级/深度，可补齐剩下几项 |
| 4 | 记忆很弱：无向量检索、无自动抽取、无剪枝，注入方式是"全量塞进 system prompt" | 我们是 BM25 + 云端向量 + 自动抽取 + 剪枝 + 两层池 → **领先** |
| 5 | 没有子代理、没有群聊（多助手只是彼此隔离的配置） | 群聊按你要求提前到 v1.1；子代理我们已有 |
| 6 | 上下文管理是"截断 + 检查点摘要"，不是检索式召回 | 我们是压缩摘要 + RAG 召回，两条路都有 |

**一条方向性差异值得记下来**：rikkahub 用 **proot 沙箱跑 Linux 用户态**来实现"设备端脚本执行"（还要下载 rootfs、绑 mount、限制 512KB/2MB/128K）。我们在 W10M 上做不到 proot —— **但我们有原生 CPython 3.15**。真解释器比"用户态模拟的 Linux"更干净，这是我们的差异化，不是缺陷。

还有两条细节值得抄：**工具循环步数它给到 256**（我们用 15，手机上可以放宽到 40~60）；**MCP 支持 OAuth 2.1（DCR + PKCE + 刷新）与 SSE / Streamable HTTP 两种传输**，我们目前只有裸 HTTP + 手填 header —— OAuth 是真实缺口，排 v1.2。

---

## 4. 排期（按已确认的决策定稿）

> ⚠️ 这张表是**当时的计划**。实际完成情况见 §7.1 —— v1.1 与 v1.2 的逻辑部分已全部落地，
> 剩下的是界面与真机验证。

| 版本 | 内容 | 为什么这个顺序 |
| --- | --- | --- |
| **v1（收尾）** | 多 provider 模型选择器 · 会话分支与重生成 · 图片附件 UI · Markdown 渲染 · 错误重试 · 部署脚本 | 这四项是"能不能日常用"的门槛；模型选择器不做，多 provider 就是摆设 |
| **v1.1** | **Gemini 原生接入** · `/v1/models` 自动配置 + 元数据表 · **Office 三件套 + PDF（平台渲染）+ 图片压缩** · **群聊** · 备份恢复 · 快捷消息 · 正则转换器 · 记忆/世界书管理页 · 浅色主题 · 存储空间管理 | 都是端上逻辑，风险低、收益直观；群聊按你的要求提前到这里 |
| **v1.2** | **内嵌 Python 真运行时**（`run_python` + 宿主桥）· TTS · 消息翻译 · 会话内 token 技术日志 | Python 需要 ARM 真机迭代，单独一版好回滚 |
| **下个版本** | pip（离线 wheel，依赖 TCC 原生扩展编译）· PDF 端上文本抽取（实验性）· 图片生成 · 局域网 Web 访问 | 都要动原生编译或大工程量 |

---

## 5. 已确认的决策（2026-10-12）
| # | 决策 | 影响 |
| --- | --- | --- |
| 1 | **Gemini 做原生接入**（多一套请求构造） | 新增 `GeminiProvider`：`generateContent` 端点、`parts[]` 消息形态、`functionDeclarations` 工具形态、`inline_data` 多模态。排在 v1.1 |
| 2 | **Python 先做桌面可跑的桩**，真机再换真运行时 | 引出 `IScriptRuntime` 抽象：桌面用「假运行时」（能跑通"写脚本 → 执行 → 拿输出 → 回填给模型"的全链路并单测），ARM 上换 `PyUwpRuntime`。**好处是接口与工具逻辑在桌面上就能验证完**，真机只需要替换实现 |
| 3 | **PDF 做图片/文字双通道抽取，图片必须压缩** | 用平台自带的 `Windows.Data.Pdf` 渲染页面（不用自己写渲染器），渲染后按长边 1568px + JPEG 0.75 压缩；文本型 PDF 优先走文本抽取 |
| 4 | **群聊提前到 v1.1** | 需要新增：群成员模型、回复模式（轮流 / 指定 / 自由）、群内多条人设的提示词拼装、按成员头像快速触发指定发言 |

### 5.1 关于第 2 条的具体设计（因为它是新引入的抽象，写清楚）

```csharp
// Core/Scripting/IScriptRuntime.cs
public interface IScriptRuntime
{
    string Name { get; }                  // "python-stub" | "python-uwp"
    bool IsReal { get; }                  // 桩为 false，用于在 UI 上明确标注
    bool IsAvailable { get; }
    Task<ScriptResult> RunAsync(ScriptRequest request, CancellationToken ct);
}

public sealed class ScriptRequest
{
    public string Source;                 // 脚本文本
    public string WorkingDirectory;       // 档案内的相对目录（受 PathSandbox 约束）
    public int TimeoutSeconds;            // 默认 30
    public int MaxOutputChars;            // 默认 20000
    public Dictionary<string,string> HostBridge;  // 宿主桥配置（可用能力清单）
}

public sealed class ScriptResult
{
    public bool Ok; public string StdOut; public string StdErr;
    public int ExitCode; public long DurationMs; public bool TimedOut;
}
```

| 实现 | 位置 | 用途 |
| --- | --- | --- |
| `StubScriptRuntime` | `Core/Scripting/`（纯逻辑，桌面与 ARM 都能编） | 桌面开发与单测：不真执行 Python，按脚本内容返回可预期的结果（含错误、超时、超长输出等分支），用来验证工具层、审批、输出截断、错误回填 |
| `PyUwpScriptRuntime` | `Platform/`（P/Invoke `python-uwp.dll`） | 真机：专用引擎线程 + `PyUwpRuntime_Initialize/RunUtf8/LastOutput/Finalize` |

`run_python` 工具只依赖 `IScriptRuntime`，所以**工具本身在桌面上就是可测的**；真机上唯一要额外验证的是引擎线程与崩溃恢复。

### 5.2 仍需你后续拍板的

1. **包体积上限**：现在 2.6 MB → 加 Python 约 8 MB → 若再加 PDF 渲染相关（平台 API 不额外占体积，但页面图缓存会占存储）大约 8~9 MB。可接受吗？
2. **PDF 单次送几页**：默认 5 页；扫描件多的话要不要提到 10 页？
3. **Python 宿主桥开放到什么程度**：我建议只开 `read_file / write_file / list_files / http_request / memory_search / now`，**不开**执行外部进程与任意路径访问。要不要再加别的？

---

## 6. 附：本地已有的可复用素材

| 素材 | 位置 | 能拿来做什么 |
| --- | --- | --- |
| CPython 3.15 ARM32 UWP 运行时 | `H:\项目\UWP\Python UWP\artifacts\PyUwp.Runtime.0.2.0-preview.1.nupkg` | 内嵌 Python（v1.2，**已接入 ARM 包**） |
| Python Terminal 宿主（可运行 appx） | `H:\项目\UWP\Python UWP\terminal\` | 集成参考：`main.cpp` 展示了 ABI 的完整调用序 |
| 设备端 C 编译器与模块装载器 | `H:\项目\UWP\Project-Chronos-Collapse` | 补 `_socket`/`_zipfile` 等原生扩展（pip 的前置） |
| 更强的 Markdown 渲染（表格/代码高亮/图片） | `Yanshuai-AI\Lumina.Agent\Pages\Chat\MarkdownBlock.cs`（1093 行） | 直接搬 |
| 公式排版（真正的二维盒模型） | `Yanshuai-AI\Lumina.Agent\Pages\Chat\LatexRenderer.cs`（1366 行） | 直接搬 |
| 时间线与模板的成熟实现 | `H:\项目\UWP\Agents Anywhere\AgentsAnywhere.Mobile\MainPage.xaml` | UI 重做参照 |
| RikkaHub / LastChat 源码 | `research\rikkahub`、`research\LastChat` | 模型元数据表、文件解析、分支、主题的具体做法 |

---

## 7. 实施进度与验证记录（2026-10-12）

### 7.1 排期的实际完成情况

| 版本 | 计划内容 | 状态 |
| --- | --- | --- |
| **v1.1** | Gemini 原生接入 | ✅ 完成（`generateContent` / `parts[]` / `functionDeclarations` / `inline_data` / 安全拦截，11 项单测） |
| | `/v1/models` 自动配置 + 元数据表 | ✅ 完成（110+ 条内置表、打分式匹配、四家服务端形态解析、分组目录） |
| | Office 三件套端上解析 | ✅ 完成（docx / xlsx / pptx，zip + XML，XXE 防护 + zip 炸弹防护） |
| | PDF 平台渲染 + 图片压缩 | ✅ 完成（`Windows.Data.Pdf` + 长边 1568px + JPEG 0.75，纯逻辑部分已单测） |
| | 群聊 | ✅ 完成（成员模型 + 轮流/指定/自由三种模式 + 多人设拼装） |
| | 备份恢复 | ✅ 完成（zip、二进制安全、事务式恢复、zip-slip 拒绝、清单校验） |
| | 快捷消息 | ✅ 完成（共享区存储 + 占位符复用 `Placeholders.Resolve`） |
| | 正则转换器 | ✅ 完成（作用域 + visualOnly + 编译缓存 + 超时） |
| | 存储空间统计 | ✅ 完成（按可清理维度分类，按 UTF-8 字节计） |
| | 记忆/世界书管理页、浅色主题 | ❌ **未做**（纯界面工作，见 UI-HANDOFF §1.2） |
| **v1.2** | 内嵌 Python 真运行时 | ✅ 完成（`IScriptRuntime` + 桩 + `run_python` + 宿主桥 + `PyUwpScriptRuntime`；ARM 包已带原生载荷） |
| | TTS | ✅ 完成（Markdown 清洗 + 按标点切段 + generation 计数打断 + `MediaPlayer` 播放） |
| | 消息翻译 | ✅ 完成（译文叠加不覆盖原文、按语言缓存、代码块数量校验） |
| | 会话内 token 技术日志 | ✅ 完成（JSONL 追加、坏行跳过、只记元数据、可复制导出） |
| **界面** | 模型选择器页 / Markdown 渲染 / 附件缩略图 / 长按菜单 / 浅色主题 | ❌ **未做**（本轮只做了能验证逻辑的简易入口） |

### 7.2 本轮修掉的真实缺陷（都不是重构，是实测发现的）

| 缺陷 | 症状 | 根因与修法 |
| --- | --- | --- |
| **UWP 上 JSON 持久化完全失效** | 所有档案/会话/配置都读不出来，界面永远显示"还没有会话"，日志只有一行"解析失败" | Newtonsoft.Json 13.x 只提供 netstandard2.0 资产，在 UWP 5.4.7（W10M 15254 自带框架）上 `JsonSerializerSettings` 的静态构造访问自己的 `StreamingContext` 字段就抛 `FieldAccessException`。**实测 10.0.3 / 12 / 13.0.3 都一样**，换版本解决不了。<br>**修法**：确认 `JObject`/`JToken`/`JsonTextReader`/`JsonTextWriter` 是好的，于是保留它的分词器、自己写对象↔节点树映射（`Core\Storage\JsonBridge.cs`）。同时把 Newtonsoft 降到 10.0.3（唯一会退回可用资产的版本） |
| 备份会破坏图片等二进制文件 | 备份再恢复后图片打不开 | 备份路径按文本 UTF-8 写二进制。加了 `AtomicFile.WriteBytesAsync` |
| pptx 解析出来是空的 | 只能拿到幻灯片标题 | 命名空间写成了 drawingml，实际是 presentationml。改为按 `LocalName` 匹配 |
| 快捷栏整条不显示 | 无任何报错，就是没有 | 在 `Page.Resources` 里找 `Application.Resources` 的样式 —— 索引器不会往上找父级，抛异常；而调用处是 fire-and-forget，异常被吞。改为 `Application.Current.Resources.TryGetValue` 并记日志 |
| 测试替身掩盖了二进制缺陷 | "二进制没被备份"这类 bug 在测试里看不见 | `InMemoryFileStore` 把文本与二进制分两个字典存，`ListFilesAsync` 只列文本。改为只存字节，与真实文件系统一致 |
| 用量日志拆解下标错位 | 报错被当成正常输出 | PyUwp 包装脚本分两次 `write` 标记序列，中间多出空片段。改为一次写完，并加测试钉住 |
| 显式 `null` 覆盖默认子对象 | 老文件里 `"memory":null` 会让 `Profile.Memory` 变 null | 自研映射层漏了 `NullValueHandling.Ignore` 的读侧语义。补上 |

### 7.3 验证状态（哪些验过、哪些没验）

**已在桌面实机验证**：应用注册并启动、3 个档案与 3 条会话正确读出、群聊会话渲染、
快捷栏显示并可填入输入框、分支条显示"候选 2/2"且切换生效并落盘、"我的"页各入口正常、
循环网络豁免已授予（`CheckNetIsolation`）。

**已验证的构建**：x86 Debug（8.06 MB 包）、ARM Release（.NET Native，**8.17 MB 包，含 PyUwp 载荷**）。

**未验证（需要真机）**：
1. `run_python` 真的能执行 Python —— 桌面加载不了 ARM 版 `python-uwp.dll`，只能验到"工具链路 + 协议拆解"。
2. `os.chdir` 到档案目录 —— 依赖 `GetNativePath()` 返回的真实路径，桌面无 UWP 沙箱路径可验。
3. TTS 实际发声与 `MediaPlayer` 的 `MediaEnded` 时序。
4. PDF 渲染在手机内存下的表现。
5. `.NET Native` 下的反射是否被 rd.xml 覆盖全 —— `JsonBridge` 的映射走反射，rd.xml 已逐个声明模型类型，但 ARM 包**没有在设备上跑过**，这是上真机时的第一优先检查项。

### 7.4 待你拍板的三件事（原 §5.2，现在有实测数字了）

1. **包体积**：实测 ARM Release **8.17 MB**（含 CPython 载荷；不含时 2.71 MB）。可接受吗？若嫌大，可以把 `Lib\` 里用不到的模块裁掉（比如 `tkinter`、`idlelib`、`test`、`unittest`、`email` 等，保守估计能省 1.5–2 MB）。
2. **PDF 单次送几页**：默认 5 页。
3. **Python 宿主桥开放范围**：当前只开了 **`memory.search` 与 `time.now`**（都只读），刻意没开文件读写与网络 —— 脚本是模型生成的，等于把"模型能做的事"再放大一圈。要不要放宽到 `read_file` / `list_files`？

