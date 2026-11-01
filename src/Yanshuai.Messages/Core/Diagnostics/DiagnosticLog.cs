// Core/Diagnostics/DiagnosticLog.cs —— 脱敏诊断日志
//
// 规则（在手机上排查问题全靠它，所以既要够用又不能泄密）：
//   记录：UTC 时间、级别、分类、消息
//   不记录：API Key、Token、邮箱、密码、请求/响应正文、HTTP 头、URL 查询参数、异常 message
//
// 实现上不靠"调用方自觉"：日志对象自己过一遍清洗（把看起来像密钥的串换成 ***），
// 再截断超长行。文件按大小轮换，最多保留两份。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class DiagnosticLog : ILogger
    {
        public const string FilePath = "diagnostics.log";
        public const string RotatedPath = "diagnostics.1.log";

        private const int MaxFileChars = 256 * 1024;      // 约 256KB 轮换
        private const int MaxLineChars = 1000;
        private const int MaxMemoryEntries = 400;

        private readonly IFileStore _store;
        private readonly IClock _clock;
        private readonly List<string> _recent = new List<string>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();
        private LogLevel _minLevel = LogLevel.Info;
        private bool _enabled = true;

        public DiagnosticLog(IFileStore store, IClock clock)
        {
            _store = store;
            _clock = clock ?? SystemClock.Instance;
        }

        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        public LogLevel MinLevel
        {
            get { return _minLevel; }
            set { _minLevel = value; }
        }

        // 常见密钥形态：sk-xxxx、Bearer 后面一长串、32 位以上十六进制
        private static readonly Regex[] SecretPatterns = new Regex[]
        {
            new Regex(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
            new Regex(@"(?i)bearer\s+[A-Za-z0-9_\-\.]{8,}", RegexOptions.Compiled),
            new Regex(@"\b[A-Fa-f0-9]{32,}\b", RegexOptions.Compiled),
            new Regex(@"[A-Za-z0-9_\-\.]{40,}", RegexOptions.Compiled)
        };

        private static readonly Regex QueryString = new Regex(@"\?[^\s]{1,}", RegexOptions.Compiled);

        public void Log(LogLevel level, string category, string message)
        {
            if (!_enabled) return;
            if (level < _minLevel) return;

            var line = Format(level, category, message);

            lock (_gate)
            {
                _recent.Add(line);
                if (_recent.Count > MaxMemoryEntries) _recent.RemoveAt(0);
            }

            var ignored = AppendAsync(line);
        }

        private string Format(LogLevel level, string category, string message)
        {
            var sb = new StringBuilder();
            sb.Append(_clock.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append(" [").Append(LevelName(level)).Append(']');
            if (!string.IsNullOrEmpty(category)) sb.Append(" [").Append(category).Append(']');
            sb.Append(' ').Append(Sanitize(message));
            return TextUtil.Truncate(sb.ToString(), MaxLineChars);
        }

        private static string LevelName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DBG";
                case LogLevel.Warning: return "WRN";
                case LogLevel.Error: return "ERR";
                default: return "INF";
            }
        }

        /// <summary>把可疑的密钥与查询串洗掉。宁可多洗，也不要把密钥写进磁盘。</summary>
        public static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var result = text.Replace('\n', ' ').Replace('\r', ' ');
            foreach (var pattern in SecretPatterns)
            {
                result = pattern.Replace(result, "***");
            }
            result = QueryString.Replace(result, "?***");
            return result;
        }

        private async Task AppendAsync(string line)
        {
            if (_store == null) return;

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                string existing = null;
                try
                {
                    if (await _store.ExistsAsync(FilePath).ConfigureAwait(false))
                    {
                        existing = await _store.ReadTextAsync(FilePath).ConfigureAwait(false);
                    }
                }
                catch (Exception) { }

                if (existing != null && existing.Length > MaxFileChars)
                {
                    try
                    {
                        await AtomicFile.WriteAsync(_store, RotatedPath, existing).ConfigureAwait(false);
                    }
                    catch (Exception) { }
                    existing = "";
                }

                var builder = new StringBuilder(existing ?? "");
                builder.Append(line).Append('\n');

                await AtomicFile.WriteAsync(_store, FilePath, builder.ToString()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 日志写失败绝不能影响主流程
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>读最近若干行（设置页的"查看调试日志"用）。</summary>
        public async Task<string> ReadTailAsync(int maxLines)
        {
            if (maxLines <= 0) maxLines = 200;

            string content = null;
            try
            {
                if (await _store.ExistsAsync(FilePath).ConfigureAwait(false))
                {
                    content = await _store.ReadTextAsync(FilePath).ConfigureAwait(false);
                }
            }
            catch (Exception) { }

            if (string.IsNullOrEmpty(content))
            {
                lock (_gate)
                {
                    if (_recent.Count == 0) return "（暂无日志）";
                    var start = Math.Max(0, _recent.Count - maxLines);
                    var sb = new StringBuilder();
                    for (int i = start; i < _recent.Count; i++) sb.Append(_recent[i]).Append('\n');
                    return sb.ToString().TrimEnd();
                }
            }

            var lines = content.Split('\n');
            var from = Math.Max(0, lines.Length - maxLines);
            var result = new StringBuilder();
            for (int i = from; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                result.Append(lines[i]).Append('\n');
            }
            return result.ToString().TrimEnd();
        }

        public async Task ClearAsync()
        {
            lock (_gate) _recent.Clear();
            try
            {
                await _store.DeleteAsync(FilePath).ConfigureAwait(false);
                await _store.DeleteAsync(RotatedPath).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }
}
