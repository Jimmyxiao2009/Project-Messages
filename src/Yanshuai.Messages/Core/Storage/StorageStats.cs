// Core/Storage/StorageStats.cs —— 存储空间统计
//
// 手机用户最常问的两件事：「怎么占了这么多」和「删什么能腾出来」。
// 所以统计的粒度按**能删的东西**分，而不是按目录结构分：
//   · 会话（含历史与分支）
//   · 附件与图片（最大头，也是用户最愿意删的）
//   · 记忆
//   · 备份与导出
//   · 日志
//
// 注意：体积用**字节数**算，不是字符数。中文一个字 UTF-8 占 3 字节，
// 用字符数会低估三倍，用户会觉得数字对不上。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class StorageBucket
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public long Bytes { get; set; }
        public int FileCount { get; set; }
        /// <summary>这个分类是否可以安全清理（清掉不影响正常使用）。</summary>
        public bool Cleanable { get; set; }
        /// <summary>清理时会做什么，给用户看。</summary>
        public string CleanupHint { get; set; } = "";
    }

    public sealed class StorageStats
    {
        public List<StorageBucket> Buckets { get; set; }
        public long TotalBytes { get; set; }
        public int TotalFiles { get; set; }
        public DateTimeOffset MeasuredAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public StorageStats()
        {
            Buckets = new List<StorageBucket>();
        }

        public StorageBucket Find(string key)
        {
            foreach (var bucket in Buckets)
            {
                if (string.Equals(bucket.Key, key, StringComparison.Ordinal)) return bucket;
            }
            return null;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";

            double value = bytes;
            string[] units = new string[] { "KB", "MB", "GB" };
            int unit = -1;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }
    }

    public sealed class StorageStatsService
    {
        private readonly IFileStore _store;
        private readonly ILogger _log;

        public StorageStatsService(IFileStore store, ILogger log)
        {
            _store = store;
            _log = log ?? NullLogger.Instance;
        }

        public async Task<StorageStats> MeasureAsync(string profileSlug, CancellationToken ct)
        {
            var stats = new StorageStats();

            List<string> files;
            try
            {
                files = new List<string>(await _store.ListFilesAsync("", true).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "storage", "列举文件失败：" + ex.GetType().Name);
                return stats;
            }

            var conversations = NewBucket("conversations", "会话记录", false,
                "删除单个会话可在会话列表里长按操作。");
            var attachments = NewBucket("attachments", "附件与图片", false,
                "删除会话时其附件会一起进入回收区，可清空回收区释放空间。");
            var memory = NewBucket("memory", "记忆", false, "可在记忆页逐条删除或执行清理。");
            var backups = NewBucket("backups", "备份与导出", true,
                "确认不再需要后可以删除，不影响当前数据。");
            var logs = NewBucket("logs", "运行日志", true,
                "日志只用于排障，可以随时清空。");
            var trash = NewBucket("trash", "回收区", true,
                "回收区里的内容是已删除的数据，清空后无法恢复。");
            var other = NewBucket("other", "其他", false, "");

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                long size = await MeasureFileAsync(file).ConfigureAwait(false);
                stats.TotalBytes += size;
                stats.TotalFiles++;

                var bucket = MapBucket(file, conversations, attachments, memory, backups, logs, trash);
                if (bucket == null) bucket = other;

                bucket.Bytes += size;
                bucket.FileCount++;
            }

            foreach (var bucket in new StorageBucket[]
            {
                conversations, attachments, memory, backups, logs, trash, other
            })
            {
                if (bucket.FileCount > 0) stats.Buckets.Add(bucket);
            }

            stats.Buckets.Sort(delegate (StorageBucket a, StorageBucket b) { return b.Bytes.CompareTo(a.Bytes); });

            return stats;
        }

        /// <summary>
        /// 归类。顺序重要：更具体的规则放前面，否则 "/conversations/tech/" 会被
        /// 会话规则先吃掉，用量日志就永远归不到"日志"里。
        /// </summary>
        private static StorageBucket MapBucket(string path, StorageBucket conversations, StorageBucket attachments,
            StorageBucket memory, StorageBucket backups, StorageBucket logs, StorageBucket trash)
        {
            if (string.IsNullOrEmpty(path)) return null;

            if (path.StartsWith("_trash/", StringComparison.Ordinal)) return trash;
            if (path.StartsWith("_backups/", StringComparison.Ordinal)) return backups;
            if (path.StartsWith("_exports/", StringComparison.Ordinal)) return backups;

            if (path.IndexOf("/conversations/tech/", StringComparison.Ordinal) >= 0) return logs;
            if (path.IndexOf("/conversations/", StringComparison.Ordinal) >= 0) return conversations;

            if (path.IndexOf("/files/attachments/", StringComparison.Ordinal) >= 0) return attachments;
            if (path.EndsWith("/avatar.png", StringComparison.Ordinal)) return attachments;
            if (path.EndsWith("/card.png", StringComparison.Ordinal)) return attachments;

            if (path.EndsWith("/memory.json", StringComparison.Ordinal)) return memory;
            if (path.EndsWith("/knowledge.json", StringComparison.Ordinal)) return memory;
            if (path.EndsWith("/card.json", StringComparison.Ordinal)) return memory;

            if (path.EndsWith(".log", StringComparison.Ordinal)) return logs;

            return null;
        }

        /// <summary>量一个文件的字节数。文本按 UTF-8 真实编码长度算。</summary>
        private async Task<long> MeasureFileAsync(string path)
        {
            try
            {
                var bytes = await _store.ReadBytesAsync(path).ConfigureAwait(false);
                return bytes != null ? bytes.Length : 0;
            }
            catch (Exception)
            {
                // 读不出来（二进制或已损坏）时按文本再试一次，仍失败算 0
                try
                {
                    var text = await _store.ReadTextAsync(path).ConfigureAwait(false);
                    return text != null ? Encoding.UTF8.GetByteCount(text) : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        private static StorageBucket NewBucket(string key, string label, bool cleanable, string hint)
        {
            return new StorageBucket
            {
                Key = key,
                Label = label,
                Cleanable = cleanable,
                CleanupHint = hint
            };
        }

        /// <summary>渲染成给用户看的一段文本。</summary>
        public static string Format(StorageStats stats)
        {
            if (stats == null) return "还没有统计数据。";

            var sb = new StringBuilder();
            sb.Append("共占用 ").Append(StorageStats.FormatBytes(stats.TotalBytes))
              .Append("（").Append(stats.TotalFiles).Append(" 个文件）\n\n");

            if (stats.Buckets.Count == 0)
            {
                sb.Append("目前没有数据。");
                return sb.ToString();
            }

            foreach (var bucket in stats.Buckets)
            {
                double percent = stats.TotalBytes > 0 ? (double)bucket.Bytes / stats.TotalBytes * 100 : 0;
                sb.Append(bucket.Label).Append("  ")
                  .Append(StorageStats.FormatBytes(bucket.Bytes))
                  .Append("（").Append(percent.ToString("0", CultureInfo.InvariantCulture)).Append("%）");
                if (bucket.Cleanable) sb.Append("  [可清理]");
                sb.Append('\n');
            }

            // 把可清理项的做法写出来：用户看到"可清理"却不知道点哪里，等于没说
            var hints = new StringBuilder();
            foreach (var bucket in stats.Buckets)
            {
                if (!bucket.Cleanable || string.IsNullOrEmpty(bucket.CleanupHint)) continue;
                if (hints.Length > 0) hints.Append('\n');
                hints.Append("· ").Append(bucket.Label).Append("：").Append(bucket.CleanupHint);
            }

            if (hints.Length > 0)
            {
                sb.Append('\n').Append(hints);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
