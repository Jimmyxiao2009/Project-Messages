// tests/CoreTests/Infra.cs —— 测试基础设施
//
// 不引入 xunit / nunit：这些包要么需要联网还原，要么在 UWP 侧的等价性没保证。
// 一个 40 行的断言器 + 内存文件存储就够了，而且能直接跑在 dotnet run 下。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public sealed class AssertionFailedException : Exception
    {
        public AssertionFailedException(string message) : base(message) { }
    }

    public static class Check
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new AssertionFailedException("期望为真: " + what);
        }

        public static void False(bool condition, string what)
        {
            if (condition) throw new AssertionFailedException("期望为假: " + what);
        }

        public static void Equal(object expected, object actual, string what)
        {
            if (!Equals(expected, actual))
            {
                throw new AssertionFailedException(string.Format(
                    CultureInfo.InvariantCulture, "{0}: 期望 <{1}> 实际 <{2}>", what, expected, actual));
            }
        }

        public static void EqualInt(int expected, int actual, string what)
        {
            if (expected != actual)
            {
                throw new AssertionFailedException(string.Format(
                    CultureInfo.InvariantCulture, "{0}: 期望 {1} 实际 {2}", what, expected, actual));
            }
        }

        public static void Contains(string haystack, string needle, string what)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
            {
                throw new AssertionFailedException(string.Format(
                    CultureInfo.InvariantCulture, "{0}: 未找到 <{1}>；实际内容 <{2}>", what, needle, haystack));
            }
        }

        public static void NotContains(string haystack, string needle, string what)
        {
            if (haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
            {
                throw new AssertionFailedException(string.Format(
                    CultureInfo.InvariantCulture, "{0}: 不该出现 <{1}>；实际内容 <{2}>", what, needle, haystack));
            }
        }

        public static void InRange(double low, double high, double actual, string what)
        {
            if (actual < low || actual > high)
            {
                throw new AssertionFailedException(string.Format(
                    CultureInfo.InvariantCulture, "{0}: {1} 不在 [{2}, {3}] 内", what, actual, low, high));
            }
        }

        public static T Throws<T>(Action action, string what) where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                return ex;
            }
            catch (Exception other)
            {
                throw new AssertionFailedException(string.Format(
                    CultureInfo.InvariantCulture, "{0}: 抛出了 {1} 而不是 {2}", what, other.GetType().Name, typeof(T).Name));
            }
            throw new AssertionFailedException(what + ": 没有抛出异常");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 内存文件存储：让存储层逻辑可以脱离 UWP 测试
    // ─────────────────────────────────────────────────────────────────────

    public sealed class InMemoryFileStore : IFileStore
    {
        // 只存字节：文本按 UTF-8 编码进来、解码出去。
        // 早先文本与二进制分两个字典存，结果是 ListFilesAsync 只列文本、
        // Peek 只读文本 —— "二进制文件没被备份" 这类 bug 在测试里完全隐形。
        // 与真实文件系统一样只保留一份数据，才是可信的替身。
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public int WriteCount { get; private set; }
        public int MoveCount { get; private set; }

        public Task<bool> ExistsAsync(string relativePath)
        {
            lock (_gate) return Task.FromResult(_files.ContainsKey(Norm(relativePath)));
        }

        public Task<string> ReadTextAsync(string relativePath)
        {
            lock (_gate)
            {
                byte[] data;
                if (!_files.TryGetValue(Norm(relativePath), out data))
                {
                    throw new System.IO.FileNotFoundException(relativePath);
                }
                return Task.FromResult(Encoding.UTF8.GetString(data, 0, data.Length));
            }
        }

        public Task WriteTextAsync(string relativePath, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
            lock (_gate)
            {
                _files[Norm(relativePath)] = bytes;
                WriteCount++;
            }
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadBytesAsync(string relativePath)
        {
            lock (_gate)
            {
                byte[] data;
                if (!_files.TryGetValue(Norm(relativePath), out data))
                {
                    throw new System.IO.FileNotFoundException(relativePath);
                }
                return Task.FromResult(data);
            }
        }

        public Task WriteBytesAsync(string relativePath, byte[] data)
        {
            lock (_gate)
            {
                _files[Norm(relativePath)] = data ?? new byte[0];
                WriteCount++;
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string relativePath)
        {
            lock (_gate) _files.Remove(Norm(relativePath));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, bool recursive)
        {
            var prefix = string.IsNullOrEmpty(relativeDir) ? "" : Norm(relativeDir) + "/";
            List<string> result;
            lock (_gate)
            {
                result = _files.Keys
                    .Where(delegate (string k)
                    {
                        if (!k.StartsWith(prefix, StringComparison.Ordinal)) return false;
                        if (recursive) return true;
                        var rest = k.Substring(prefix.Length);
                        return rest.IndexOf('/') < 0;
                    })
                    .ToList();
            }
            IReadOnlyList<string> readOnly = result;
            return Task.FromResult(readOnly);
        }

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDir)
        {
            var prefix = string.IsNullOrEmpty(relativeDir) ? "" : Norm(relativeDir) + "/";
            var dirs = new HashSet<string>(StringComparer.Ordinal);
            lock (_gate)
            {
                foreach (var key in _files.Keys)
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = key.Substring(prefix.Length);
                    int slash = rest.IndexOf('/');
                    if (slash > 0) dirs.Add(prefix + rest.Substring(0, slash));
                }
            }
            IReadOnlyList<string> readOnly = dirs.ToList();
            return Task.FromResult(readOnly);
        }

        public Task EnsureDirectoryAsync(string relativeDir)
        {
            return Task.CompletedTask;
        }

        public Task MoveAsync(string fromRelative, string toRelative)
        {
            lock (_gate)
            {
                byte[] data;
                if (_files.TryGetValue(Norm(fromRelative), out data))
                {
                    _files.Remove(Norm(fromRelative));
                    _files[Norm(toRelative)] = data;      // 覆盖目标
                    MoveCount++;
                }
            }
            return Task.CompletedTask;
        }

        public string GetAbsolutePath(string relativePath)
        {
            return "mem:/" + Norm(relativePath);
        }

        /// <summary>便于断言：按 UTF-8 解出文本。文件不存在返回 null。</summary>
        public string Peek(string relativePath)
        {
            lock (_gate)
            {
                byte[] data;
                if (!_files.TryGetValue(Norm(relativePath), out data)) return null;
                return Encoding.UTF8.GetString(data, 0, data.Length);
            }
        }

        public byte[] PeekBytes(string relativePath)
        {
            lock (_gate)
            {
                byte[] data;
                return _files.TryGetValue(Norm(relativePath), out data) ? data : null;
            }
        }

        public int Count
        {
            get { lock (_gate) return _files.Count; }
        }

        private static string Norm(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 假交互：可控的审批与追问
    // ─────────────────────────────────────────────────────────────────────

    public sealed class FakeInteraction : IUserInteraction
    {
        public bool ApproveResult = true;
        public int ConfirmCalls;
        public List<ToolPermissionRequest> Requests = new List<ToolPermissionRequest>();
        public List<AskAnswer> Answers = new List<AskAnswer>();

        public Task<bool> ConfirmPermissionAsync(ToolPermissionRequest request, CancellationToken ct)
        {
            ConfirmCalls++;
            Requests.Add(request);
            return Task.FromResult(ApproveResult);
        }

        public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
        {
            IReadOnlyList<AskAnswer> result = Answers;
            return Task.FromResult(result);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 假 HTTP：按 URL 命中返回预置响应
    // ─────────────────────────────────────────────────────────────────────

    public sealed class FakeHttpTransport : IHttpTransport
    {
        public readonly List<HttpRequestSpec> Requests = new List<HttpRequestSpec>();
        public string NextBody = "{}";
        public int NextStatus = 200;
        public List<string> NextStreamLines = new List<string>();
        public Exception ThrowOnSend;

        public Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken ct)
        {
            Requests.Add(request);
            if (ThrowOnSend != null) throw ThrowOnSend;
            return Task.FromResult(new HttpResponseSpec
            {
                StatusCode = NextStatus,
                Body = NextBody,
                ContentType = "application/json"
            });
        }

        public Task StreamLinesAsync(HttpRequestSpec request, Func<string, bool> onLine, CancellationToken ct)
        {
            Requests.Add(request);
            if (ThrowOnSend != null) throw ThrowOnSend;
            if (NextStatus != 200)
            {
                throw new ProviderNetworkException("fake_" + NextStatus.ToString(CultureInfo.InvariantCulture));
            }
            foreach (var line in NextStreamLines)
            {
                ct.ThrowIfCancellationRequested();
                if (!onLine(line)) break;
            }
            onLine(null);
            return Task.CompletedTask;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 用例注册与运行
    // ─────────────────────────────────────────────────────────────────────

    public sealed class TestCase
    {
        public string Suite;
        public string Name;
        public Action Body;
    }

    public static class Runner
    {
        private static readonly List<TestCase> Cases = new List<TestCase>();

        public static void Add(string suite, string name, Action body)
        {
            Cases.Add(new TestCase { Suite = suite, Name = name, Body = body });
        }

        public static int RunAll(string filter)
        {
            int passed = 0;
            var failures = new List<string>();
            string currentSuite = null;

            foreach (var test in Cases)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    string.IsNullOrEmpty(test.Suite))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(filter) &&
                    test.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    test.Suite.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (currentSuite != test.Suite)
                {
                    currentSuite = test.Suite;
                    Console.WriteLine();
                    Console.WriteLine("── " + currentSuite + " ──");
                }

                try
                {
                    test.Body();
                    passed++;
                    Console.WriteLine("  PASS  " + test.Name);
                }
                catch (AssertionFailedException ex)
                {
                    failures.Add(test.Suite + " / " + test.Name + " → " + ex.Message);
                    Console.WriteLine("  FAIL  " + test.Name);
                    Console.WriteLine("        " + ex.Message);
                }
                catch (Exception ex)
                {
                    failures.Add(test.Suite + " / " + test.Name + " → " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine("  FAIL  " + test.Name + "  (异常)");
                    Console.WriteLine("        " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════");
            Console.WriteLine("通过 " + passed + " / " + (passed + failures.Count));
            if (failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("失败明细：");
                foreach (var f in failures) Console.WriteLine("  • " + f);
            }
            Console.WriteLine("════════════════════════════════════════");
            return failures.Count;
        }
    }
}
