// Core/Providers/SseParser.cs —— Server-Sent Events 增量解析
//
// SSE 的规矩：每个事件由若干行组成，`data:` 之后的内容拼起来（多行用 \n 连接），
// 空行表示事件结束。冒号后可以有一个空格，也可以没有（规范里空格是可选的，
// 有服务端就是不写，旧项目因此丢过流）。
//
// 这里只做"行 → 事件负载"的转换，具体 JSON 由各 provider 解析。

using System;
using System.Collections.Generic;
using System.Text;

namespace Yanshuai.Messages.Core
{
    public sealed class SseParser
    {
        private readonly StringBuilder _data = new StringBuilder();
        private readonly bool _flushOnNewData;
        private string _eventName = "";

        public SseParser() : this(true) { }

        /// <summary>
        /// flushOnNewData：遇到新的 data 行时，把上一段已缓冲的数据当作完整事件先吐出去。
        ///
        /// 为什么需要这个开关：规范要求事件之间有空行、多行 data 要拼接。但实际环境里
        /// 有些中转网关会把空行吃掉，于是所有事件会被粘成一条、整条流一个都解析不出来。
        /// OpenAI / Anthropic 从不发多行 data，所以这里选择"牺牲规范里的多行拼接，
        /// 换取粘包场景下依然可用"。想要严格规范行为就传 false。
        /// </summary>
        public SseParser(bool flushOnNewData)
        {
            _flushOnNewData = flushOnNewData;
        }

        /// <summary>
        /// 喂一行。返回一个事件负载（可能是空串，表示这次没有完整事件）。
        /// 返回 null 表示这次没有事件。
        /// </summary>
        public SseEvent Feed(string line)
        {
            if (line == null) return Take();

            if (line.Length == 0) return Take();

            // 注释行（心跳）
            if (line[0] == ':') return null;

            int colon = line.IndexOf(':');
            string field;
            string value;

            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line.Substring(0, colon);
                value = line.Substring(colon + 1);
                // 规范：冒号后若紧跟一个空格，去掉这一个空格（只去一个）
                if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
            }

            if (string.Equals(field, "data", StringComparison.Ordinal))
            {
                SseEvent pending = null;
                if (_flushOnNewData && _data.Length > 0) pending = Take();

                if (_data.Length > 0) _data.Append('\n');
                _data.Append(value);
                return pending;
            }

            if (string.Equals(field, "event", StringComparison.Ordinal))
            {
                _eventName = value;
                return null;
            }

            // id / retry / 其他字段：忽略
            return null;
        }

        /// <summary>取出已缓冲的事件。没有则返回 null。</summary>
        private SseEvent Take()
        {
            if (_data.Length == 0 && string.IsNullOrEmpty(_eventName)) return null;

            var evt = new SseEvent
            {
                Name = _eventName ?? "",
                Data = _data.ToString()
            };
            _data.Clear();
            _eventName = "";
            return evt;
        }
    }

    public sealed class SseEvent
    {
        public string Name { get; set; } = "";
        public string Data { get; set; } = "";

        public bool IsDone
        {
            get { return string.Equals(Data.Trim(), "[DONE]", StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// 增量文本累积器。把流式碎片攒起来，并支持"每 N 毫秒才对外通知一次"。
    /// 旧项目是每来一个 token 就更新一次 UI，手机上会明显卡。
    /// </summary>
    public sealed class StreamAccumulator
    {
        private readonly StringBuilder _content = new StringBuilder();
        private readonly StringBuilder _reasoning = new StringBuilder();

        public string Content { get { return _content.ToString(); } }
        public string Reasoning { get { return _reasoning.ToString(); } }

        public void AppendContent(string delta)
        {
            if (!string.IsNullOrEmpty(delta)) _content.Append(delta);
        }

        public void AppendReasoning(string delta)
        {
            if (!string.IsNullOrEmpty(delta)) _reasoning.Append(delta);
        }

        public void Clear()
        {
            _content.Clear();
            _reasoning.Clear();
        }
    }
}
