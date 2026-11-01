// Application operations shared by the native management pages.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class TrashEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public string StoredPath { get; set; } = "";
        public DateTimeOffset DeletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class WorkspaceOperations
    {
        private readonly Workspace _workspace;
        private readonly IFileStore _store;
        private readonly ILogger _log;
        private readonly IClock _clock;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private const string TrashIndex = "_shared/trash-index.json";

        public WorkspaceOperations(Workspace workspace, IFileStore store, ILogger log, IClock clock)
        {
            _workspace = workspace;
            _store = store;
            _log = log;
            _clock = clock ?? SystemClock.Instance;
        }

        public async Task<List<TrashEntry>> ListTrashAsync()
        {
            var entries = await JsonStore.TryReadAsync<List<TrashEntry>>(_store, TrashIndex, _log).ConfigureAwait(false)
                ?? new List<TrashEntry>();
            return entries.OrderByDescending(x => x.DeletedAtUtc).ToList();
        }

        public async Task<TrashEntry> RecycleFileAsync(string path, string name, string kind)
        {
            ValidatePath(path);
            if (path.StartsWith("_", StringComparison.Ordinal))
                throw new ArgumentException("不能回收共享配置。");
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!await _store.ExistsAsync(path).ConfigureAwait(false)) throw new InvalidOperationException("文件已不存在。");
                var entry = new TrashEntry { Name = name, Kind = kind, OriginalPath = path, DeletedAtUtc = _clock.UtcNow };
                entry.StoredPath = "_trash/" + entry.Id + "/" + WorkspaceLayout.FileName(path);
                var entries = await ListTrashAsync().ConfigureAwait(false);
                // Write metadata first; a failed move leaves the source intact and is removed below.
                entries.Add(entry);
                await AtomicFile.WriteAsync(_store, TrashIndex, JsonStore.Serialize(entries)).ConfigureAwait(false);
                try { await _store.MoveAsync(path, entry.StoredPath).ConfigureAwait(false); }
                catch
                {
                    entries.Remove(entry);
                    await AtomicFile.WriteAsync(_store, TrashIndex, JsonStore.Serialize(entries)).ConfigureAwait(false);
                    throw;
                }
                return entry;
            }
            finally { _gate.Release(); }
        }

        public async Task RecycleConversationAsync(Profile profile, Conversation conversation)
        {
            if (profile == null || conversation == null) throw new ArgumentNullException("conversation");
            await RecycleFileAsync(WorkspaceLayout.ConversationFile(profile.Slug, conversation.Id),
                conversation.Title, "conversation").ConfigureAwait(false);
            // Source has already moved. This removes only the index entry.
            await _workspace.DeleteConversationAsync(profile.Slug, conversation.Id).ConfigureAwait(false);
        }

        public async Task RecycleProfileAsync(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            ValidatePath(profile.Slug);
            var entry = new TrashEntry { Kind = "profile", Name = profile.Name,
                OriginalPath = profile.Slug, StoredPath = "_trash/" + Guid.NewGuid().ToString("N") + "/profile",
                DeletedAtUtc = _clock.UtcNow };
            var entries = await ListTrashAsync().ConfigureAwait(false);
            var files = await _store.ListFilesAsync(profile.Slug, true).ConfigureAwait(false);
            var moved = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var file in files)
                {
                    var target = entry.StoredPath + file.Substring(profile.Slug.Length);
                    await _store.MoveAsync(file, target).ConfigureAwait(false);
                    moved.Add(new KeyValuePair<string, string>(file, target));
                }
                entries.Add(entry);
                await AtomicFile.WriteAsync(_store, TrashIndex, JsonStore.Serialize(entries)).ConfigureAwait(false);
            }
            catch
            {
                for (int i = moved.Count - 1; i >= 0; i--) await _store.MoveAsync(moved[i].Value, moved[i].Key).ConfigureAwait(false);
                throw;
            }
            await _workspace.ReloadAsync().ConfigureAwait(false);
        }

        public async Task RestoreAsync(TrashEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            ValidatePath(entry.OriginalPath);
            ValidatePath(entry.StoredPath);
            if (!entry.StoredPath.StartsWith("_trash/", StringComparison.Ordinal))
                throw new ArgumentException("回收路径不正确。");
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (entry.Kind == "profile")
                {
                    if (await _store.ExistsAsync(WorkspaceLayout.ProfileFile(entry.OriginalPath)).ConfigureAwait(false))
                        throw new InvalidOperationException("原位置已有同名档案，不能覆盖。");
                    var files = await _store.ListFilesAsync(entry.StoredPath, true).ConfigureAwait(false);
                    if (files.Count == 0) throw new InvalidOperationException("回收档案的文件已不存在。");
                    foreach (var file in files)
                        if (await _store.ExistsAsync(entry.OriginalPath + file.Substring(entry.StoredPath.Length)).ConfigureAwait(false))
                            throw new InvalidOperationException("原位置存在同名文件，无法安全恢复。");
                    var moved = new List<KeyValuePair<string, string>>();
                    try {
                        foreach (var file in files) {
                            var target = entry.OriginalPath + file.Substring(entry.StoredPath.Length);
                            await _store.MoveAsync(file, target).ConfigureAwait(false);
                            moved.Add(new KeyValuePair<string, string>(file, target));
                        }
                    } catch {
                        for (int i = moved.Count - 1; i >= 0; i--) await _store.MoveAsync(moved[i].Value, moved[i].Key).ConfigureAwait(false);
                        throw;
                    }
                    var profileEntries = await ListTrashAsync().ConfigureAwait(false);
                    profileEntries.RemoveAll(x => x.Id == entry.Id);
                    await AtomicFile.WriteAsync(_store, TrashIndex, JsonStore.Serialize(profileEntries)).ConfigureAwait(false);
                    await _workspace.ReloadAsync().ConfigureAwait(false);
                    return;
                }
                if (await _store.ExistsAsync(entry.OriginalPath).ConfigureAwait(false))
                    throw new InvalidOperationException("原位置已有同名数据，不能覆盖。");
                var first = entry.OriginalPath.Split('/')[0];
                var owner = _workspace.Profiles.FirstOrDefault(x => x.Slug == first);
                if (owner == null) throw new InvalidOperationException("所属档案已不存在，请先恢复档案。");
                await _store.MoveAsync(entry.StoredPath, entry.OriginalPath).ConfigureAwait(false);
                if (entry.Kind == "conversation") await RebuildIndexAsync(owner).ConfigureAwait(false);
                var entries = await ListTrashAsync().ConfigureAwait(false);
                entries.RemoveAll(x => x.Id == entry.Id);
                await AtomicFile.WriteAsync(_store, TrashIndex, JsonStore.Serialize(entries)).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task PurgeAsync(TrashEntry entry)
        {
            if (entry == null || !entry.StoredPath.StartsWith("_trash/", StringComparison.Ordinal))
                throw new ArgumentException("回收路径不正确。");
            ValidatePath(entry.StoredPath);
            if (entry.Kind == "profile")
                foreach (var file in await _store.ListFilesAsync(entry.StoredPath, true).ConfigureAwait(false)) await _store.DeleteAsync(file).ConfigureAwait(false);
            else await _store.DeleteAsync(entry.StoredPath).ConfigureAwait(false);
            var entries = await ListTrashAsync().ConfigureAwait(false);
            entries.RemoveAll(x => x.Id == entry.Id);
            await AtomicFile.WriteAsync(_store, TrashIndex, JsonStore.Serialize(entries)).ConfigureAwait(false);
        }

        public async Task<int> RebuildIndexAsync(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            var index = await _workspace.LoadIndexAsync(profile.Slug, false).ConfigureAwait(false);
            var restored = new List<Conversation>();
            foreach (var path in await _store.ListFilesAsync(WorkspaceLayout.ConversationDir(profile.Slug), false).ConfigureAwait(false))
            {
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || WorkspaceLayout.FileName(path) == "index.json") continue;
                var conversation = await _workspace.LoadConversationAsync(profile.Slug, WorkspaceLayout.FileNameWithoutExtension(path)).ConfigureAwait(false);
                if (conversation != null) restored.Add(conversation);
            }
            var rebuilt = new ConversationIndex();
            foreach (var conversation in restored)
                rebuilt.Upsert(new ConversationIndexEntry {
                    Id = conversation.Id, ProfileId = profile.Id, Title = conversation.Title,
                    Preview = conversation.Preview(), CreatedAtUtc = conversation.CreatedAtUtc,
                    UpdatedAtUtc = conversation.UpdatedAtUtc, Pinned = conversation.Pinned,
                    Archived = conversation.Archived, Tokens = conversation.TotalInputTokens + conversation.TotalOutputTokens
                });
            await AtomicFile.WriteAsync(_store, WorkspaceLayout.ConversationIndexFile(profile.Slug),
                JsonStore.Serialize(rebuilt)).ConfigureAwait(false);
            index.Entries = rebuilt.Entries;
            return restored.Count;
        }

        public async Task MoveConversationAsync(Profile source, Profile target, Conversation conversation)
        {
            if (source == null || target == null || conversation == null) throw new ArgumentNullException("conversation");
            if (source.Id == target.Id) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var destination = WorkspaceLayout.ConversationFile(target.Slug, conversation.Id);
                if (await _store.ExistsAsync(destination).ConfigureAwait(false)) throw new InvalidOperationException("目标档案已有此会话。");
                var copy = JsonBridge.Clone(conversation);
                copy.ProfileId = target.Id;
                // Copy attachment bytes before committing the destination conversation.
                var all = new List<Message>(copy.Messages);
                foreach (var variants in copy.Variants.Values) all.AddRange(variants);
                var copied = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var message in all)
                foreach (var attachment in message.Attachments)
                {
                    if (string.IsNullOrWhiteSpace(attachment.Path)) continue;
                    string newPath;
                    if (!copied.TryGetValue(attachment.Path, out newPath))
                    {
                        var oldPath = attachment.Path.StartsWith(source.Slug + "/", StringComparison.Ordinal)
                            ? attachment.Path : source.Slug + "/" + attachment.Path;
                        newPath = WorkspaceLayout.AttachmentsDir(target.Slug) + "/" + Guid.NewGuid().ToString("N")
                            + "-" + WorkspaceLayout.FileName(attachment.Path);
                        var bytes = await _store.ReadBytesAsync(oldPath).ConfigureAwait(false);
                        await AtomicFile.WriteBytesAsync(_store, newPath, bytes).ConfigureAwait(false);
                        copied.Add(attachment.Path, newPath);
                    }
                    attachment.Path = newPath;
                }
                await _workspace.SaveConversationAsync(target.Slug, copy).ConfigureAwait(false);
                await _workspace.DeleteConversationAsync(source.Slug, conversation.Id).ConfigureAwait(false);
                conversation.ProfileId = target.Id;
            }
            finally { _gate.Release(); }
        }

        public async Task<bool> SaveTextAsync(string path, string expectedHash, string content, bool overwrite)
        {
            ValidatePath(path);
            if (path.StartsWith("_", StringComparison.Ordinal)) throw new ArgumentException("只允许编辑档案文件。");
            if (!overwrite && await _store.ExistsAsync(path).ConfigureAwait(false))
            {
                var current = await _store.ReadTextAsync(path).ConfigureAwait(false);
                if (TextUtil.Sha256Hex(current) != expectedHash) return false;
            }
            else if (!overwrite && expectedHash != null) return false; // File deleted while editing.
            await AtomicFile.WriteAsync(_store, path, content ?? "").ConfigureAwait(false);
            return true;
        }

        public static void ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal)
                || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || path.IndexOf('\0') >= 0)
                throw new ArgumentException("路径必须是工作区内的相对路径。");
            foreach (var segment in path.Split('/'))
                if (segment == ".." || segment == "." || segment.Length == 0)
                    throw new ArgumentException("路径不能包含目录跳转。");
        }
    }
}
