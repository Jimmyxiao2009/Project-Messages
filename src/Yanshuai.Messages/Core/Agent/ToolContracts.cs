// Core/Agent/ToolContracts.cs —— 工具契约与注册表
//
// 工具是"无状态 + 一个 Context 参数"，所有依赖（存储、网络、设备、记忆）
// 都从 ToolContext 拿。旧项目的工具是 FunctionCallEngine 这个跨 4 个文件的
// static partial 类，还直接 new HttpClient、直接碰 CoreApplication.Dispatcher，
// 结果既没法单测也没法换宿主。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Yanshuai.Messages.Core.Packages;

namespace Yanshuai.Messages.Core
{
    public sealed class ToolResult
    {
        public bool Ok { get; set; }
        /// <summary>回给模型的内容。失败时是一句可读的说明，让模型自己纠偏。</summary>
        public string Content { get; set; } = "";
        public string ErrorCode { get; set; } = "";
        /// <summary>给 UI 的短摘要（例如 "+18 −6"），为空时 UI 自己截取。</summary>
        public string Summary { get; set; } = "";

        public static ToolResult Success(string content)
        {
            return new ToolResult { Ok = true, Content = content ?? "" };
        }

        public static ToolResult Success(string content, string summary)
        {
            return new ToolResult { Ok = true, Content = content ?? "", Summary = summary ?? "" };
        }

        public static ToolResult Failure(string message, string code)
        {
            return new ToolResult
            {
                Ok = false,
                Content = "错误：" + (message ?? "未知错误"),
                ErrorCode = code ?? ""
            };
        }

        public static ToolResult Failure(string message)
        {
            return Failure(message, "");
        }
    }

    /// <summary>工具执行现场。所有外部依赖都从这里取。</summary>
    public sealed class ToolContext
    {
        public IUserInteraction Interaction { get; set; }
        public Profile Profile { get; set; }
        public ApiProfile Api { get; set; }
        public string ProfileSlug { get; set; } = "";

        public IFileStore Store { get; set; }
        public IHttpTransport Http { get; set; }
        public ILogger Log { get; set; }
        public IClock Clock { get; set; }
        public IDeviceBridge Device { get; set; }
        public IEmbedder Embedder { get; set; }

        public SearchSettings Search { get; set; }
        public List<McpServerConfig> McpServers { get; set; }
        public List<SkillDefinition> Skills { get; set; }

        /// <summary>记忆检索入口（给 memory_search 工具用）。</summary>
        public MemoryService Memory { get; set; }

        /// <summary>子代理执行入口，由 AgentRunner 注入，避免工具反向依赖 runner。</summary>
        public Func<string, CancellationToken, Task<string>> RunSubagentAsync { get; set; }

        /// <summary>脚本运行时（桌面是桩，真机是 PyUwp）。为空则 run_python 不可用。</summary>
        public IScriptRuntime ScriptRuntime { get; set; }

        /// <summary>脚本可调用的宿主能力。</summary>
        public IScriptHostBridge ScriptHost { get; set; }

        /// <summary>档案是否允许执行脚本（设置里的开关，默认关）。</summary>
        public bool ScriptEnabled { get; set; }

        public ToolContext()
        {
            McpServers = new List<McpServerConfig>();
            Skills = new List<SkillDefinition>();
        }

        /// <summary>档案的私有文件根目录（工具只能在这里读写）。</summary>
        public string FilesRoot
        {
            get { return WorkspaceLayout.FilesDir(ProfileSlug); }
        }
    }

    public interface ITool
    {
        string Name { get; }
        string Description { get; }
        ToolGroup Group { get; }
        /// <summary>敏感工具需要用户批准（见 SensitiveTools）。</summary>
        bool IsSensitive { get; }
        ToolSchema Schema();
        Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct);
    }

    /// <summary>工具基类，省掉每个工具重复实现 Schema 的样板。</summary>
    public abstract class ToolBase : ITool
    {
        private ToolSchema _schema;

        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract ToolGroup Group { get; }
        public virtual bool IsSensitive { get { return SensitiveTools.IsSensitive(Name); } }

        /// <summary>由子类填充参数定义。</summary>
        protected abstract void DescribeParams(ToolSchema schema);

        public ToolSchema Schema()
        {
            if (_schema == null)
            {
                var schema = new ToolSchema { Name = Name, Description = Description };
                DescribeParams(schema);
                _schema = schema;
            }
            return _schema;
        }

        public abstract Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct);
    }

    public sealed class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);

        public void Register(ITool tool)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Name)) return;
            _tools[tool.Name] = tool;
        }

        public ITool Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ITool tool;
            return _tools.TryGetValue(name, out tool) ? tool : null;
        }

        public IReadOnlyCollection<ITool> All
        {
            get { return _tools.Values; }
        }

        /// <summary>
        /// 按档案的工具开关过滤出本次可用的工具。
        /// ToolsEnabled 为假 → 一个都不用（走纯流式对话）；
        /// Enabled 列表为空 → 用全部默认工具（这是新建档案的友好默认）；
        /// run_python 额外受 ScriptEnabled 门控 —— 即使被显式列进 Enabled 也不放行，
        /// 免得"打开脚本开关"这个动作被绕过。
        /// </summary>
        /// <summary>
        /// 取本次可用的工具。**全部常开且不可逐项修改** —— 能不能真执行由
        /// 对话页的三档权限（PermissionService.Level）在执行时把关：
        /// 仅可查看 / 工作区内修改 / 完全访问。
        /// 早期版本在这里按档案开关过滤，结果是"工具莫名全灭"最常见的根因，
        /// 且用户根本找不到开关，已移除。
        /// </summary>
        public List<ITool> Resolve(Profile profile)
        {
            var result = new List<ITool>(_tools.Values);
            result.Sort(delegate (ITool a, ITool b)
            {
                return string.CompareOrdinal(a.Name, b.Name);
            });
            return result;
        }
        public List<ToolSchema> Schemas(IEnumerable<ITool> tools)
        {
            var list = new List<ToolSchema>();
            if (tools == null) return list;
            foreach (var tool in tools) list.Add(tool.Schema());
            return list;
        }

        /// <summary>注册全部内置工具。</summary>
        public static ToolRegistry CreateDefault()
        {
            var registry = new ToolRegistry();
            registry.Register(new AskUserTool());
            registry.Register(new CalendarCreateTool());
            registry.Register(new TimeTool());
            registry.Register(new FileListTool());
            registry.Register(new FileReadTool());
            registry.Register(new FileWriteTool());
            registry.Register(new WebSearchTool());
            registry.Register(new FetchPageTool());
            registry.Register(new MemorySearchTool());
            registry.Register(new SubagentTool());
            registry.Register(new McpListTool());
            registry.Register(new McpTool());
            registry.Register(new SkillListTool());
            registry.Register(new SkillTool());
            registry.Register(new CalendarTool());
            registry.Register(new ContactsTool());
            registry.Register(new CallTool());
            registry.Register(new SmsTool());
            registry.Register(new OpenAppTool());
            registry.Register(new MediaTool());
            registry.Register(new ReadImageTool());
            registry.Register(new RunPythonTool());

            // Chronos 本地包（pip 风格：list / run / install / uninstall）
            registry.Register(new PackageListTool());
            registry.Register(new RunModuleTool());
            registry.Register(new PackageInstallTool());
            registry.Register(new PackageUninstallTool());
            return registry;
        }
    }
}
