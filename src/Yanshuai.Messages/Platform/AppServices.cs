// Platform/AppServices.cs —— 组合根
//
// 全应用只有这一处 new 具体实现，其余代码一律拿接口。
// 页面从这里取 Workspace / ChatService，不再自己拼依赖。

using System;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class AppServices
    {
        public static AppServices Current { get; private set; }

        public UwpFileStore Files { get; private set; }
        public UwpSecretStore Secrets { get; private set; }
        public HttpTransport Http { get; private set; }
        public DiagnosticLog Log { get; private set; }
        public Workspace Workspace { get; private set; }
        public WorkspaceOperations Operations { get; private set; }
        public ToolRegistry Registry { get; private set; }
        public UwpDeviceBridge Device { get; private set; }
        public UwpInteraction Interaction { get; private set; }
        public CloudEmbedder Embedder { get; private set; }
        public ChatService Chat { get; private set; }
        public IClock Clock { get; private set; }

        // ── v1.1 / v1.2 新增 ─────────────────────────────────────────────
        public BackupService Backup { get; private set; }
        public StorageStatsService StorageStats { get; private set; }
        public TokenTechLog TechLog { get; private set; }
        public TranslationService Translation { get; private set; }
        public UwpSpeechSynthesizer Speech { get; private set; }
        public TtsService Tts { get; private set; }
        public PyUwpScriptRuntime ScriptRuntime { get; private set; }
        public IScriptHostBridge ScriptHost { get; private set; }

        public bool Initialized { get; private set; }

        private AppServices() { }

        public static async Task<AppServices> CreateAsync()
        {
            var services = new AppServices();
            services.Clock = SystemClock.Instance;
            services.Files = await UwpFileStore.CreateAsync();

            services.Log = new DiagnosticLog(services.Files, services.Clock);
            services.Secrets = new UwpSecretStore();
            services.Http = new HttpTransport();
            services.Device = new UwpDeviceBridge(services.Files);
            services.Interaction = new UwpInteraction();
            services.Registry = ToolRegistry.CreateDefault();

            services.Workspace = new Workspace(services.Files, services.Secrets, services.Log, services.Clock);
            await services.Workspace.InitializeAsync();
            services.Operations = new WorkspaceOperations(services.Workspace, services.Files, services.Log, services.Clock);

            services.Embedder = new CloudEmbedder(
                services.Http,
                services.Log,
                delegate { return services.Workspace.DefaultApiProfile(); },
                delegate (ApiProfile api) { return services.Workspace.GetApiKey(api); });

            // v1.1：备份、存储统计
            services.Backup = new BackupService(services.Files, services.Log, services.Clock);
            services.StorageStats = new StorageStatsService(services.Files, services.Log);

            // v1.2：用量日志、翻译、朗读
            services.TechLog = new TokenTechLog(services.Files, services.Log);
            services.Translation = new TranslationService(services.Http, services.Log);
            services.Speech = new UwpSpeechSynthesizer(services.Log);
            services.Tts = new TtsService(services.Speech, services.Log, services.Http, services.Speech);

            // v1.2：脚本运行时。runtime_root 指向包目录 —— PyUwp 的 Lib\ 与
            // python-uwp.dll 一起放在包的根下，所以直接给安装目录。
            // 路径解析器给的是**真实文件系统路径**（不是 ms-appdata URI），
            // 否则 Python 的 os.chdir 会失败。
            services.ScriptRuntime = new PyUwpScriptRuntime(
                delegate (string relative) { return services.Files.GetNativePath(relative); },
                services.Log);
            // localSite 必须是真实可写目录：传 null 时 PyUwp 的外部导入器
            // （_PyImport_InitExternal）初始化会失败，报
            // "initialization failed: func=_PyImport_InitExternal"。
            // LocalFolder 对 UWP 应用始终可写，正合适。
            services.ScriptRuntime.ConfigurePaths(
                Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                Windows.Storage.ApplicationData.Current.LocalFolder.Path);
            services.ScriptHost = new ScriptHostBridge(null, services.Clock, null);

            services.Chat = new ChatService(
                services.Workspace,
                services.Files,
                services.Log,
                services.Clock,
                services.Http,
                services.Device,
                services.Embedder,
                services.Interaction,
                services.Registry);

            services.Chat.ToolStoreFactory = delegate (Profile profile) {
                var mounts = new System.Collections.Generic.List<FileMount>();
                foreach (var configuration in services.Workspace.Settings.AuthorizedFolders) {
                    if (configuration.ProfileId.Length > 0 && configuration.ProfileId != profile.Id) continue;
                    var selected = configuration;
                    mounts.Add(new FileMount { Configuration = selected, Open = async delegate { var folder = await Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.GetFolderAsync(selected.Token); return (IFileStore)UwpFileStore.FromFolder(folder); } });
                }
                return new MountedFileStore(services.Files, WorkspaceLayout.FilesDir(profile.Slug), mounts);
            };
            services.Chat.TechLog = services.TechLog;
            services.Chat.ScriptRuntime = services.ScriptRuntime;
            services.Chat.ScriptHost = services.ScriptHost;

            services.Initialized = true;
            Current = services;
            return services;
        }

        /// <summary>切换档案：重建记忆池并绑定到 ChatService。</summary>
        public async Task<bool> OpenProfileAsync(Profile profile)
        {
            var opened = await Chat.OpenProfileAsync(profile);
            if (opened && profile != null)
            {
                // 宿主桥要跟着档案走：记忆检索是档案级的
                ScriptHost = new ScriptHostBridge(Chat.Memory, Clock, profile);
                Chat.ScriptHost = ScriptHost;
            }
            return opened;
        }

        public async Task<AppServices> RelocateAsync(Windows.Storage.StorageFolder parent, System.Threading.CancellationToken ct)
        {
            if (parent == null) throw new ArgumentNullException("parent");
            var folder = await parent.CreateFolderAsync(UwpFileStore.WorkspaceFolderName, Windows.Storage.CreationCollisionOption.GenerateUniqueName);
            await Files.CopyToAsync(folder, ct);
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            var old = settings.Values["workspaceToken"] as string;
            var access = Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList;
            var token = access.Add(folder, folder.Path);
            settings.Values["workspaceToken"] = token;
            try { return await CreateAsync(); }
            catch {
                if (old == null) settings.Values.Remove("workspaceToken"); else settings.Values["workspaceToken"] = old;
                access.Remove(token);
                throw;
            }
        }

        public void Shutdown()
        {
            try
            {
                if (Speech != null) Speech.Dispose();
            }
            catch (Exception) { }

            try
            {
                if (ScriptRuntime != null) ScriptRuntime.Dispose();
            }
            catch (Exception) { }

            try
            {
                if (Http != null) Http.Dispose();
            }
            catch (Exception) { }
        }
    }
}
