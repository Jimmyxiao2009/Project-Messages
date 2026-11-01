// Core/Permissions/PermissionService.cs —— 敏感操作审批
//
// 策略只有一条：**白名单里的工具需要批准，其余直接放行。**
// 不引入权限层级 —— 手机上多一层权限模型只会让用户困惑，而实际需要挡住的就那几个动作。
//
// 与旧项目的三点差异：
//   1) 审批记录是真 JSON（旧项目是每行一个 key 的纯文本，没法带时间与来源，也没有 UI 入口）。
//   2) 审批请求串行化：同一时刻只弹一个框，避免连续工具各弹一个把用户按在屏幕上。
//   3) FullTrust 每次进入会话由调用方复位，不持久化"永不再问"。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public static class SensitiveTools
    {
        /// <summary>
        /// 需要用户批准的工具。判定标准是"会改变设备状态"或"会把本地内容发出去"：
        ///   write_file     改文件
        ///   calendar_create 写日程
        ///   make_call      拨号
        ///   send_sms       发短信
        ///   read_image     把本地图片内容发给模型（外传）
        ///   run_python     执行任意代码
        /// </summary>
        public static readonly string[] Names = new string[]
        {
            "write_file",
            "calendar_create",
            "make_call",
            "send_sms",
            "read_image",
            "run_python",
            // Chronos 模块是设备上执行的**原生代码**，与 run_python 同级
            "run_module"
        };

        public static bool IsSensitive(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            foreach (var n in Names)
            {
                if (string.Equals(n, toolName, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 对话页快捷权限的三档。粒度按"会不会改变/外发东西"划，不引入权限层级树：
    ///   仅可查看    —— 读与检索放行；写文件、拨号、短信、日程、读图外传、执行脚本一律拒绝；
    ///   工作区内修改 —— 档案沙箱内的文件写入免审批（沙箱由工具层强制）；
    ///                  拨号/短信/日程/读图外传/执行脚本仍逐次批准；
    ///   完全访问    —— 全部放行，不再弹审批（等价于旧的 FullTrust，不持久化）。
    /// </summary>
    public enum ToolPermissionLevel
    {
        ViewOnly = 0,
        WorkspaceWrite = 1,
        FullAccess = 2
    }

    public sealed class PermissionService
    {
        private readonly IFileStore _store;
        private readonly ILogger _log;
        private readonly SemaphoreSlim _promptLock = new SemaphoreSlim(1, 1);
        private PermissionFile _cache;

        public PermissionService(IFileStore store, ILogger log)
        {
            _store = store;
            _log = log;
        }

        /// <summary>
        /// 本次会话的权限档位 —— 对话页快捷权限设置改的就是它。
        /// 不落盘：每次进入会话由 ChatService 按档案复位，与旧的 FullTrust 语义一致。
        /// </summary>
        public ToolPermissionLevel Level { get; set; } = ToolPermissionLevel.WorkspaceWrite;

        /// <summary>兼容旧调用点：完全访问 == 旧的 FullTrust。</summary>
        public bool FullTrust
        {
            get { return Level == ToolPermissionLevel.FullAccess; }
            set { Level = value ? ToolPermissionLevel.FullAccess : ToolPermissionLevel.WorkspaceWrite; }
        }

        public async Task LoadAsync()
        {
            var file = await JsonStore.TryReadAsync<PermissionFile>(_store, WorkspaceLayout.SharedPermissions, _log)
                .ConfigureAwait(false);
            _cache = file ?? new PermissionFile();
        }

        public IReadOnlyList<string> Granted
        {
            get
            {
                var file = _cache ?? new PermissionFile();
                return file.Granted.AsReadOnly();
            }
        }

        public bool IsGranted(string key)
        {
            return _cache != null && _cache.Has(key);
        }

        public async Task GrantAsync(string key)
        {
            if (_cache == null) _cache = new PermissionFile();
            _cache.Grant(key);
            await PersistAsync().ConfigureAwait(false);
        }

        public async Task RevokeAsync(string key)
        {
            if (_cache == null) return;
            _cache.Revoke(key);
            await PersistAsync().ConfigureAwait(false);
        }

        public async Task RevokeAllAsync()
        {
            _cache = new PermissionFile();
            await PersistAsync().ConfigureAwait(false);
        }

        private Task PersistAsync()
        {
            var json = JsonStore.Serialize(_cache);
            return AtomicFile.WriteAsync(_store, WorkspaceLayout.SharedPermissions, json);
        }

        /// <summary>
        /// 判定一次工具调用是否放行。返回 null 表示可以执行；否则返回给模型看的拒绝说明。
        /// </summary>
        public async Task<string> CheckAsync(
            string toolName, string permissionKey, ToolPermissionRequest request, IUserInteraction ui, CancellationToken ct)
        {
            // 完全访问：全部放行（等价于旧的 FullTrust）
            if (Level == ToolPermissionLevel.FullAccess) return null;
            // 非敏感工具（读、检索、搜索）在任何档位都放行
            if (!SensitiveTools.IsSensitive(toolName)) return null;

            if (IsGranted(permissionKey)) return null;

            // 仅可查看：写入、执行、外发一律拒绝，并告诉用户去哪儿改
            if (Level == ToolPermissionLevel.ViewOnly)
            {
                return "用户拒绝了此操作：当前会话权限是「仅可查看」。需要写入或执行时，请在对话页菜单的权限设置里调整。";
            }

            // 工作区内修改：档案沙箱内的文件写入不再逐次问。
            // 能写到哪里由 PathSandbox 和授权目录强制，与档位无关，这里只放行"不再问"。
            if (string.Equals(toolName, "write_file", StringComparison.Ordinal)) return null;

            if (ui == null) return "用户拒绝了此操作：当前没有可用的审批界面。";

            await _promptLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 等锁期间可能已经被批准过（用户点了"始终允许"）
                if (IsGranted(permissionKey)) return null;

                bool allowed;
                try
                {
                    allowed = await ui.ConfirmPermissionAsync(request, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 必须带异常类型与消息：真机上出现过"审批界面出错"，但旧实现只记了
                    // 工具名，没有任何线索能定位是弹窗本身炸了还是别的问题。
                    if (_log != null) _log.Log(LogLevel.Warning, "perm",
                        "审批界面异常: " + toolName + " | " + ex.GetType().FullName + ": " + ex.Message);
                    return "用户拒绝了此操作：审批界面出错。";
                }

                if (!allowed) return "用户拒绝了此操作。请换一种方式，或者先向用户说明你为什么需要它。";

                // 授权记录的落盘放在审批 try 之外：写盘失败是"记住授权"失败，
                // 不该被外面那个 catch 翻译成"审批界面出错"（真机上就这么误导过一次）。
                try
                {
                    await GrantAsync(permissionKey).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_log != null) _log.Log(LogLevel.Warning, "perm", "授权记录写入失败: " + ex.GetType().Name);
                }
                return null;
            }
            finally
            {
                _promptLock.Release();
            }
        }

        /// <summary>
        /// 生成权限键。形如 write_file:notes.md —— 同一文件再次写入不再问，
        /// 但换成另一个文件还是要问。粒度比"整个工具永久放行"细，比"每次都问"省事。
        /// </summary>
        public static string BuildKey(string toolName, string argumentJson)
        {
            string detail = "";

            switch (toolName)
            {
                case "write_file":
                    detail = ToolArguments.GetString(argumentJson, "path",
                             ToolArguments.GetString(argumentJson, "file", ""));
                    break;
                case "calendar_create":
                    detail = ToolArguments.GetString(argumentJson, "title", "");
                    break;
                case "make_call":
                case "send_sms":
                    detail = ToolArguments.GetString(argumentJson, "number",
                             ToolArguments.GetString(argumentJson, "to", ""));
                    break;
                case "read_image":
                    detail = ToolArguments.GetString(argumentJson, "path", "");
                    break;
            }

            detail = TextUtil.Truncate(TextUtil.Normalize(detail), 60);
            return string.IsNullOrEmpty(detail) ? toolName : toolName + ":" + detail;
        }

        /// <summary>构造给用户看的审批说明。写在这里，各工具的 Invoke 不用各写一份。</summary>
        public static ToolPermissionRequest BuildRequest(string toolName, string permissionKey, string argumentJson)
        {
            var request = new ToolPermissionRequest
            {
                ToolName = toolName,
                PermissionKey = permissionKey
            };

            switch (toolName)
            {
                case "write_file":
                    request.Title = "允许写入文件";
                    request.Detail = "这会修改你档案目录里的文件。原内容会先备份一份。";
                    request.Fields.Add(new KeyValuePair<string, string>("文件",
                        ToolArguments.GetString(argumentJson, "path", ToolArguments.GetString(argumentJson, "file", "(未指定)"))));
                    break;

                case "calendar_create":
                    request.Title = "允许新建日程";
                    request.Fields.Add(new KeyValuePair<string, string>("标题", ToolArguments.GetString(argumentJson, "title", "")));
                    request.Fields.Add(new KeyValuePair<string, string>("开始", ToolArguments.GetString(argumentJson, "start", "")));
                    request.Fields.Add(new KeyValuePair<string, string>("时长", ToolArguments.GetInt(argumentJson, "minutes", 60) + " 分钟"));
                    break;

                case "make_call":
                    request.Title = "允许拨打电话";
                    request.Detail = "会打开系统电话应用并填入号码。";
                    request.Fields.Add(new KeyValuePair<string, string>("号码",
                        ToolArguments.GetString(argumentJson, "number", ToolArguments.GetString(argumentJson, "to", ""))));
                    break;

                case "send_sms":
                    request.Title = "允许发送短信";
                    request.Detail = "会打开系统短信应用并填入内容。";
                    request.Fields.Add(new KeyValuePair<string, string>("号码",
                        ToolArguments.GetString(argumentJson, "number", ToolArguments.GetString(argumentJson, "to", ""))));
                    request.Fields.Add(new KeyValuePair<string, string>("内容",
                        TextUtil.Truncate(ToolArguments.GetString(argumentJson, "message", ""), 120)));
                    break;

                case "read_image":
                    request.Title = "允许读取图片";
                    request.Detail = "图片内容会被发送给模型服务，请确认这个服务可信任。";
                    request.Fields.Add(new KeyValuePair<string, string>("路径",
                        ToolArguments.GetString(argumentJson, "path", "")));
                    break;

                default:
                    request.Title = "允许执行 " + toolName;
                    break;
            }

            return request;
        }
    }
}
