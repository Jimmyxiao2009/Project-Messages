# UI 交接说明 —— 给接手继续打磨设计稿的 AI

> 日期：2026-10-12 ｜ 项目：`H:\项目\UWP\Project-Messages\src\Yanshuai.Messages`
> 设计依据：`docs/设计稿.md`（完整设计文档）与 `design/prototype/`（高保真原型，15 屏）
>
> **状态：逻辑层（`Core\` + `Platform\`）已全部完成，311 项单元测试通过，x86 Debug 与
> ARM Release（.NET Native）两个构建都过，桌面实测可用。你的工作面只有 `Ui\` + `Themes\Design.xaml`。**

---

## 0. 你的任务边界

把当前这个"能用的简易 UI"打磨成设计稿的样子。**不要动 `Core\` 与 `Platform\`。**

需要新的数据或行为时，**先提接口需求**，由逻辑层提供，不要往 `Ui\` 里塞业务逻辑。
下面第 1.2 节列出了"逻辑已经现成、只是缺页面"的全部清单 —— 那些是你最值得先做的。

---

## 1. 现在有什么

### 1.1 已完成的逻辑能力（都有单测）

| 能力 | 位置 |
| --- | --- |
| 工作区装载 / 档案 CRUD / 删除进回收区 | `Core\Storage\Workspace.cs` |
| 会话正文与索引分离、列表只读索引 | 同上 |
| 模型档案 + 密钥进 `PasswordVault` | `Workspace` + `Platform\UwpSecrets.cs` |
| **JSON 持久化（自研映射层）** | `Core\Storage\JsonStore.cs` + **`JsonBridge.cs`** ← 见第 5 节，改动前必读 |
| OpenAI 兼容 / Anthropic / **Gemini 原生** 三套 provider | `Core\Providers\` |
| 模型元数据表（110+ 条，打分式匹配）+ 服务端元数据解析 + 分组目录 | `Core\Providers\ModelMetadata.cs` |
| Agent 工具循环（轮次上限、取消、审批、图片回传） | `Core\Agent\AgentRunner.cs` |
| 20 个内置工具（含 `run_python`） | `Core\Tools\BuiltinTools.cs` + `ScriptTools.cs` |
| 敏感操作审批（白名单 + 记忆授权） | `Core\Permissions\PermissionService.cs` |
| 时间线投影（纯函数） | `Core\Timeline.cs` |
| **会话分支**（附加式候选池 + 分叉 + 标题去重） | `Core\Models\Conversations.cs` |
| **正则转换器**（作用域 + visualOnly + 编译缓存 + 超时） | `Core\Text\RegexTransformer.cs` |
| **群聊**（成员 + 三种回复模式 + 多人设拼装） | `Core\Models\Group.cs` |
| 记忆池（BM25 + 云端向量 + 剪枝） | `Core\Memory\MemoryService.cs` |
| 角色卡导入（PNG tEXt/zTXt + JSON v2/v3） | `Core\Cards\CardImporter.cs` |
| 上下文压缩 | `Core\ContextCompressor.cs` |
| **文档解析**（docx / xlsx / pptx 端上解析） | `Core\Files\OoxmlReader.cs`、`DocumentExtractor.cs` |
| **图片压缩**（纯逻辑）+ PDF 渲染（`Windows.Data.Pdf`） | `Core\Files\ImageCompressor.cs`、`Platform\UwpPdfRenderer.cs` |
| **备份与恢复**（zip，二进制安全，事务式恢复） | `Core\Backup\BackupService.cs` |
| **快捷消息**（共享区，占位符展开） | `Core\Text\QuickMessages.cs` |
| **存储空间统计**（按可清理维度分类） | `Core\Storage\StorageStats.cs` |
| **朗读**（Markdown 清洗 + 切段 + 打断） | `Core\Speech\SpeechText.cs`、`TtsService.cs`、`Platform\UwpSpeechSynthesizer.cs` |
| **消息翻译**（译文叠加、按语言缓存、代码块保护） | `Core\Translation\TranslationService.cs` |
| **会话级用量日志**（JSONL，只记元数据） | `Core\Diagnostics\TokenTechLog.cs` |
| **脚本运行时抽象**（桌面桩 + 真机 PyUwp） | `Core\Scripting\`、`Platform\PyUwpScriptRuntime.cs` |
| 脱敏诊断日志 | `Core\Diagnostics\DiagnosticLog.cs` |

### 1.2 界面现状

```
HomeView   根 Pivot：对话 / 档案 / 我的
ChatView   会话页：顶栏 + 待处理条 + 时间线 + 分支条 + 运行条 + 朗读条 + 快捷栏 + 输入行
```

已经接上的（本轮新加）：快捷消息栏、分支切换条、朗读条、"存储空间"页、"备份与恢复"页、
"快捷消息"管理页、会话设置里的朗读/翻译/用量入口、档案编辑里的脚本开关。

**对照设计稿，缺的是页面（不是缺功能，逻辑都现成）**：

- 档案详情页（现在是弹窗）；人设分节编辑（身份 / 人设 / 模型 / 工具 / 记忆 / 外观）
- **模型选择器页**：分组 + 收藏 + 折叠状态持久化。逻辑在 `ModelCatalog`，界面完全没做
- **群聊成员编辑页**：`Conversation.Members` 已有模型与三种回复模式，界面没做
- 角色卡导入向导（现在是选文件直接导入，没有预览与补齐步骤）
- 记忆管理页（列表 / 编辑 / 整理预览 / 重建索引）
- 世界书与模式注入的编辑页
- 工具 / MCP / 技能 的选择页
- 文件与附件页
- **正则转换器的编辑页**：`RegexTransformer` 已可用，界面没有入口
- 独立的调试日志页、独立的会话用量页（现在都是弹窗）
- 浅色主题

### 1.3 本轮已接进界面的东西（**别重复做**）

这些在 `Ui\MainPage.xaml(.cs)` 里已经能用了。你要做的是把它们搬到独立页面 / 做得更好看，
不是从头接一遍。列出来是为了让你知道"数据从哪来、入口在哪"：

| 界面元素 | 事件处理器 | 依赖的服务 |
| --- | --- | --- |
| 快捷消息栏（输入框上方，横向滚动） | `RefreshQuickBarAsync` / `FillComposer` | `Workspace.Library.QuickMessages` + `QuickMessages.Visible/Expand` |
| 分支切换条（"候选 N / M" + 左右箭头） | `UpdateBranchBar` / `BranchPrev_Click` / `BranchNext_Click` / `SwitchBranchAsync` | `Conversation.VariantCount/VariantIndex/SelectVariant` |
| 朗读条（"正在朗读" + 停止） | `SpeakLastAsync` / `StopSpeechAsync` / `StopSpeech_Click` | `AppServices.Tts` + `SpeechText` |
| 消息翻译 | `TranslateLastAsync` | `AppServices.Translation` + `TranslationService.CommonLanguages` |
| 会话用量 | `ShowTechLogAsync` | `AppServices.TechLog` + `TokenTechLog.Format` |
| 存储空间页 | `Storage_Click` | `AppServices.StorageStats.MeasureAsync` |
| 备份与恢复页 | `Backup_Click` / `CreateBackupAsync` / `RestoreBackupAsync` | `AppServices.Backup` + `UwpFileStore.CreateExportFileAsync/PickBackupFileAsync` |
| 快捷消息管理页 | `QuickMessages_Click` / `BuildQuickMessageEditor` | `Workspace.Library.QuickMessages` + `SaveLibraryAsync` |
| 档案编辑里的脚本开关 | `ShowProfileEditorAsync` / `BuildRuntimeHint` | `Profile.Tools.ScriptEnabled` + `IScriptRuntime` |
| 会话设置里的四个动作 | `ChatMore_Click` | 压缩 / 朗读 / 翻译 / 用量 |

**两个"取全局资源"的帮助方法**（`MainPage` 里已有，搬到别的页面时照抄）：
`FindStyle(key)` 与 `FindBrush(key)`。它们走 `Application.Current.Resources.TryGetValue`，
而不是 `this.Resources[key]` —— 后者查不到 `Themes\Design.xaml` 里的东西，会直接抛。

### 1.4 ⚠️ 该拆了：`MainPage.xaml.cs` 已经 1970 行

第 10 节第 1 条写着"不要把业务逻辑写进 `Ui\`"，**这条规矩我自己在本轮破坏了** ——
为了让新功能能在桌面上验证，我把分支、朗读、翻译、用量、存储、备份、快捷消息管理
七块全塞进了 `MainPage.xaml.cs`，从 1400 行涨到 **1970 行、24 个事件处理器**。

建议的拆法（按现有区域分隔注释切，几乎是一一对应）：

```
Ui\
├─ MainPage.xaml(.cs)          只留：根 Pivot 三页 + 导航 + 返回键 + 小工具
├─ Pages\
│   ├─ ChatPage.xaml(.cs)      会话页：时间线 + 输入区 + 分支/运行/朗读/快捷条
│   ├─ ProfileEditorPage       档案编辑（现在是最长的一个弹窗）
│   ├─ ApiProfilesPage         模型档案
│   ├─ ModelPickerPage         模型选择器（还没做）
│   ├─ StoragePage             存储空间
│   ├─ BackupPage              备份与恢复
│   ├─ QuickMessagesPage       快捷消息管理
│   └─ TechLogPage             会话用量
└─ Dialogs\                    其余仍在用的弹窗
```

拆的时候注意：**新增 XAML 页面必须往 csproj 加一行 `<Page Include="...">`**
（C# 文件是通配的，不用管）。见第 5 节。

---

## 2. 分层与边界

```
src\Yanshuai.Messages\
├─ Core\            ← 不依赖 Windows.*，纯逻辑。桌面测试工程直接链接它编译。
│   ├─ Models\          数据模型（JsonProperty 特性即磁盘格式）
│   ├─ Storage\         Workspace 门面 / 原子写 / 目录布局 / ★JsonBridge
│   ├─ Providers\       三家 provider + SSE + 重试 + 模型目录 + 元数据表
│   ├─ Agent\           AgentRunner（工具循环）+ 工具契约
│   ├─ Tools\           20 个内置工具 + 路径沙箱 + 脚本工具
│   ├─ Scripting\       脚本运行时抽象 + 桩 + 宿主桥 + PyUwp 协议
│   ├─ Files\           OOXML 解析 / 图片压缩 / 文档抽取
│   ├─ Backup\          备份与恢复
│   ├─ Speech\          朗读文本处理 + 朗读服务
│   ├─ Translation\     消息翻译
│   ├─ Text\            正则转换器 + 快捷消息
│   ├─ Memory\          记忆池
│   ├─ Prompting\       提示词装配管线 + 占位符
│   ├─ Cards\           角色卡导入
│   ├─ Permissions\     审批
│   ├─ Diagnostics\     脱敏日志 + 会话用量日志
│   ├─ Timeline.cs      时间线投影（纯函数）
│   └─ ChatService.cs   ★ UI 的唯一入口
├─ Platform\        ← UWP 适配层：文件、凭据、HTTP、设备、PDF、语音、脚本运行时、组合根
└─ Ui\              ← ★ 你的工作面
```

**UI 只应该调用 `ChatService`、`Workspace`、以及 `AppServices` 上暴露的服务。**
组合根是 `Platform\AppServices.cs`，`AppServices.Current` 取到它。

---

## 3. 视觉规范（必须遵守，来自设计稿 §5）

### 3.1 令牌

全部在 `Themes\Design.xaml`，**页面里不许出现硬编码色值**。

- 底色层次：`BgBrush #000` → `ChromeLowBrush #171717` → `ChromeBrush #1F1F1F` → `ChromeMidBrush #2B2B2B` → `LowBrush #333`
- 文字：`TextBrush #FFF` / `SoftTextBrush` / `MidBrush #999` / `FaintBrush #666`
- 强调色：`AccentBrush` `AccentTextBrush` `AccentSoftBrush` —— 启动时被 `Ui\ThemeHelper.ApplyAccent` 换成系统强调色
- 语义色：`ThinkBrush #B4A0FF` `WarnBrush #FFB900` `ErrBrush #E81123` `OkBrush #6CCB5F`
- 字体：`IconFont` = Segoe MDL2 Assets，`MonoFont` = Consolas
- 触控：`TouchTargetHeight 44`、`IconButtonWidth 48`、`IconButtonHeight 40`、`RowHeight 56`

### 3.2 已确立、**不要推翻**的规则

1. **只有用户消息是气泡。** 靠右、强调色、**直角**、右下带小尾巴。其余一律不进气泡。
2. **过程走左侧轨道**：2px 竖线 + 节点。思考是圆点、工具是方块、失败转红。
   运行中最多只露最近 5 步（`Limits.RunningVisibleSteps`），更早的合并成"显示前面 N 步"。
   折叠规则在 `TimelineProjector.VisibleSteps`，**不要在 UI 里重写一遍**。
3. **列表靠 4px 左色条表达状态**：警告色=需要你处理、强调色=运行中、错误色=失败。
4. **审批不进时间线**，固定在会话页顶部的提示条（`NoticeBanner`）。
5. **发送与追加是同位置的互斥替换**，打断放在输入框上方的运行条里（永远点得到）。
6. **单强调色**：一屏只有一个彩色焦点。
7. **快捷消息点一下是"填进输入框"，不是直接发出去**。用户多半还要改几个字。

### 3.3 时间线模板

`Ui\TimelineTemplateSelector.cs` 按 `RowKind` 分派 5 个模板，模板都在 `MainPage.xaml` 的
`Page.Resources` 里。要加行类型就往 `RowKind` 加值 + 加模板 + 改选择器。

---

## 4. 必须保留的行为（别在重构时弄丢）

| 行为 | 为什么 |
| --- | --- |
| **生成中离开页面不取消** | 手机上切出去看别的很常见。`OnNavigatedFrom` 只解绑权限回调，不动 `CancellationToken` |
| **流式三层节流** | 250ms 批量刷新 + 气泡内累积 + 300ms 淡入。不加节流在 W10M 上会明显掉帧 |
| **时间线 ListView 保持虚拟化** | 外面套 `ScrollViewer` 会让虚拟化失效并内存暴涨（旧项目踩过） |
| **返回键优先级** | 先关会话页 → 再收搜索 → 最后交给系统退出（`IBackHandler`）。离开会话页时要 `StopSpeechAsync()` |
| **敏感操作审批白名单** | `write_file / calendar_create / make_call / send_sms / read_image / run_python`，其余直接放行 |
| **FullTrust 每次进入会话复位** | 不能在配置里持久化"永不再问" |
| **脚本执行双重门控** | `ToolOptions.ScriptEnabled`（档案开关）+ `SensitiveTools`（每次审批）。`Registry.Resolve` 里也挡了一道，别绕过 |
| **密钥只进 `PasswordVault`** | 配置文件里永远没有明文；诊断日志自动清洗 |
| **用量日志只记元数据** | 不记提示词正文，URL 只留主机名 —— 这份文件是给用户导出/贴到 issue 的 |
| **朗读用 generation 计数打断** | 只在开始时"停旧的"不够：旧任务正 await 引擎时，停完它还会继续读下一段 |

---

## 5. 硬约束（踩上去就白干）

| 约束 | 说明 |
| --- | --- |
| **C# 7.3** | 不能用 records / switch 表达式 / `using var` / 目标类型 new / 可空引用类型。桌面测试工程也用 7.3 编译 Core，写新语法会立刻在那儿报错 |
| **最低系统 14393** | 不能用 contract 5.0+ 的 API。`StackPanel.Spacing` 就属于这类（已用 `ApiInformation` 探测后再设，别改成硬写） |
| **不要引入 WinUI 3 / Windows App SDK** | W10M 装不上。只用经典 UWP XAML |
| **不要升级 `Microsoft.NETCore.UniversalWindowsPlatform`** | 停在 `5.4.7`。升级会把最低框架抬到手机上不存在的东西上 |
| **★ Newtonsoft.Json 必须停在 `10.0.3`** | 13.x 只提供 netstandard2.0 资产，在 5.4.7 上 `JsonSerializerSettings` 一构造就抛 `FieldAccessException`，**所有 JSON 读写全废**（连 `new JsonSerializerSettings()` 都不行）。实测过 10.0.3 / 12 / 13.0.3，只有 10.0.3 会退回可用的 netstandard1.3 资产 |
| **★ 不要直接用 `JsonConvert` / `new JsonSerializerSettings()`** | 同上，它们在真机上必崩。所有 JSON 走 `JsonStore` 或 `JsonBridge`。`JObject`/`JToken`/`JsonTextReader`/`JsonTextWriter` 是好的，可以用 |
| **★ 反射要用 `System.Reflection` 扩展方法** | UWP 的 `System.Type` 上没有 `GetProperties()`/`GetProperty()`/`IsEnum`，必须用 `GetRuntimeProperties()` / `GetRuntimeProperty()` / `GetTypeInfo().IsEnum`；`GetCustomAttributes` 返回 `IEnumerable<Attribute>` 而不是数组 |
| **★ 页面里取全局样式必须走 `Application.Current.Resources`** | `Page.Resources` 索引器**不会**往上找父级字典。`this.Resources["OutlineButton"]` 对 `Themes\Design.xaml` 里的样式必然抛异常。用 `MainPage` 里的 `FindStyle()` / `FindBrush()` 帮助方法，它们做了 `TryGetValue` |
| **CommandBar 主命令不会自动溢流** | 超出宽度会被直接裁切。主命令 ≤ 3 个，其余放 `SecondaryCommands` |
| **系统栏不要自己画** | 状态栏用 `StatusBar` API（`ThemeHelper.ApplyStatusBar`） |
| **软键盘** | 只能靠 UWP 默认的整页上移。**必须实测**：会话内全屏表单要自适应高度、底部按钮要固定 |
| **新增 XAML 页面必须改 csproj** | `MainPage.xaml` 之外要加 `<Page Include="...">`，否则 `InitializeComponent` 找不到（C# 文件是通配的，不用管） |
| **★ 新增"纯代码页面"必须补 `Ui\PageTypeRegistry.xaml`** | 纯 C# 构建的页面（`DetailPage` 的子类，如 `OnboardingPage`、各设置页）要往那个文件里加一行 `<DataTemplate>`。`Frame.Navigate(typeof(T))` 在 .NET Native 下要查 XAML 类型表的激活器，只有出现在 XAML 标记里的类型才有；漏了就是**真机一打开那个页面就闪退**（进程无声消失、crash.log 为空、埋点停在 Navigate 前），桌面 Debug 完全看不出来。详见 §5.1 |
| **不要在 `Ui\` 里静默吞异常** | 本轮踩过一次：快捷栏因为在页面字典里找不到样式而抛异常，调用处是 fire-and-forget，症状是"快捷栏莫名其妙不显示"。要么记 `App.WriteCrashLog`，要么给用户可见的降级 |
| **★「模型收不收图片」只能用 `ModelSelection.ResolveVision`** | 判断依据分散过一次，症状是**图片被静默丢弃**：请求声明支持图片、消息里却没有图，模型回答"我没收到图片"，界面上不报错也没有日志。优先级：模型元数据 > 档案级开关 > Provider 级开关 |
| **★ `PromptContext.Api` 要传 `ModelSelection.Resolve` 的结果** | 别传 `_workspace.ApiProfiles.Find(...)`。那是存储里的原始 Provider 档案，模型名可能不是会话正在用的那个，视觉能力也可能是旧的。`ChatService.BuildPromptContextAsync` 的 `resolvedApi` 参数就是为此存在 |
| **★ 时间线里"定稿消息"同样要收过程步骤** | `TimelineProjector.Project` 的 `IsFinal` 分支必须先把思考/工具收进轨道再 `flushProcess()`。这里曾经直接 flush + `continue`，导致**定稿后思考整段消失**：流式生成时看得到"思考"，回复一结束或重新打开会话就再也找不到。改动这段要跑 `时间线` 那组回归 |
| **★ 过程行刷新只能用 `RowVm.RaiseProcessChanged()`** | **不要**对过程行调 `RaiseChanged()`（`PropertyChanged(null)` = "全部属性已变"）。过程行模板里有嵌套的 `ItemsControl`，绑定引擎重读整行时会抛 NRE（`0x80004003`、堆栈为空、连 `DebugSettings.BindingFailed` 都收不到），异常直接穿透 Click 处理器。症状有两个：**点过程轨道展开没反应**，以及**工具步骤界面一直不更新**（`StepUpdated` 也走这条通知）。改这里要在桌面实测点击展开 |
| **流式思考没有实时进轨道** | 纯对话（不开工具）时 `AssistantDelta` 的 `reasoning` 参数目前**被忽略**，思考只在定稿后随过程行出现。想让用户边生成边看思考，要在 `AppendReasoningDelta` 里维护一个 live `StepRecord` 并接进 `_liveSteps` |

---

## 5.1 真机"闪退且无日志"的排查套路

`.NET Native` 的 fail-fast 不抛托管异常，`App.UnhandledException` / `crash.log` 都拿不到东西。
2026-10-15 定位"全新安装启动即退出"用的办法（当时真机工作区为空 → 启动必进引导页 → 必崩）：

1. **把时间夹出来**：往 `Platform\StartupTrace` 里加细粒度 `Mark`，再用设备门户的
   `/api/resourcemanager/processes` 每 2 秒探一次活。**最后一个 Mark + 存活时长**就能把死点夹到某一行。
   注意加一个 `DispatcherTimer` 心跳：真机 UI 线程一卡死，心跳就停，能和"崩了"区分开。
2. **桌面复现**：把桌面 `LocalState\workspace` 临时改名（先备份！）造出同样的"空工作区"状态。
   桌面 Debug 不裁剪元数据，往往能直接给出真实堆栈；**但如果桌面 Debug 也正常，问题就在 .NET Native 的元数据上**。
3. **对照生成物**：`obj\<arch>\Release\XamlTypeInfo.g.cs` 是 XAML 类型表。
   查 `Activate_\d+_<页面名>` 在不在 —— 不在就说明 `Frame.Navigate` 找不到激活器，
   要往 `Ui\PageTypeRegistry.xaml` 补一行。

设备门户排障入口：`\LocalState\startup-trace.log`（埋点）、`crash.log`、`workspace\diagnostics.log`；
读文件用 `tools\Read-PhoneDiagnostics.ps1`。

---

## 6. 构建与验证

```powershell
$msbuild='C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'

# 桌面 x86 Debug（日常开发看界面）
& $msbuild src\Yanshuai.Messages\Yanshuai.Messages.csproj /t:Build `
  /p:Configuration=Debug /p:Platform=x86 /v:minimal /nologo

# 手机 ARM Release（.NET Native，最终目标）
& $msbuild src\Yanshuai.Messages\Yanshuai.Messages.csproj /t:Build `
  /p:Configuration=Release /p:Platform=ARM /v:minimal /nologo

# 逻辑回归（改了 Core 才需要跑；改 UI 不用）
cd tests\CoreTests; dotnet run -c Debug
```

当前体积参考：**x86 Debug 包 8.06 MB**、**ARM Release 包 8.17 MB**。
ARM 包比早期大是因为自动带上了 CPython 载荷（`python-uwp.dll` + `z.dll` + `Lib\` 622 个 .py），
不含时约 2.7 MB。载荷目录不存在时会自动跳过，构建照常通过 —— 所以如果你在别的机器上
构建 ARM 包发现只有 2.7 MB，那不是坏了，是没接 PyUwp。详见 §9.1。

### 6.1 桌面跑起来看界面（本轮新加，很好用）

```powershell
.\tools\Deploy-Desktop.ps1          # 注册 x86 Debug 包（松散文件，改完编译再跑一次即可）
.\tools\Launch-Desktop.ps1 -Wait    # 启动并等窗口出现
.\tools\Capture-AppWindow.ps1 -TitleLike '言枢' -Out shot.png   # 截图
.\tools\Click-Window.ps1 -TitleLike '言枢' -X 0.35 -Y 0.40      # 按窗口内比例点击
```

截图脚本按**窗口标题**找窗口、用 `PrintWindow` 渲染 —— UWP 的可见窗口属于
`ApplicationFrameHost`，按进程名找只会抓到别的窗口。

**改完一定要真跑一遍再截图**。本轮有两个缺陷就是"编译通过、逻辑测试通过、但界面是坏的"：
快捷栏整条不显示（资源查找抛异常被吞）、JSON 全废导致列表永远空。
桌面跑一次的代价是十几秒，比在真机上猜便宜得多。

### 6.2 造演示数据（验证界面在"有数据"时的样子）

```powershell
$ws = "$env:LOCALAPPDATA\Packages\com.yanshuai.messages_amaw0954g0pkg\LocalState\workspace"
dotnet run --project tools\SeedData -- "$ws" --fresh     # 建档案/会话/分支/群聊/长会话
dotnet run --project tools\SeedData -- "$ws" --verify    # 回读校验（排障用）
```

它走的是 Core 里同一套 `Workspace`，写出来的格式与应用自己写的完全一致。

### 6.3 真机调试的两个前提

```powershell
# 1) 回环豁免（应用里连本机代理/假服务时必须做，否则请求被 AppContainer 挡掉）
CheckNetIsolation LoopbackExempt -a '-n=com.yanshuai.messages_amaw0954g0pkg'

# 2) 签名用本机 CN=LockWizard 证书（有效期到 2029-04-18）
$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq 'CN=LockWizard' -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' sign /fd SHA256 /sha1 $cert.Thumbprint <appx路径>
```

**排障入口**：`LocalState\crash.log`（崩溃）与 `LocalState\workspace\diagnostics.log`（脱敏流水）。
两者都不含密钥与正文，可以放心贴出来。

---

## 7. 验收清单（每轮改完至少过一遍）

**屏幕**：360×640 为主，必须验证 320 宽与 430 宽

- [ ] 窄屏（320）下顶栏标题不换行、底部按钮不溢出、快捷栏能横向滚动
- [ ] 软键盘弹出：输入框可见、底部按钮不被挡、时间线不被压没
- [ ] 长中文与长英文（不换行的 URL / 路径）都不撑破布局
- [ ] 空态 / 错误态 / 忙碌态都有，且文案不同（搜索无结果 ≠ 一条会话都没有）
- [ ] 旋转（横竖屏）与挂起恢复后状态正确
- [ ] 返回键逐层正确：关弹层 → 退会话 → 收搜索 → 退出
- [ ] 深色下所有文字对比度够（`FaintBrush` 只用于装饰，不用于正文）
- [ ] 系统强调色换成红/绿后，所有强调色元素都跟随变化

---

## 8. 参考素材

| 文件 | 用途 |
| --- | --- |
| `docs\设计稿.md` | 完整设计文档：信息架构、逐页设计、数据模型 |
| `docs\CAPABILITIES.md` | 竞品对照、逐条需求落地方案、排期 |
| `design\prototype\index.html` | 15 屏高保真原型（浏览器打开），顶部还有两张规格表 |
| `design\prototype\preview.png` | 原型总览图 |
| `docs\shot-chat.png` | 会话页实拍（用户气泡 + 输出 + 输入区） |
| `docs\shot-quickbar.png` | 快捷消息栏实拍 |
| `docs\shot-branch.png` / `shot-branch-switched.png` | 分支条实拍（候选 2/2 与切换后 1/2 的对照） |
| `docs\shot-me.png` | "我的"页实拍（含新增的存储 / 备份 / 快捷消息三行） |
| `docs\shot-final.png` | 会话列表实拍（有数据的状态） |
| `docs\shot-v12.png`、`shot-home-data.png`、`shot-home-data2.png`、`shot-json-fixed.png` | 排障过程中的留档。**前两张是空白/启动画面，没有参考价值**，可以删 |
| `H:\项目\UWP\Agents Anywhere\design\mobile-v3\index.html` | 本设计的视觉来源（用户认可的那一版） |
| `...\Agents Anywhere\AgentsAnywhere.Mobile\MainPage.xaml` | 时间线与模板的成熟参照实现 |
| `...\Project-Messages\Yanshuai-AI\Lumina.Agent\Pages\Chat\MarkdownBlock.cs` | 若要更强的 Markdown 渲染（表格/代码高亮/图片），可搬运 |
| `...\Lumina.Agent\Pages\Chat\LatexRenderer.cs` | 真正的二维盒模型公式排版（1366 行），需要公式时直接搬 |

---

## 9. 还没做、需要你或后续补的

| 项 | 说明 |
| --- | --- |
| **拆 `MainPage.xaml.cs`** | 见 1.4。1970 行、24 个事件处理器，是当前最大的技术债。**建议先做这个再往上加新页面** |
| **模型选择器页** | 逻辑最全、界面最缺的一块。`ModelCatalog` 已能给分组/收藏/能力标记，直接画就行 |
| **Markdown / 公式渲染** | 现在输出是纯 `TextBlock`。设计稿要求表格 / 代码块 / 公式 |
| **浅色主题** | 令牌只做了深色（`App.xaml` 写死 `RequestedTheme="Dark"`）。要做需把 `Themes\Design.xaml` 拆成 `ThemeDictionaries`，**同一文件内用 `{ThemeResource}` 更稳** |
| **附件体验** | 现在只支持文本附件（内容拼进消息），没有缩略图与图片附件入口。`ImageCompressor` 与 `Attachment` 模型都已就绪 |
| **长按菜单** | 消息的复制 / 编辑 / 删除 / 分支都还没做（分支逻辑在 `Conversation.AddVariant` / `SelectVariant`） |
| **群聊成员编辑** | 模型与回复模式都在 Core，界面没有入口 |
| **正则转换器编辑页** | 同上 |
| **追问（ask_user）** | Core 有 `IUserInteraction.AskAsync` 与 `AskQuestion`，没挂到工具上。要做就加 `ask_user` 工具 + 一个全屏问答页 |
| **记忆抽取的可见性** | 抽取在后台跑（每 N 轮一次），用户看不到"刚记住了什么"。建议给一条轻提示 |
| **向量检索的可见性** | 嵌入失败会静默退到 BM25。设置页应显示"当前是向量还是关键词检索" |
| **手机部署脚本** | `Deploy-Phone.ps1` / `Launch-Phone.ps1` 还没写。可参照 `H:\项目\UWP\Agents Anywhere\scripts\` 里的五个脚本改写（它们处理了绕系统代理、curl multipart、先关进程再更新、上传 .NET Native 依赖等坑） |
| **真机验证 PyUwp** | ARM 包已带上原生载荷（见下），但**没有在真机上跑过**。第一次上真机时请重点验证 `run_python` 与 `os.chdir` 工作目录 |
| **真机验证 JsonBridge** | 自研 JSON 映射走反射，`.NET Native` 下需要 rd.xml 覆盖到所有模型类型。已逐个声明，但同样没在设备上跑过。**这是上真机的第一优先检查项**（症状：列表又变空、`diagnostics.log` 出现"解析失败"） |

### 9.1 关于 PyUwp（`run_python` 的真运行时）

ARM 包会自动带上 `python-uwp.dll` / `z.dll` / `Lib\`（622 个 .py），前提是
`..\..\..\Python UWP\_extracted\runtimes\win10-arm\native` 存在。目录不存在时开关自动关闭，
构建照常通过，应用里 `run_python` 会如实报告"运行时不可用"（**不会静默失败**）。

**已知缺失**（决定了 pip 要留到下个版本）：`_socket` / `_ssl` / `_hashlib` / `zipfile` / `_sqlite3`
都还没编进去，所以脚本里不能做网络请求、不能解压、不能用数据库。计算、文本处理、
标准库的纯 Python 模块（json / re / math / datetime 等）都可以用。

---

## 10. 明确禁止

1. **不要把业务逻辑写进 `Ui\`**。旧项目把 20 个分部文件糊在 `MainPage` 上，任何视觉改动都可能碰坏业务。要新数据就扩 `ChatService`。
   ⚠️ 这条我自己在本轮破坏了（见 1.4）—— 请优先把它拆回去，别在 1970 行上继续加。
2. **不要为了好看去改 `Core\` 里的算法**（时间线投影规则、压缩阈值、记忆打分、权限白名单）。要改先说清理由，并同步更新 `tests\CoreTests`。
3. **不要引入新的第三方 UI 库 / 字体包**。包体积对 W10M 很敏感；系统字体优先是刻意取舍。
4. **不要在页面里硬编码颜色、尺寸、间距**。全部走 `Themes\Design.xaml`。
5. **不要用 `Application.Resources` 的键去猜**。旧项目引用了不存在的资源键，在别的主题下运行期直接崩，且只在真机上发现。所有键都在 `Design.xaml` 里，用之前先确认存在。
