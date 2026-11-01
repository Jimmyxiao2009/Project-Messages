// Platform/UwpFileStore.cs —— 工作区文件存储（UWP 实现）
//
// 根目录固定在 LocalFolder\workspace。Core 里所有路径都是相对路径，
// 这里的唯一职责就是把它翻译成 StorageFolder / StorageFile 操作。
//
// 注意两个 UWP 特有的坑：
//   1) 目录 API 是逐级的，没有"一次创建多级目录"，要按段走；
//   2) NavigateAsync 是我们自己的 async 方法（返回 Task），不能再接 .AsTask()；
//      只有 WinRT 的 IAsyncOperation 才需要 .AsTask()。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class UwpFileStore : IFileStore
    {
        public const string WorkspaceFolderName = "workspace";

        private readonly StorageFolder _root;

        private UwpFileStore(StorageFolder root)
        {
            _root = root;
        }

        public static UwpFileStore FromFolder(StorageFolder folder) { return new UwpFileStore(folder); }
        public static async Task<UwpFileStore> CreateAsync()
        {
            var token = ApplicationData.Current.LocalSettings.Values["workspaceToken"] as string;
            if (!string.IsNullOrEmpty(token)) {
                var folder = await Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                return new UwpFileStore(folder);
            }
            var root = await ApplicationData.Current.LocalFolder
                .CreateFolderAsync(WorkspaceFolderName, CreationCollisionOption.OpenIfExists);
            return new UwpFileStore(root);
        }

        // ── 路径解析 ─────────────────────────────────────────────────────

        private static async Task<StorageFolder> NavigateAsync(StorageFolder root, string relativeDir, bool create)
        {
            if (string.IsNullOrEmpty(relativeDir)) return root;

            var folder = root;
            foreach (var segment in relativeDir.Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;

                folder = create
                    ? await folder.CreateFolderAsync(segment, CreationCollisionOption.OpenIfExists)
                    : await folder.GetFolderAsync(segment);
            }
            return folder;
        }

        private async Task<StorageFile> FindFileAsync(string relativePath, bool createIfMissing)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                throw new ArgumentException("路径为空", "relativePath");
            }

            string dir, name;
            WorkspaceLayout.Split(relativePath, out dir, out name);

            var folder = await NavigateAsync(_root, dir, createIfMissing).ConfigureAwait(false);
            if (createIfMissing)
            {
                return await folder.CreateFileAsync(name, CreationCollisionOption.OpenIfExists)
                    .AsTask().ConfigureAwait(false);
            }
            return await folder.GetFileAsync(name).AsTask().ConfigureAwait(false);
        }

        // ── IFileStore ───────────────────────────────────────────────────

        public async Task<bool> ExistsAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            try
            {
                await FindFileAsync(relativePath, false).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<string> ReadTextAsync(string relativePath)
        {
            var file = await FindFileAsync(relativePath, false).ConfigureAwait(false);
            return await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
        }

        public async Task WriteTextAsync(string relativePath, string content)
        {
            string dir, name;
            WorkspaceLayout.Split(relativePath, out dir, out name);

            var folder = await NavigateAsync(_root, dir, true).ConfigureAwait(false);
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting)
                .AsTask().ConfigureAwait(false);
            await FileIO.WriteTextAsync(file, content ?? "").AsTask().ConfigureAwait(false);
        }

        public async Task<byte[]> ReadBytesAsync(string relativePath)
        {
            var file = await FindFileAsync(relativePath, false).ConfigureAwait(false);
            var buffer = await FileIO.ReadBufferAsync(file).AsTask().ConfigureAwait(false);

            using (var reader = DataReader.FromBuffer(buffer))
            {
                var bytes = new byte[buffer.Length];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        public async Task WriteBytesAsync(string relativePath, byte[] data)
        {
            string dir, name;
            WorkspaceLayout.Split(relativePath, out dir, out name);

            var folder = await NavigateAsync(_root, dir, true).ConfigureAwait(false);
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting)
                .AsTask().ConfigureAwait(false);
            await FileIO.WriteBytesAsync(file, data ?? new byte[0]).AsTask().ConfigureAwait(false);
        }

        public async Task DeleteAsync(string relativePath)
        {
            try
            {
                var file = await FindFileAsync(relativePath, false).ConfigureAwait(false);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete).AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 删不存在的文件不算错误
            }
        }

        public async Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, bool recursive)
        {
            var result = new List<string>();
            StorageFolder folder;

            try
            {
                folder = await NavigateAsync(_root, relativeDir, false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return result;
            }

            await CollectFilesAsync(folder, relativeDir, recursive, result).ConfigureAwait(false);
            return result;
        }

        private static async Task CollectFilesAsync(
            StorageFolder folder, string prefix, bool recursive, List<string> into)
        {
            IReadOnlyList<StorageFile> files;
            try
            {
                files = await folder.GetFilesAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var file in files)
            {
                into.Add(Join(prefix, file.Name));
            }

            if (!recursive) return;

            IReadOnlyList<StorageFolder> children;
            try
            {
                children = await folder.GetFoldersAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var child in children)
            {
                await CollectFilesAsync(child, Join(prefix, child.Name), true, into).ConfigureAwait(false);
            }
        }

        public async Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDir)
        {
            var result = new List<string>();
            StorageFolder folder;

            try
            {
                folder = await NavigateAsync(_root, relativeDir, false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return result;
            }

            IReadOnlyList<StorageFolder> children;
            try
            {
                children = await folder.GetFoldersAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return result;
            }

            foreach (var child in children)
            {
                result.Add(Join(relativeDir, child.Name));
            }
            return result;
        }

        public async Task EnsureDirectoryAsync(string relativeDir)
        {
            if (string.IsNullOrEmpty(relativeDir)) return;
            await NavigateAsync(_root, relativeDir, true).ConfigureAwait(false);
        }

        /// <summary>移动并覆盖目标。原子写入依赖它做"临时文件替换成正式文件"。</summary>
        public async Task MoveAsync(string fromRelative, string toRelative)
        {
            string toDir, toName;
            WorkspaceLayout.Split(toRelative, out toDir, out toName);

            var source = await FindFileAsync(fromRelative, false).ConfigureAwait(false);
            var destination = await NavigateAsync(_root, toDir, true).ConfigureAwait(false);

            await source.MoveAsync(destination, toName, NameCollisionOption.ReplaceExisting)
                .AsTask().ConfigureAwait(false);
        }

        public string GetAbsolutePath(string relativePath)
        {
            return _root.Path == System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, WorkspaceFolderName)
                ? "ms-appdata:///local/" + WorkspaceFolderName + "/" + (relativePath ?? "").Replace((char)92, '/')
                : new Uri(GetNativePath(relativePath)).AbsoluteUri;
        }

        /// <summary>
        /// 真实文件系统路径（形如 C:\Users\...\Packages\&lt;包&gt;\LocalState\workspace\...）。
        ///
        /// 为什么不能复用 GetAbsolutePath：那个返回的是 ms-appdata:/// URI，
        /// 只有 WinRT 的 StorageFile 能认。Python 的 os.chdir、以及任何原生代码
        /// 都只吃真实路径 —— 拿 URI 过去会直接失败。
        /// </summary>
        public string GetNativePath(string relativePath)
        {
            var root = ApplicationData.Current.LocalFolder.Path;
            var full = _root.Path;

            if (string.IsNullOrEmpty(relativePath)) return full;

            var relative = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)
                                       .Replace('\\', System.IO.Path.DirectorySeparatorChar)
                                       .TrimStart(System.IO.Path.DirectorySeparatorChar);

            return System.IO.Path.Combine(full, relative);
        }

        public async Task CopyToAsync(StorageFolder destination, System.Threading.CancellationToken ct)
        {
            var target = new UwpFileStore(destination);
            foreach (var path in await ListFilesAsync("", true))
            {
                ct.ThrowIfCancellationRequested();
                if (path.EndsWith(AtomicFile.TempSuffix, StringComparison.Ordinal)) continue;
                await target.WriteBytesAsync(path, await ReadBytesAsync(path));
            }
        }

        // ── 备份与导出用的文件选择 ───────────────────────────────────────

        /// <summary>
        /// 让用户选一个保存位置并创建文件。取消返回 null。
        /// 用 FileSavePicker 而不是直接写 LocalState：备份要能拿出去，否则换机就白备了。
        /// </summary>
        public async Task<StorageFile> CreateExportFileAsync(string suggestedName)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = suggestedName;
                picker.FileTypeChoices.Add("言枢备份", new List<string> { BackupNaming.Extension });

                return await picker.PickSaveFileAsync();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>让用户挑一个备份文件。取消返回 null。</summary>
        public async Task<StorageFile> PickBackupFileAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(BackupNaming.Extension);
                picker.FileTypeFilter.Add(".zip");

                return await picker.PickSingleFileAsync();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 取 StorageFile 句柄，给需要原生 StorageFile 的组件用
        /// （例如 Windows.Data.Pdf 只能从 StorageFile 加载）。
        /// 这个方法刻意不在 IFileStore 里 —— Core 不该认识 StorageFile。
        /// </summary>
        public async Task<StorageFile> GetFileAsync(string relativePath)
        {
            try
            {
                return await FindFileAsync(relativePath, false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>取 StorageFolder 句柄（导出、备份时用）。</summary>
        public async Task<StorageFolder> GetFolderAsync(string relativeDir)
        {
            try
            {
                return await NavigateAsync(_root, relativeDir, false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>工作区根目录（备份时用来打包）。</summary>
        public StorageFolder Root { get { return _root; } }

        private static string Join(string prefix, string name)
        {
            if (string.IsNullOrEmpty(prefix)) return name;
            return prefix.TrimEnd('/') + "/" + name;
        }
    }
}
