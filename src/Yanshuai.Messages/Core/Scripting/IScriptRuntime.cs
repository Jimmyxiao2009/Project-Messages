// Core/Scripting/IScriptRuntime.cs —— 脚本运行时抽象
//
// 为什么要抽象：真 Python 运行时（PyUwp）是 **ARM-only** 的原生库，
// 桌面根本加载不了。如果工具直接 P/Invoke，那"写脚本 → 执行 → 拿输出 → 回填给模型"
// 这条链路在桌面上就永远测不了，只能每次上真机试。
//
// 抽象之后：
//   · StubScriptRuntime  —— 纯托管实现，桌面与 ARM 都能跑，用来验证整条链路
//   · PyUwpScriptRuntime  —— 真机用的真运行时，只替换这一个实现
// 于是工具层、审批、输出截断、超时、错误回填全部可以在桌面上测完。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class ScriptRequest
    {
        public string Source { get; set; } = "";
        /// <summary>工作目录（档案内相对路径，受 PathSandbox 约束）。</summary>
        public string WorkingDirectory { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 30;
        public int MaxOutputChars { get; set; } = 20000;

        /// <summary>允许脚本调用的宿主能力（宿主桥）。为空表示不给任何能力。</summary>
        public List<string> AllowedHostCalls { get; set; }

        public ScriptRequest()
        {
            AllowedHostCalls = new List<string>();
        }
    }

    public sealed class ScriptResult
    {
        public bool Ok { get; set; }
        public string StdOut { get; set; } = "";
        public string StdErr { get; set; } = "";
        public int ExitCode { get; set; }
        public long DurationMs { get; set; }
        public bool TimedOut { get; set; }
        public bool OutputTruncated { get; set; }
        public string ErrorCode { get; set; } = "";

        public static ScriptResult Fail(string code, string message)
        {
            return new ScriptResult
            {
                Ok = false,
                ErrorCode = code,
                StdErr = message ?? "",
                ExitCode = 1
            };
        }

        /// <summary>给模型看的一段文本。stdout / stderr 分开标，方便模型区分"结果"与"报错"。</summary>
        public string FormatForModel()
        {
            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrEmpty(StdOut))
            {
                sb.Append(StdOut.TrimEnd());
            }

            if (!string.IsNullOrEmpty(StdErr))
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("[stderr]\n").Append(StdErr.TrimEnd());
            }

            if (sb.Length == 0) sb.Append("（脚本没有输出）");

            if (TimedOut)
            {
                sb.Append("\n\n[超时] 脚本执行超过 ")
                  .Append(DurationMs / 1000)
                  .Append(" 秒被终止。检查是否有死循环或过重的计算。");
            }

            if (OutputTruncated)
            {
                sb.Append("\n\n[输出过长已截断]");
            }

            return sb.ToString();
        }
    }

    public interface IScriptRuntime
    {
        /// <summary>实现名，用于在界面上区分真假运行时。</summary>
        string Name { get; }

        /// <summary>true 表示这是真运行时；false 表示是桩（桌面开发用）。</summary>
        bool IsReal { get; }

        bool IsAvailable { get; }

        /// <summary>运行时版本描述，给界面显示。</summary>
        string Version { get; }

        Task<ScriptResult> RunAsync(ScriptRequest request, CancellationToken ct);
    }

    /// <summary>宿主桥：脚本可以调用的能力，由宿主实现。</summary>
    public interface IScriptHostBridge
    {
        /// <summary>可用能力清单（脚本侧据此判断能调什么）。</summary>
        IReadOnlyList<string> AvailableCalls { get; }

        /// <summary>执行一次宿主调用。返回结果文本。</summary>
        Task<string> CallAsync(string call, string argumentsJson, CancellationToken ct);
    }

    /// <summary>没有宿主桥时的实现：任何调用都返回"未开放"。</summary>
    public sealed class NoScriptHostBridge : IScriptHostBridge
    {
        public static readonly NoScriptHostBridge Instance = new NoScriptHostBridge();

        public IReadOnlyList<string> AvailableCalls { get { return new string[0]; } }

        public Task<string> CallAsync(string call, string argumentsJson, CancellationToken ct)
        {
            return Task.FromResult("错误：宿主没有开放任何能力。");
        }
    }
}
