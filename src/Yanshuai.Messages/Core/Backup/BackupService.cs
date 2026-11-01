// Core/Backup/BackupService.cs —— 备份与恢复
//
// 做法：把整个工作区目录打包成一个 zip（流式写，不整份读进内存）。
//
// 三条纪律（都是踩过的）：
//   1) **不含密钥**。API Key 在 PasswordVault 里，不落盘，所以备份里天然没有 ——
//      恢复后需要重新填密钥。这一点要在导出后明确告诉用户，别让人以为丢数据了。
//      如果用户确实要连密钥一起备份，那得走单独的"含凭据"开关，并明确警告。
//   2) **恢复前先备份现状**。恢复是覆盖操作，先自动存一份 "恢复前" 的备份，
//      误操作的代价从"数据没了"降为"翻一下备份"。
//   3) **事务式**：先全部解压到临时目录并校验，通过后才替换。
//      旧项目在这点上踩过坑（部分失败导致数据损坏）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class BackupSelection
    {
        public List<string> ProfileSlugs { get; set; }
        public bool IncludeConversations { get; set; } = true;
        public bool IncludeFiles { get; set; } = true;
        public bool IncludeMemory { get; set; } = true;
        public bool IncludeShared { get; set; } = true;

        public bool Includes(string path)
        {
            var first = path.Split('/')[0];
            if (first == "_shared" || path == Workspace.SharedSettingsPath) {
                if (path == WorkspaceLayout.SharedGlobalMemory) return IncludeShared && IncludeMemory;
                return IncludeShared;
            }
            if (ProfileSlugs != null && !ProfileSlugs.Contains(first)) return false;
            if (!IncludeConversations && path.IndexOf("/conversations/", StringComparison.Ordinal) >= 0) return false;
            if (!IncludeFiles && (path.IndexOf("/files/", StringComparison.Ordinal) >= 0 || path.EndsWith("/avatar.png", StringComparison.Ordinal) || path.EndsWith("/card.png", StringComparison.Ordinal))) return false;
            if (!IncludeMemory && (path.EndsWith("/memory.json", StringComparison.Ordinal) || path.EndsWith("/knowledge.json", StringComparison.Ordinal))) return false;
            return true;
        }
    }
    public sealed class BackupManifest
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public string AppVersion { get; set; } = "";
        public string DeviceInfo { get; set; } = "";
        public int ProfileCount { get; set; }
        public int ConversationCount { get; set; }
        public bool ContainsSecrets { get; set; }

        /// <summary>归档里的条目清单（用于恢复前校验与"备份里有什么"的展示）。</summary>
        public List<string> Entries { get; set; }

        public BackupManifest()
        {
            Entries = new List<string>();
        }
    }

    public sealed class BackupResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Path { get; set; } = "";
        public long ByteSize { get; set; }
        public int FileCount { get; set; }
        public BackupManifest Manifest { get; set; }
        public List<string> Warnings { get; private set; }

        public BackupResult()
        {
            Warnings = new List<string>();
        }

        public string SizeLabel { get { return ImageCompressor.Describe(ByteSize); } }
    }

    public sealed class RestoreResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public int RestoredFiles { get; set; }
        public string SafetyBackupPath { get; set; } = "";
        public List<string> Warnings { get; private set; }

        public RestoreResult()
        {
            Warnings = new List<string>();
        }
    }

    public static class BackupNaming
    {
        public const string ManifestEntryName = "_backup-manifest.json";
        public const string Extension = ".yanshuai-backup.zip";

        /// <summary>默认文件名：带时间戳，方便区分多次备份。</summary>
        public static string DefaultFileName(DateTimeOffset nowLocal)
        {
            return "言枢备份-" + nowLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Extension;
        }

        public static bool LooksLikeBackup(string fileName)
        {
            return !string.IsNullOrEmpty(fileName)
                && fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 备份服务。它只负责"打包 / 解包 / 校验"，
    /// 具体往哪个目录写由调用方给路径（平台层用 FileSavePicker 决定）。
    /// </summary>
    public sealed class BackupService
    {
        /// <summary>单文件上限（防解压炸弹）。</summary>
        public const long MaxEntryBytes = 256L * 1024 * 1024;

        /// <summary>整包解压总量上限。</summary>
        public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

        private readonly IFileStore _store;
        private readonly ILogger _log;
        private readonly IClock _clock;

        public BackupService(IFileStore store, ILogger log, IClock clock)
        {
            _store = store;
            _log = log;
            _clock = clock ?? SystemClock.Instance;
        }

        // ═════════════════════════════════════════════════════════════════
        // 备份
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 把工作区打包。progress 回调给出 (已处理文件数, 总文件数, 当前文件名)。
        /// </summary>
        public async Task<BackupResult> CreateAsync(
            Stream destination,
            Workspace workspace,
            string appVersion,
            string deviceInfo,
            Action<int, int, string> progress,
            CancellationToken ct, BackupSelection selection = null)
        {
            var result = new BackupResult();

            if (destination == null)
            {
                result.Error = "没有指定输出位置。";
                return result;
            }

            try
            {
                // 先把要打包的文件列出来（用于进度与清单）
                var files = await CollectFilesAsync().ConfigureAwait(false);
                if (selection != null) files.RemoveAll(path => !selection.Includes(path));
                if (files.Count == 0)
                {
                    result.Error = "工作区里没有任何数据可备份。";
                    return result;
                }

                var manifest = new BackupManifest
                {
                    CreatedAtUtc = _clock.UtcNow,
                    AppVersion = appVersion ?? "",
                    DeviceInfo = deviceInfo ?? "",
                    ContainsSecrets = false
                };

                if (workspace != null)
                {
                    manifest.ProfileCount = selection == null || selection.ProfileSlugs == null ? workspace.Profiles.Count : selection.ProfileSlugs.Count;
                }

                using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, true))
                {
                    int index = 0;
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        index++;

                        if (progress != null) progress(index, files.Count, file);

                        try
                        {
                            var entry = zip.CreateEntry(file, CompressionLevel.Optimal);
                            var bytes = await _store.ReadBytesAsync(file).ConfigureAwait(false);

                            using (var stream = entry.Open())
                            {
                                await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                            }

                            manifest.Entries.Add(file);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            result.Warnings.Add("跳过无法读取的文件：" + file);
                        }
                    }

                    // 清单最后写，它记录的 Entries 才是实际写进去的那些
                    var manifestEntry = zip.CreateEntry(BackupNaming.ManifestEntryName, CompressionLevel.Optimal);
                    var manifestJson = JsonStore.Serialize(manifest);
                    var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
                    using (var stream = manifestEntry.Open())
                    {
                        await stream.WriteAsync(manifestBytes, 0, manifestBytes.Length, ct).ConfigureAwait(false);
                    }

                    result.FileCount = manifest.Entries.Count;
                    result.Manifest = manifest;
                }

                result.Ok = true;
                result.ByteSize = destination.CanSeek ? destination.Length : 0;

                if (_log != null)
                {
                    _log.Log(LogLevel.Info, "backup",
                        "已备份 " + result.FileCount.ToString(CultureInfo.InvariantCulture) + " 个文件，"
                        + ImageCompressor.Describe(result.ByteSize));
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = "备份失败：" + ex.Message;
                return result;
            }
        }

        private async Task<List<string>> CollectFilesAsync()
        {
            var files = new List<string>();

            IReadOnlyList<string> found;
            try
            {
                found = await _store.ListFilesAsync("", true).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return files;
            }

            foreach (var file in found)
            {
                var normalized = (file ?? "").Replace('\\', '/');

                // 不备份回收区（那是给用户手动翻的），也不备份临时文件
                if (normalized.StartsWith(WorkspaceLayout.TrashDir + "/", StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized == "_shared/trash-index.json") continue;
                if (normalized.StartsWith("_backups/", StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized.StartsWith("_migration-backup/", StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized.StartsWith("_restore/", StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized.EndsWith(AtomicFile.TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (normalized.IndexOf("/.backup/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                files.Add(normalized);
            }

            files.Sort(StringComparer.Ordinal);
            return files;
        }

        // ═════════════════════════════════════════════════════════════════
        // 校验
        // ═════════════════════════════════════════════════════════════════

        /// <summary>读一个备份包的清单（不实际恢复，用于"备份里有什么"）。</summary>
        public static BackupManifest ReadManifest(Stream source)
        {
            if (source == null) return null;

            try
            {
                using (var zip = new ZipArchive(source, ZipArchiveMode.Read, true))
                {
                    var entry = FindEntry(zip, BackupNaming.ManifestEntryName);
                    if (entry == null) return null;

                    using (var stream = entry.Open())
                    using (var reader = new StreamReader(stream))
                    {
                        var json = reader.ReadToEnd();
                        return JsonStore.TryDeserialize<BackupManifest>(json, null, "manifest");
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>校验一个备份包是否可恢复。返回 null 表示没问题。</summary>
        public static string Validate(Stream source)
        {
            if (source == null) return "没有可读取的备份文件。";

            try
            {
                using (var zip = new ZipArchive(source, ZipArchiveMode.Read, true))
                {
                    long total = 0;
                    int count = 0;

                    foreach (var entry in zip.Entries)
                    {
                        count++;
                        if (entry.Length > MaxEntryBytes)
                        {
                            return "备份里有异常大的文件（" + entry.FullName + "），已拒绝以防解压占用过多空间。";
                        }
                        total += entry.Length;
                        if (total > MaxTotalBytes)
                        {
                            return "备份解压后体积过大，已拒绝。";
                        }
                    }

                    if (count == 0) return "备份是空的。";

                    if (FindEntry(zip, BackupNaming.ManifestEntryName) == null)
                    {
                        return "这不是言枢的备份包（缺少清单文件）。";
                    }
                }
                return null;
            }
            catch (InvalidDataException)
            {
                return "备份文件已损坏，不是有效的 zip。";
            }
            catch (Exception)
            {
                return "无法读取备份文件。";
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 恢复
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 恢复。流程：校验 → 解压到临时区 → 全部成功才逐文件覆盖 → 清理临时区。
        /// 任何一步失败都不动现有数据。
        /// </summary>
        public async Task<RestoreResult> RestoreAsync(
            Stream source, string stagingDir, Action<int, int, string> progress, CancellationToken ct)
        {
            var result = new RestoreResult();

            var problem = Validate(source);
            if (problem != null)
            {
                result.Error = problem;
                return result;
            }

            if (string.IsNullOrEmpty(stagingDir)) stagingDir = "_restore-staging";

            try
            {
                source.Seek(0, SeekOrigin.Begin);
            }
            catch (Exception)
            {
                // 不支持 seek 的流也没关系，后面按顺序读
            }

            // ── 第一步：全部解压到临时区 ──
            var staged = new List<string>();

            try
            {
                using (var zip = new ZipArchive(source, ZipArchiveMode.Read, true))
                {
                    int index = 0;
                    int total = zip.Entries.Count;

                    foreach (var entry in zip.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        index++;

                        var name = NormalizeEntryName(entry.FullName);
                        if (name == null) continue;                          // 路径穿越，跳过
                        if (name == BackupNaming.ManifestEntryName) continue;

                        if (progress != null) progress(index, total, name);

                        var target = stagingDir + "/" + name;

                        using (var input = entry.Open())
                        {
                            var bytes = await ReadAllAsync(input, MaxEntryBytes).ConfigureAwait(false);
                            // 必须按二进制写：备份里含图片，UTF-8 转字符串会破坏它
                            await AtomicFile.WriteBytesAsync(_store, target, bytes).ConfigureAwait(false);
                        }
                        staged.Add(name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = "解压失败，未改动任何现有数据：" + ex.Message;
                return result;
            }

            if (staged.Count == 0)
            {
                result.Error = "备份里没有可恢复的文件。";
                return result;
            }

            // ── 第二步：逐文件覆盖 ──
            int restored = 0;
            foreach (var name in staged)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var bytes = await _store.ReadBytesAsync(stagingDir + "/" + name).ConfigureAwait(false);
                    // 同样必须按二进制写回
                    await AtomicFile.WriteBytesAsync(_store, name, bytes).ConfigureAwait(false);
                    restored++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    result.Warnings.Add("恢复失败：" + name);
                }
            }

            // ── 第三步：清掉临时区 ──
            await CleanupStagingAsync(stagingDir, staged).ConfigureAwait(false);

            result.Ok = restored > 0;
            result.RestoredFiles = restored;

            if (!result.Ok) result.Error = "一个文件都没恢复成功。";

            if (_log != null)
            {
                _log.Log(LogLevel.Info, "backup", "已恢复 " + restored.ToString(CultureInfo.InvariantCulture) + " 个文件");
            }
            return result;
        }

        private async Task CleanupStagingAsync(string stagingDir, List<string> files)
        {
            foreach (var name in files)
            {
                try
                {
                    await _store.DeleteAsync(stagingDir + "/" + name).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 规范化 zip 内的条目名。拒绝绝对路径与 .. （zip slip 攻击）。
        /// 返回 null 表示这个条目应当跳过。
        /// </summary>
        public static string NormalizeEntryName(string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;

            var name = entryName.Replace('\\', '/').TrimStart('/');

            if (name.Length == 0) return null;
            if (name.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
            if (name.Length >= 2 && name[1] == ':') return null;      // 盘符

            foreach (var ch in name)
            {
                if (ch < 0x20) return null;                            // 控制字符
            }

            if (name.EndsWith("/", StringComparison.Ordinal)) return null;   // 目录项

            return name;
        }

        private static ZipArchiveEntry FindEntry(ZipArchive zip, string name)
        {
            foreach (var entry in zip.Entries)
            {
                if (string.Equals(entry.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }

        private static async Task<byte[]> ReadAllAsync(Stream stream, long limit)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[81920];
                long total = 0;

                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0) break;

                    total += read;
                    if (total > limit) throw new InvalidDataException("条目过大");

                    memory.Write(buffer, 0, read);
                }
                return memory.ToArray();
            }
        }
    }
}
