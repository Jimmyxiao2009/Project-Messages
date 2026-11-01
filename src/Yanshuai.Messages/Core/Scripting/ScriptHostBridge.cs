// Core/Scripting/ScriptHostBridge.cs —— 宿主桥的默认实现
//
// 脚本能调的能力刻意限制在**只读**范围：查记忆、取时间。
// 设备类能力（发短信、拨号、写文件）不开放给脚本 ——
// 脚本是被模型生成的，等于把"模型能做的事"再放大一圈；
// 放大之前先确保放大出来的部分碰不到设备状态。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class ScriptHostBridge : IScriptHostBridge
    {
        private readonly MemoryService _memory;
        private readonly IClock _clock;

        private readonly List<string> _calls = new List<string>();

        public ScriptHostBridge(MemoryService memory, IClock clock, Profile profile)
        {
            _memory = memory;
            _clock = clock;

            if (_memory != null) _calls.Add("memory.search");
            if (_clock != null) _calls.Add("time.now");
        }

        public IReadOnlyList<string> AvailableCalls { get { return _calls; } }

        public async Task<string> CallAsync(string call, string argumentsJson, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(call)) return "错误：没有指定能力名。";

            switch (call)
            {
                case "time.now":
                {
                    var now = _clock != null ? _clock.UtcNow : DateTimeOffset.UtcNow;
                    return now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }

                case "memory.search":
                {
                    if (_memory == null) return "错误：当前档案没有开启记忆。";
                    var query = ToolArguments.GetString(argumentsJson, "query", "");
                    if (string.IsNullOrWhiteSpace(query)) return "错误：缺少 query 参数。";

                    var hits = await _memory.SearchAsync(query, 5, true, ct).ConfigureAwait(false);
                    if (hits == null || hits.Count == 0) return "没有找到相关记忆。";

                    var sb = new StringBuilder();
                    foreach (var hit in hits)
                    {
                        sb.Append("- ").Append(hit.Item != null ? hit.Item.Text : "").Append('\n');
                    }
                    return sb.ToString().TrimEnd();
                }

                default:
                    return "错误：不支持的能力 " + call + "。";
            }
        }
    }
}
