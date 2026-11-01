// tools/SeedData/FileSystemStore.cs —— 真实文件系统上的 IFileStore
//
// Core 只有接口；UWP 实现要 WinRT，测试实现是内存的。
// 这个实现给"在桌面上直接往应用的 LocalState 里写数据"用。
//
// 路径纪律与 UwpFileStore 一致：所有相对路径都夹在根目录之内，
// 上跳（..）一律拒绝 —— 这个工具会拿到用户给的绝对路径，越界写文件是真实的危险。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tools
{
    public sealed class FileSystemStore : IFileStore
    {
        private readonly string _root;

        public FileSystemStore(string root)
        {
            _root = Path.GetFullPath(root);
        }

        public string Root { get { return _root; } }

        public Task<bool> ExistsAsync(string relativePath)
        {
            return Task.FromResult(File.Exists(Resolve(relativePath)));
        }

        public Task<string> ReadTextAsync(string relativePath)
        {
            var path = Resolve(relativePath);
            if (!File.Exists(path)) throw new FileNotFoundException(relativePath);
            return Task.FromResult(File.ReadAllText(path, Encoding.UTF8));
        }

        public Task WriteTextAsync(string relativePath, string content)
        {
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content ?? "", new UTF8Encoding(false));
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadBytesAsync(string relativePath)
        {
            var path = Resolve(relativePath);
            if (!File.Exists(path)) throw new FileNotFoundException(relativePath);
            return Task.FromResult(File.ReadAllBytes(path));
        }

        public Task WriteBytesAsync(string relativePath, byte[] data)
        {
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data ?? new byte[0]);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string relativePath)
        {
            var path = Resolve(relativePath);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, bool recursive)
        {
            var result = new List<string>();
            var dir = Resolve(relativeDir);
            if (!Directory.Exists(dir))
            {
                IReadOnlyList<string> empty = result;
                return Task.FromResult(empty);
            }

            var prefix = string.IsNullOrEmpty(relativeDir) ? "" : Normalize(relativeDir) + "/";
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            foreach (var file in Directory.GetFiles(dir, "*", option))
            {
                result.Add(prefix + Path.GetRelativePath(dir, file).Replace('\\', '/'));
            }

            IReadOnlyList<string> readOnly = result;
            return Task.FromResult(readOnly);
        }

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDir)
        {
            var result = new List<string>();
            var dir = Resolve(relativeDir);
            if (!Directory.Exists(dir))
            {
                IReadOnlyList<string> empty = result;
                return Task.FromResult(empty);
            }

            var prefix = string.IsNullOrEmpty(relativeDir) ? "" : Normalize(relativeDir) + "/";
            foreach (var sub in Directory.GetDirectories(dir))
            {
                result.Add(prefix + Path.GetFileName(sub));
            }

            IReadOnlyList<string> readOnly = result;
            return Task.FromResult(readOnly);
        }

        public Task EnsureDirectoryAsync(string relativeDir)
        {
            if (!string.IsNullOrEmpty(relativeDir)) Directory.CreateDirectory(Resolve(relativeDir));
            return Task.CompletedTask;
        }

        public Task MoveAsync(string fromRelative, string toRelative)
        {
            var from = Resolve(fromRelative);
            var to = Resolve(toRelative);

            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (File.Exists(to)) File.Delete(to);
            if (File.Exists(from)) File.Move(from, to);

            return Task.CompletedTask;
        }

        public string GetAbsolutePath(string relativePath)
        {
            return Resolve(relativePath);
        }

        /// <summary>
        /// 把相对路径解析成根目录下的绝对路径，并确保结果没有跑出根目录。
        /// 用 GetFullPath 之后比对前缀，能挡住 "a/../../b" 与绝对路径注入两种写法。
        /// </summary>
        private string Resolve(string relativePath)
        {
            var normalized = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0) return _root;

            var combined = Path.GetFullPath(Path.Combine(_root, normalized));

            if (!combined.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("路径越界：" + relativePath);
            }

            return combined;
        }

        private static string Normalize(string path)
        {
            return (path ?? "").Replace('\\', '/').TrimStart('/');
        }
    }
}
