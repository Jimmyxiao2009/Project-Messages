// Platform/UwpInteraction.cs —— 审批与追问的 UI 桥
//
// Core 通过 IUserInteraction 提要求，UI 把处理函数挂进来。
// 这样 Agent 循环不需要知道页面的存在，UI 也不需要知道循环的存在。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class UwpInteraction : IUserInteraction
    {
        /// <summary>请求批准一个敏感操作。返回 true 表示允许。</summary>
        public Func<ToolPermissionRequest, CancellationToken, Task<bool>> PermissionHandler { get; set; }

        /// <summary>向用户提问。返回按 Id 对应的答案。</summary>
        public Func<IReadOnlyList<AskQuestion>, CancellationToken, Task<IReadOnlyList<AskAnswer>>> AskHandler { get; set; }

        public Task<bool> ConfirmPermissionAsync(ToolPermissionRequest request, CancellationToken ct)
        {
            var handler = PermissionHandler;
            if (handler == null)
            {
                // 没有界面就一律拒绝，绝不静默放行
                return Task.FromResult(false);
            }
            return handler(request, ct);
        }

        public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
        {
            var handler = AskHandler;
            if (handler == null)
            {
                IReadOnlyList<AskAnswer> empty = new List<AskAnswer>();
                return Task.FromResult(empty);
            }
            return handler(questions, ct);
        }
    }
}
