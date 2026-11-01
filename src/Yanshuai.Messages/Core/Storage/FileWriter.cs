// Core/Storage/FileWriter.cs —— 原子写 + 保存合并
//
// 解决旧项目两个真实故障：
//   1) 直接覆盖写：写到一半断电/挂起 → 文件半截 → 下次启动整个数据解析失败。
//      这里先写 .tmp 再 Move 覆盖，任何时刻磁盘上至少有一份完整文件。
//   2) 并发保存：UI 事件密集触发保存，同一个文件被两个任务同时写。
//      这里按路径合并（同一路径窗口期内只落盘最后一次），并用一把写锁串行化。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public static class AtomicFile
    {
        public const string TempSuffix = ".tmp";

        /// <summary>先写临时文件再原子替换。失败时不留下半截目标文件。</summary>
        public static async Task WriteAsync(IFileStore store, string relativePath, string content)
        {
            var tmp = relativePath + TempSuffix;
            await store.WriteTextAsync(tmp, content);
            await store.MoveAsync(tmp, relativePath);
        }

        /// <summary>
        /// 二进制的原子写。备份/恢复必须走这条 ——
        /// 图片、角色卡原图这类内容是二进制，用 UTF-8 转字符串再写会把它们破坏掉。
        /// </summary>
        public static async Task WriteBytesAsync(IFileStore store, string relativePath, byte[] data)
        {
            var tmp = relativePath + TempSuffix;
            await store.WriteBytesAsync(tmp, data);
            await store.MoveAsync(tmp, relativePath);
        }
    }

    /// <summary>
    /// 按路径合并的保存调度器。
    /// 调用方只管 Schedule(path, () => 序列化结果)，密集触发只会落盘最后一次。
    /// </summary>
    public sealed class SaveScheduler : IDisposable
    {
        private sealed class Entry
        {
            public Func<string> Serialize;
            public CancellationTokenSource Cts;
        }

        private readonly IFileStore _store;
        private readonly ILogger _log;
        private readonly int _debounceMs;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _pending =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public SaveScheduler(IFileStore store, ILogger log, int debounceMs)
        {
            _store = store;
            _log = log;
            _debounceMs = debounceMs < 0 ? 0 : debounceMs;
        }

        /// <summary>登记一次保存。同一路径在 debounce 窗口内重复登记会被合并。</summary>
        public void Schedule(string relativePath, Func<string> serialize)
        {
            if (string.IsNullOrEmpty(relativePath) || serialize == null) return;

            CancellationToken token;
            lock (_gate)
            {
                if (_disposed) return;

                Entry entry;
                if (_pending.TryGetValue(relativePath, out entry))
                {
                    entry.Serialize = serialize;
                    entry.Cts.Cancel();          // 重置倒计时
                    entry.Cts.Dispose();
                    entry.Cts = new CancellationTokenSource();
                }
                else
                {
                    entry = new Entry { Serialize = serialize, Cts = new CancellationTokenSource() };
                    _pending[relativePath] = entry;
                }
                token = entry.Cts.Token;
            }

            if (_debounceMs == 0)
            {
                // 无延迟：直接异步落盘，但仍走同一把写锁与合并逻辑
                var ignored = DrainAsync(relativePath);
                return;
            }

            var ignoredToo = Task.Run(async delegate
            {
                try
                {
                    await Task.Delay(_debounceMs, token).ConfigureAwait(false);
                    await DrainAsync(relativePath).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
        }

        /// <summary>立刻落盘，不等 debounce（用于"用户点了保存"这种确定性动作）。</summary>
        public async Task SaveNowAsync(string relativePath, Func<string> serialize)
        {
            if (string.IsNullOrEmpty(relativePath)) return;

            lock (_gate)
            {
                Entry entry;
                if (_pending.TryGetValue(relativePath, out entry))
                {
                    entry.Cts.Cancel();
                    _pending.Remove(relativePath);
                }
            }
            await WriteAsync(relativePath, serialize).ConfigureAwait(false);
        }

        /// <summary>把所有待写的都刷下去。挂起/退出前调用。</summary>
        public async Task FlushAsync()
        {
            List<string> keys;
            lock (_gate)
            {
                keys = new List<string>(_pending.Keys);
            }
            foreach (var key in keys)
            {
                await DrainAsync(key).ConfigureAwait(false);
            }
        }

        private async Task DrainAsync(string relativePath)
        {
            Func<string> serialize = null;
            lock (_gate)
            {
                Entry entry;
                if (_pending.TryGetValue(relativePath, out entry))
                {
                    serialize = entry.Serialize;
                    entry.Cts.Cancel();
                    entry.Cts.Dispose();
                    _pending.Remove(relativePath);
                }
            }
            if (serialize == null) return;
            await WriteAsync(relativePath, serialize).ConfigureAwait(false);
        }

        private async Task WriteAsync(string relativePath, Func<string> serialize)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                string json;
                try
                {
                    json = serialize();
                }
                catch (Exception)
                {
                    if (_log != null) _log.Log(LogLevel.Error, "save", "序列化失败: " + relativePath);
                    return;
                }

                try
                {
                    await AtomicFile.WriteAsync(_store, relativePath, json).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (_log != null) _log.Log(LogLevel.Error, "save", "写入失败: " + relativePath);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var entry in _pending.Values)
                {
                    entry.Cts.Cancel();
                    entry.Cts.Dispose();
                }
                _pending.Clear();
            }
        }
    }

    /// <summary>
    /// 节流器：用于流式输出期间"最多每 N 毫秒保存一次"。
    /// 旧项目是每来一个 token 就考虑保存，手机上会明显掉帧。
    /// </summary>
    public sealed class Throttle
    {
        private readonly int _intervalMs;
        private readonly IClock _clock;
        private DateTimeOffset _last = DateTimeOffset.MinValue;

        public Throttle(IClock clock, int intervalMs)
        {
            _clock = clock ?? SystemClock.Instance;
            _intervalMs = intervalMs < 0 ? 0 : intervalMs;
        }

        /// <summary>到点返回 true，并重置计时。</summary>
        public bool ShouldRun()
        {
            var now = _clock.UtcNow;
            if ((now - _last).TotalMilliseconds < _intervalMs) return false;
            _last = now;
            return true;
        }

        public void Reset()
        {
            _last = DateTimeOffset.MinValue;
        }
    }
}
