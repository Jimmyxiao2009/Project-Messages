// Core/Storage/Workspace.cs —— 工作区门面
//
// UI 只跟这个类打交道，不直接碰文件路径。它负责：
//   • 装载/保存共享区（设置、模型档案、注入库、用户人设、权限）
//   • 档案的增删改（删除是移入回收区，不是抹掉）
//   • 会话的增删改 + 索引维护（列表页只读索引，不打开每个会话）
//   • 密钥：API Key 与搜索 Key 只进 ISecretStore，配置文件里永远没有明文
//
// 与旧项目的区别：不再有一个"所有东西塞一起"的巨型 JSON 与整体重写，
// 每个会话一个文件，档案一个文件夹，写坏了也只坏那一份。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class Workspace
    {
        private readonly IFileStore _store;
        private readonly ISecretStore _secrets;
        private readonly ILogger _log;
        private readonly IClock _clock;

        private readonly List<Profile> _profiles = new List<Profile>();
        private readonly Dictionary<string, ConversationIndex> _indexCache =
            new Dictionary<string, ConversationIndex>(StringComparer.Ordinal);

        public Workspace(IFileStore store, ISecretStore secrets, ILogger log, IClock clock)
        {
            _store = store;
            _secrets = secrets;
            _log = log;
            _clock = clock ?? SystemClock.Instance;

            Settings = new GlobalSettings();
            ApiProfiles = new ApiProfileFile();
            Library = new PromptLibrary();
            UserPersonas = new UserPersonaFile();
            McpServers = new List<McpServerConfig>();
            Skills = new List<SkillDefinition>();
            Permissions = new PermissionService(store, log);
        }

        public GlobalSettings Settings { get; private set; }
        public ApiProfileFile ApiProfiles { get; private set; }
        public PromptLibrary Library { get; private set; }
        public UserPersonaFile UserPersonas { get; private set; }
        public List<McpServerConfig> McpServers { get; private set; }
        public List<SkillDefinition> Skills { get; private set; }
        public PermissionService Permissions { get; private set; }

        public IReadOnlyList<Profile> Profiles { get { return _profiles.AsReadOnly(); } }

        // ═════════════════════════════════════════════════════════════════
        // 装载
        // ═════════════════════════════════════════════════════════════════
        public async Task InitializeAsync()
        {
            Settings = await JsonStore.TryReadAsync<GlobalSettings>(_store, SharedSettingsPath, _log).ConfigureAwait(false)
                       ?? new GlobalSettings();
            Settings.Repair();
            Settings.Search.ApiKey = _secrets.Get(SearchSecretKey);

            ApiProfiles = await JsonStore.TryReadAsync<ApiProfileFile>(_store, WorkspaceLayout.SharedApiProfiles, _log)
                          .ConfigureAwait(false) ?? new ApiProfileFile();
            if (ApiProfiles.Items == null) ApiProfiles.Items = new List<ApiProfile>();

            Library = await JsonStore.TryReadAsync<PromptLibrary>(_store, WorkspaceLayout.SharedLibrary, _log)
                      .ConfigureAwait(false) ?? new PromptLibrary();
            if (Library.Injections == null) Library.Injections = new List<ModeInjection>();
            if (Library.Lorebooks == null) Library.Lorebooks = new List<Lorebook>();

            UserPersonas = await JsonStore.TryReadAsync<UserPersonaFile>(_store, WorkspaceLayout.SharedUserPersonas, _log)
                           .ConfigureAwait(false) ?? new UserPersonaFile();
            if (UserPersonas.Items == null) UserPersonas.Items = new List<UserPersona>();

            McpServers = await JsonStore.TryReadAsync<List<McpServerConfig>>(_store, WorkspaceLayout.SharedMcp, _log)
                         .ConfigureAwait(false) ?? new List<McpServerConfig>();

            Skills = await JsonStore.TryReadAsync<List<SkillDefinition>>(_store, WorkspaceLayout.SharedSkills, _log)
                     .ConfigureAwait(false) ?? new List<SkillDefinition>();

            await Permissions.LoadAsync().ConfigureAwait(false);

            await LoadProfilesAsync().ConfigureAwait(false);
        }

        /// <summary>设置单独放一个文件，方便在档案目录坏掉时仍然能进设置页。</summary>
        public const string SharedSettingsPath = "_settings.json";
        public const string SearchSecretKey = "search.default";

        private async Task LoadProfilesAsync()
        {
            _profiles.Clear();

            IReadOnlyList<string> dirs;
            try
            {
                dirs = await _store.ListDirectoriesAsync("").ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var dir in dirs)
            {
                var slug = WorkspaceLayout.FileName(dir);
                if (string.IsNullOrEmpty(slug)) continue;
                if (string.Equals(slug, WorkspaceLayout.SharedDir, StringComparison.Ordinal)) continue;
                if (string.Equals(slug, WorkspaceLayout.TrashDir, StringComparison.Ordinal)) continue;

                var profile = await JsonStore.TryReadAsync<Profile>(
                    _store, WorkspaceLayout.ProfileFile(slug), _log).ConfigureAwait(false);
                if (profile == null) continue;

                profile.Repair();
                if (string.IsNullOrEmpty(profile.Slug)) profile.Slug = slug;
                _profiles.Add(profile);
            }

            _profiles.Sort(delegate (Profile a, Profile b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
        }

        // ═════════════════════════════════════════════════════════════════
        // 设置
        // ═════════════════════════════════════════════════════════════════
        public Task SaveSettingsAsync()
        {
            Settings.Repair();
            _secrets.Set(SearchSecretKey, Settings.Search.ApiKey ?? "");
            return AtomicFile.WriteAsync(_store, SharedSettingsPath, JsonStore.Serialize(Settings));
        }

        // ═════════════════════════════════════════════════════════════════
        // 模型档案
        // ═════════════════════════════════════════════════════════════════
        public ApiProfile DefaultApiProfile()
        {
            return ApiProfiles.Resolve("");
        }

        public string GetApiKey(ApiProfile profile)
        {
            if (profile == null) return "";
            return _secrets.Get(profile.SecretKey) ?? "";
        }

        public void SetApiKey(ApiProfile profile, string key)
        {
            if (profile == null) return;
            _secrets.Set(profile.SecretKey, key ?? "");
        }

        public Task SaveApiProfilesAsync()
        {
            return AtomicFile.WriteAsync(_store, WorkspaceLayout.SharedApiProfiles, JsonStore.Serialize(ApiProfiles));
        }

        public async Task DeleteApiProfileAsync(string id)
        {
            var profile = ApiProfiles.Find(id);
            if (profile == null) return;

            _secrets.Remove(profile.SecretKey);
            for (int i = ApiProfiles.Items.Count - 1; i >= 0; i--)
            {
                if (ApiProfiles.Items[i].Id == id) ApiProfiles.Items.RemoveAt(i);
            }
            if (ApiProfiles.DefaultId == id) ApiProfiles.DefaultId = "";

            await SaveApiProfilesAsync().ConfigureAwait(false);
        }

        // ═════════════════════════════════════════════════════════════════
        // 档案
        // ═════════════════════════════════════════════════════════════════
        public Profile FindProfile(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var profile in _profiles)
            {
                if (string.Equals(profile.Id, id, StringComparison.Ordinal)) return profile;
            }
            return null;
        }

        /// <summary>新建档案。slug 冲突时自动加后缀，保证一个档案一个独立文件夹。</summary>
        public async Task<Profile> CreateProfileAsync(string name, string slugHint)
        {
            var profile = new Profile { Name = string.IsNullOrWhiteSpace(name) ? "新档案" : name.Trim() };
            profile.Persona.Name = profile.Name;
            profile.Slug = await UniqueSlugAsync(string.IsNullOrEmpty(slugHint) ? profile.Name : slugHint)
                .ConfigureAwait(false);
            profile.Repair();

            await EnsureProfileFoldersAsync(profile.Slug).ConfigureAwait(false);
            await SaveProfileAsync(profile).ConfigureAwait(false);
            _profiles.Add(profile);

            return profile;
        }

        private async Task<string> UniqueSlugAsync(string hint)
        {
            var baseSlug = TextUtil.Slug(hint);
            var candidate = baseSlug;
            int suffix = 2;

            while (true)
            {
                bool taken = false;
                foreach (var profile in _profiles)
                {
                    if (string.Equals(profile.Slug, candidate, StringComparison.Ordinal)) { taken = true; break; }
                }
                if (!taken)
                {
                    try
                    {
                        if (await _store.ExistsAsync(WorkspaceLayout.ProfileFile(candidate)).ConfigureAwait(false))
                        {
                            taken = true;
                        }
                    }
                    catch (Exception) { }
                }
                if (!taken) return candidate;

                candidate = baseSlug + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
        }

        private async Task EnsureProfileFoldersAsync(string slug)
        {
            await _store.EnsureDirectoryAsync(WorkspaceLayout.FilesDir(slug)).ConfigureAwait(false);
            await _store.EnsureDirectoryAsync(WorkspaceLayout.AttachmentsDir(slug)).ConfigureAwait(false);
            await _store.EnsureDirectoryAsync(WorkspaceLayout.ConversationDir(slug)).ConfigureAwait(false);
        }

        public async Task SaveProfileAsync(Profile profile)
        {
            if (profile == null) return;
            profile.Repair();
            await EnsureProfileFoldersAsync(profile.Slug).ConfigureAwait(false);
            await AtomicFile.WriteAsync(_store, WorkspaceLayout.ProfileFile(profile.Slug),
                JsonStore.Serialize(profile)).ConfigureAwait(false);
        }

        /// <summary>删除档案：整包移入回收区，不直接抹掉。</summary>
        public async Task<bool> DeleteProfileAsync(string id)
        {
            var profile = FindProfile(id);
            if (profile == null) return false;

            try
            {
                var target = WorkspaceLayout.TrashDirFor(profile.Slug, _clock.UtcNow);
                await _store.EnsureDirectoryAsync(WorkspaceLayout.TrashDir).ConfigureAwait(false);

                // 逐个文件搬家（IFileStore 没有整目录移动）
                var files = await _store.ListFilesAsync(profile.Slug, true).ConfigureAwait(false);
                foreach (var file in files)
                {
                    var relative = file.Substring(profile.Slug.Length).TrimStart('/');
                    var destination = target + "/" + relative;
                    await _store.MoveAsync(file, destination).ConfigureAwait(false);
                }

                _indexCache.Remove(profile.Slug);
                _profiles.Remove(profile);
                return true;
            }
            catch (Exception)
            {
                if (_log != null) _log.Log(LogLevel.Error, "workspace", "删除档案失败: " + profile.Slug);
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 会话
        // ═════════════════════════════════════════════════════════════════
        public Conversation CreateConversation(Profile profile, string title)
        {
            var conversation = new Conversation
            {
                ProfileId = profile != null ? profile.Id : "",
                Title = string.IsNullOrWhiteSpace(title) ? "新对话" : title.Trim()
            };
            return conversation;
        }

        public async Task<ConversationIndex> LoadIndexAsync(string slug, bool forceReload)
        {
            if (!forceReload)
            {
                ConversationIndex cached;
                if (_indexCache.TryGetValue(slug, out cached)) return cached;
            }

            var index = await JsonStore.TryReadAsync<ConversationIndex>(
                _store, WorkspaceLayout.ConversationIndexFile(slug), _log).ConfigureAwait(false)
                ?? new ConversationIndex();

            if (index.Entries == null) index.Entries = new List<ConversationIndexEntry>();
            _indexCache[slug] = index;
            return index;
        }

        public async Task<List<ConversationIndexEntry>> ListConversationsAsync(string slug, bool includeArchived)
        {
            var index = await LoadIndexAsync(slug, false).ConfigureAwait(false);
            return index.Sorted(includeArchived);
        }

        public async Task<List<ConversationIndexEntry>> ListAllConversationsAsync(bool includeArchived)
        {
            var all = new List<ConversationIndexEntry>();
            foreach (var profile in _profiles)
            {
                var entries = await ListConversationsAsync(profile.Slug, includeArchived).ConfigureAwait(false);
                all.AddRange(entries);
            }
            all.Sort(delegate (ConversationIndexEntry a, ConversationIndexEntry b)
            {
                if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
                return b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc);
            });
            return all;
        }

        public async Task<Conversation> LoadConversationAsync(string slug, string conversationId)
        {
            var conversation = await JsonStore.TryReadAsync<Conversation>(
                _store, WorkspaceLayout.ConversationFile(slug, conversationId), _log).ConfigureAwait(false);
            if (conversation == null) return null;

            conversation.Repair();
            return conversation;
        }

        public Task SaveConversationAsync(string slug, Conversation conversation)
        {
            return SaveConversationAsync(slug, conversation, true);
        }

        public async Task SaveConversationAsync(string slug, Conversation conversation, bool updateIndex)
        {
            if (conversation == null || string.IsNullOrEmpty(slug)) return;

            conversation.Touch();
            await AtomicFile.WriteAsync(_store,
                WorkspaceLayout.ConversationFile(slug, conversation.Id),
                JsonStore.Serialize(conversation)).ConfigureAwait(false);

            if (!updateIndex) return;

            var index = await LoadIndexAsync(slug, false).ConfigureAwait(false);
            index.Upsert(new ConversationIndexEntry
            {
                Id = conversation.Id,
                ProfileId = conversation.ProfileId,
                Title = conversation.Title,
                Preview = conversation.Preview(),
                CreatedAtUtc = conversation.CreatedAtUtc,
                UpdatedAtUtc = conversation.UpdatedAtUtc,
                Archived = conversation.Archived,
                Pinned = conversation.Pinned,
                Tokens = conversation.TotalInputTokens + conversation.TotalOutputTokens
            });

            await AtomicFile.WriteAsync(_store, WorkspaceLayout.ConversationIndexFile(slug),
                JsonStore.Serialize(index)).ConfigureAwait(false);
        }

        public async Task DeleteConversationAsync(string slug, string conversationId)
        {
            try
            {
                await _store.DeleteAsync(WorkspaceLayout.ConversationFile(slug, conversationId)).ConfigureAwait(false);
            }
            catch (Exception) { }

            var index = await LoadIndexAsync(slug, false).ConfigureAwait(false);
            if (index.Remove(conversationId))
            {
                await AtomicFile.WriteAsync(_store, WorkspaceLayout.ConversationIndexFile(slug),
                    JsonStore.Serialize(index)).ConfigureAwait(false);
            }
        }

        /// <summary>标记会话状态，让列表页能显示"需要你处理/运行中/失败"。</summary>
        public async Task MarkConversationStateAsync(
            string slug, string conversationId, bool needsAttention, bool running, bool failed)
        {
            var index = await LoadIndexAsync(slug, false).ConfigureAwait(false);
            var entry = index.Find(conversationId);
            if (entry == null) return;

            entry.NeedsAttention = needsAttention;
            entry.Running = running;
            entry.Failed = failed;

            await AtomicFile.WriteAsync(_store, WorkspaceLayout.ConversationIndexFile(slug),
                JsonStore.Serialize(index)).ConfigureAwait(false);
        }

        // ═════════════════════════════════════════════════════════════════
        // 其余共享区
        // ═════════════════════════════════════════════════════════════════
        public Task SaveLibraryAsync()
        {
            return AtomicFile.WriteAsync(_store, WorkspaceLayout.SharedLibrary, JsonStore.Serialize(Library));
        }

        /// <summary>
        /// 清空回收区，返回清掉的条目数。
        ///
        /// 只删 _trash 下的东西 —— 这是整个应用里唯一一处"真删"。
        /// 回收区里的内容本来就是用户已经删过的，所以这里不需要二次确认
        /// 之外的额外保护；但路径必须严格限定在 TrashDir 前缀内，
        /// 否则一个写错的路径就可能删到档案本体。
        /// </summary>
        public async Task<int> PurgeTrashAsync()
        {
            var removed = 0;
            try
            {
                var files = await _store.ListFilesAsync(WorkspaceLayout.TrashDir, true).ConfigureAwait(false);
                foreach (var file in files)
                {
                    var normalized = (file ?? "").Replace('\\', '/');
                    if (!normalized.StartsWith(WorkspaceLayout.TrashDir + "/", StringComparison.Ordinal)) continue;

                    await _store.DeleteAsync(normalized).ConfigureAwait(false);
                    removed++;
                }

                // 空目录留着不影响使用，IFileStore 也没有删目录的能力
                if (_log != null) _log.Log(LogLevel.Info, "workspace", "已清空回收区：" + removed + " 个文件");
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Log(LogLevel.Warning, "workspace", "清空回收区失败：" + ex.GetType().Name);
            }

            return removed;
        }

        /// <summary>
        /// 从磁盘重新加载共享区与档案列表。恢复备份之后必须调用 ——
        /// 否则界面还是恢复前那份内存里的旧数据。
        /// </summary>
        public async Task ReloadAsync()
        {
            _indexCache.Clear();
            await InitializeAsync().ConfigureAwait(false);
        }

        public Task SaveUserPersonasAsync()
        {
            return AtomicFile.WriteAsync(_store, WorkspaceLayout.SharedUserPersonas, JsonStore.Serialize(UserPersonas));
        }

        public Task SaveMcpAsync()
        {
            return AtomicFile.WriteAsync(_store, WorkspaceLayout.SharedMcp, JsonStore.Serialize(McpServers));
        }

        public Task SaveSkillsAsync()
        {
            return AtomicFile.WriteAsync(_store, WorkspaceLayout.SharedSkills, JsonStore.Serialize(Skills));
        }

        /// <summary>按档案的开关过滤出本次可用的 MCP 服务器与技能。</summary>
        public List<McpServerConfig> ResolveMcp(Profile profile)
        {
            var result = new List<McpServerConfig>();
            if (profile == null) return result;

            foreach (var server in McpServers)
            {
                if (profile.Tools.McpServers == null || profile.Tools.McpServers.Count == 0) break;
                foreach (var name in profile.Tools.McpServers)
                {
                    if (string.Equals(name, server.Id, StringComparison.Ordinal) || string.Equals(name, server.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        var resolved = JsonBridge.Clone(server);
                        var token = _secrets.Get("mcp." + server.Id);
                        if (!string.IsNullOrEmpty(token)) resolved.Headers["Authorization"] = "Bearer " + token;
                        result.Add(resolved);
                        break;
                    }
                }
            }
            return result;
        }

        public List<SkillDefinition> ResolveSkills(Profile profile)
        {
            var result = new List<SkillDefinition>();
            if (profile == null) return result;

            bool all = profile.Tools.UseAllSkills && (profile.Tools.Skills == null || profile.Tools.Skills.Count == 0);
            foreach (var skill in Skills)
            {
                if (all) { result.Add(skill); continue; }
                foreach (var name in profile.Tools.Skills)
                {
                    if (string.Equals(name, skill.Id, StringComparison.Ordinal) || string.Equals(name, skill.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(skill);
                        break;
                    }
                }
            }
            return result;
        }

        public UserPersona ResolveUserPersona(Profile profile)
        {
            if (profile == null) return null;
            var persona = UserPersonas.Find(profile.UserPersonaId);
            if (persona != null) return persona;
            return UserPersonas.Find(UserPersonas.ActiveId);
        }
    }
}
