// Core/Text/RegexTransformer.cs —— 正则转换器
//
// 用途：在消息**发送前**或**显示前**做文本替换。典型场景：
//   · 去掉模型爱加的固定前缀/后缀
//   · 把 {{char}} 之类的残留替换成真实名字
//   · 只改显示不改发送（visualOnly）—— 例如把 markdown 标记藏起来交给渲染器
//
// 三个字段是 rikkahub 验证过的关键设计，照抄：
//   · scope：只作用于用户消息 / 只作用于助手消息 / 两者
//   · visualOnly：只影响界面显示，发给模型的内容不变
//   · 编译缓存：正则编译很贵，缓存住，并限制缓存规模防内存膨胀

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    public static class RegexScopes
    {
        public const string User = "user";
        public const string Assistant = "assistant";

        public static bool AppliesTo(RegexTransform rule, bool isUserMessage)
        {
            if (rule == null || !rule.Enabled) return false;

            var scope = (rule.Scope ?? "").Trim().ToLowerInvariant();
            if (scope == "both" || scope.Length == 0) return true;
            if (scope == User) return isUserMessage;
            if (scope == Assistant) return !isUserMessage;
            return true;
        }
    }

    public sealed class RegexTransform
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "";

        /// <summary>查找模式（.NET 正则语法）。</summary>
        [JsonProperty("find")] public string Find { get; set; } = "";

        /// <summary>替换成什么，支持 $1 $2 这样的捕获组引用。</summary>
        [JsonProperty("replace")] public string Replace { get; set; } = "";

        /// <summary>user / assistant / both</summary>
        [JsonProperty("scope")] public string Scope { get; set; } = "both";

        /// <summary>true 表示只改显示，发给模型的内容保持原样。</summary>
        [JsonProperty("visualOnly")] public bool VisualOnly { get; set; }

        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

        public RegexTransform Clone()
        {
            return new RegexTransform
            {
                Id = Id, Name = Name, Find = Find, Replace = Replace,
                Scope = Scope, VisualOnly = VisualOnly, Enabled = Enabled
            };
        }
    }

    /// <summary>
    /// 转换器引擎。无状态、可复用；正则编译带缓存。
    /// 任何一条规则写错正则都不能让整轮对话失败 —— 出错就跳过这条规则。
    /// </summary>
    public sealed class RegexTransformer
    {
        private const int MaxCacheEntries = 64;

        private readonly Dictionary<string, Regex> _cache = new Dictionary<string, Regex>(StringComparer.Ordinal);
        private readonly List<string> _cacheOrder = new List<string>();
        private readonly object _gate = new object();

        private readonly List<RegexTransform> _rules = new List<RegexTransform>();

        public void Load(IEnumerable<RegexTransform> rules)
        {
            _rules.Clear();
            if (rules != null)
            {
                foreach (var rule in rules)
                {
                    if (rule == null || !rule.Enabled) continue;
                    if (string.IsNullOrEmpty(rule.Find)) continue;
                    _rules.Add(rule);
                }
            }
        }

        public int RuleCount { get { return _rules.Count; } }

        /// <summary>是否配了"只影响显示"的规则（UI 据此决定要不要在渲染前跑一遍）。</summary>
        public bool HasVisualRules
        {
            get
            {
                foreach (var rule in _rules)
                {
                    if (rule.VisualOnly) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 应用到一条消息的文本上。
        /// visual 为 true 时只跑 visualOnly 规则；为 false 时只跑非 visualOnly 规则。
        /// </summary>
        public string Apply(string text, bool isUserMessage, bool visual)
        {
            if (string.IsNullOrEmpty(text) || _rules.Count == 0) return text ?? "";

            var result = text;
            foreach (var rule in _rules)
            {
                if (rule.VisualOnly != visual) continue;
                if (!RegexScopes.AppliesTo(rule, isUserMessage)) continue;

                var regex = GetRegex(rule.Find);
                if (regex == null) continue;      // 正则写错：跳过这条，不影响其它规则

                try
                {
                    result = regex.Replace(result, rule.Replace ?? "");
                }
                catch (Exception)
                {
                    // 替换串里的捕获组引用越界等情况，跳过
                }
            }
            return result;
        }

        /// <summary>发送前应用：非 visualOnly 的规则。</summary>
        public string ApplyOutgoing(string text, bool isUserMessage)
        {
            return Apply(text, isUserMessage, false);
        }

        /// <summary>显示前应用：visualOnly 的规则。没配就直接返回原串，省一次遍历。</summary>
        public string ApplyVisual(string text, bool isUserMessage)
        {
            if (!HasVisualRules) return text ?? "";
            return Apply(text, isUserMessage, true);
        }

        private Regex GetRegex(string pattern)
        {
            lock (_gate)
            {
                Regex cached;
                if (_cache.TryGetValue(pattern, out cached)) return cached;

                Regex compiled;
                try
                {
                    // 带超时：防止用户写出灾难性回溯的正则把 UI 卡死
                    compiled = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
                }
                catch (Exception)
                {
                    return null;
                }

                if (_cache.Count >= MaxCacheEntries)
                {
                    var oldest = _cacheOrder[0];
                    _cacheOrder.RemoveAt(0);
                    _cache.Remove(oldest);
                }

                _cache[pattern] = compiled;
                _cacheOrder.Add(pattern);
                return compiled;
            }
        }

        // ── 校验（设置页保存前用） ──────────────────────────────────────

        /// <summary>校验一条规则。返回 null 表示没问题，否则是给用户看的原因。</summary>
        public static string Validate(RegexTransform rule)
        {
            if (rule == null) return "规则为空。";
            if (string.IsNullOrWhiteSpace(rule.Find)) return "查找模式不能为空。";

            try
            {
                new Regex(rule.Find, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                return "正则写法有问题：" + ex.Message;
            }

            var scope = (rule.Scope ?? "").Trim().ToLowerInvariant();
            if (scope.Length > 0 && scope != "both" && scope != RegexScopes.User && scope != RegexScopes.Assistant)
            {
                return "作用范围只能是 user / assistant / both。";
            }

            return null;
        }
    }
}
