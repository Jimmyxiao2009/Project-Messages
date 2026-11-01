// Core/Enums.cs —— 全局枚举与常量
//
// 行 / 步骤两层模型：行决定时间线里"一条东西"，步骤决定轨道上的"一个节点"。
// 这是照 Agents Anywhere 的分法（RowKind / StepKind / StepState），
// 但重新命名与收敛，去掉了它里面几个只在远端场景下才有的取值。

using System;

namespace Yanshuai.Messages.Core
{
    /// <summary>时间线行类型——决定用哪个 DataTemplate。</summary>
    public enum RowKind
    {
        User = 0,       // 用户消息（唯一走气泡的）
        Process = 1,    // 一轮的过程（思考 + 工具），内部挂若干步骤
        Output = 2,     // 一轮的最终输出
        Marker = 3,     // 居中灰字（加载更早、已压缩上下文…）
        Error = 4       // 错误
    }

    /// <summary>过程轨道上的步骤类型。</summary>
    public enum StepKind
    {
        Reasoning = 0,  // 思考
        Tool = 1,       // 工具调用
        Note = 2,       // 模型在轮中间说的话
        Approval = 3,   // 等待批准
        System = 4,     // 系统事件（压缩、裁剪）
        Error = 5,      // 出错
        More = 6        // "显示前面 N 步"折叠节点
    }

    public enum StepState
    {
        Done = 0,
        Running = 1,
        Failed = 2,
        Stopped = 3
    }

    /// <summary>一轮的运行状态。</summary>
    public enum RunState
    {
        Idle = 0,
        Thinking = 1,
        ToolRunning = 2,
        WaitingApproval = 3,
        Stopping = 4,
        Stopped = 5,
        Failed = 6,
        Done = 7
    }

    public enum MessageRole
    {
        System = 0,
        User = 1,
        Assistant = 2,
        Tool = 3
    }

    /// <summary>注入位置。顺序即装配顺序，改这里就会改提示词结构，慎重。</summary>
    public enum InjectionPosition
    {
        BeforeSystem = 0,
        AfterSystem = 1,
        TopOfChat = 2,
        BottomOfChat = 3,
        AtDepth = 4
    }

    public enum MemoryCategory
    {
        Preference = 0,
        Fact = 1,
        Experience = 2,
        Conversation = 3
    }

    public enum MemoryScope
    {
        Profile = 0,
        Global = 1
    }

    /// <summary>工具分组，只影响设置页的展示。</summary>
    public enum ToolGroup
    {
        Network = 0,
        Files = 1,
        Device = 2,
        Personal = 3,
        Agent = 4
    }

    public static class Limits
    {
        /// <summary>工具循环最大轮次（可按档案覆盖）。</summary>
        public const int DefaultMaxToolTurns = 15;

        /// <summary>子代理最大嵌套深度。</summary>
        public const int MaxSubagentDepth = 5;

        /// <summary>一轮内工具结果的裁剪：保留最近 N 条完整。</summary>
        public const int KeepFullToolResults = 6;

        /// <summary>被裁剪的旧工具结果保留多少字符。</summary>
        public const int TrimmedToolResultChars = 500;

        /// <summary>单次读文件上限（字符）。</summary>
        public const int ReadFileChars = 8000;

        /// <summary>本地文本文件读取上限（字节）。</summary>
        public const long MaxTextFileBytes = 4L * 1024 * 1024;

        /// <summary>单个附件上限（字节）。</summary>
        public const long MaxAttachmentBytes = 25L * 1024 * 1024;

        /// <summary>单次附件数量上限。</summary>
        public const int MaxAttachments = 10;

        /// <summary>角色卡 PNG 上限（字节），防超大图注入。</summary>
        public const long MaxCardPngBytes = 5L * 1024 * 1024;

        /// <summary>嵌入向量库条目上限。</summary>
        public const int MaxMemoryItems = 10000;

        /// <summary>满载时淘汰到的目标条数。</summary>
        public const int MemoryTrimTarget = 8000;

        /// <summary>思考步骤在运行中最多露出的条数，超出折叠成 More 节点。</summary>
        public const int RunningVisibleSteps = 5;

        /// <summary>上下文压缩触发阈值（占模型窗口比例）。</summary>
        public const double CompactThreshold = 0.80;

        /// <summary>压缩时保留最近多少条消息不动。</summary>
        public const int CompactKeepRecent = 4;

        /// <summary>少于这么多条就不值得压缩。</summary>
        public const int CompactMinMessages = 6;

        /// <summary>工具输出超过这个字符数就截断。</summary>
        public const int ToolOutputChars = 16000;
    }
}
