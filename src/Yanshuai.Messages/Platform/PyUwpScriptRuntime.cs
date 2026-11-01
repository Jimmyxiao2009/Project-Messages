// Platform/PyUwpScriptRuntime.cs —— 真 Python 运行时（PyUwp）
//
// 对接的是 PyUwp.Runtime 0.2.0 的原生 ABI（见 PyUwpDriver.h）。三条硬约束决定了这个类的形状：
//
//   1) **必须在同一个专用线程上调用**。ABI 明确写着 "Create one dedicated worker thread
//      and call every function below on it"，从别的线程调会返回 PYUWP_E_WRONG_THREAD。
//      所以这里有一个常驻引擎线程 + 串行队列，所有调用都 post 到那个线程。
//
//   2) **输出只有一路**。LastOutput 把 stdout 与 stderr 合并成一根字符串，
//      没法区分"结果"和"报错"。而区分这两者恰恰是模型纠错的关键。
//      解法：在 Python 侧把 sys.stdout / sys.stderr 换成缓冲区，
//      结束时用 base64 编码、带分隔符打到真正的 stdout 上，C# 侧再拆开。
//      用 base64 而不是原样拼接，是为了彻底避免分隔符碰撞与编码问题。
//
//   3) **没有取消机制**。RunUtf8 是同步阻塞的，ABI 没有提供中断入口。
//      所以脚本一旦死循环，引擎线程就真的回不来了。这里不假装能杀掉它：
//      超时后把运行时标记为"已卡死"，后续调用直接给出明确错误，
//      而不是让用户等一个永远不会回来的调用。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class PyUwpScriptRuntime : IScriptRuntime, IDisposable
    {
        // ── 原生 ABI ─────────────────────────────────────────────────────
        private const string Dll = "python-uwp.dll";

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int PyUwpRuntime_Initialize(string runtimeRoot, string localSite);

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int PyUwpRuntime_RunUtf8(byte[] source);

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int PyUwpRuntime_Finalize();

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern IntPtr PyUwpRuntime_LastError();

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern IntPtr PyUwpRuntime_LastOutput();

        [System.Runtime.InteropServices.DllImport(Dll, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int PyUwpSelfTest();

        private const int E_WRONG_THREAD = -2;
        private const int E_INITIALIZATION = -1000;
        private const int E_EXECUTION = -2000;

        // ── 引擎线程 ─────────────────────────────────────────────────────
        private readonly BlockingQueue<WorkItem> _queue = new BlockingQueue<WorkItem>();
        private readonly Func<string, string> _pathResolver;
        private readonly ILogger _log;

        private Task _engine;
        private readonly object _gate = new object();

        private bool _initialized;
        private bool _initFailed;
        private string _initError = "";
        private bool _wedged;              // 上一次执行超时 → 引擎线程回不来了
        private bool _disposed;
        private int _runs;

        private string _runtimeRoot;
        private string _localSite;

        /// <summary>
        /// pathResolver 要把工作区相对路径转成**真实文件系统路径**。
        /// 不能用 IFileStore.GetAbsolutePath —— 那里返回的是 ms-appdata:/// URI，
        /// 只有 WinRT 认，Python 的 os.chdir 会直接失败。
        /// </summary>
        public PyUwpScriptRuntime(Func<string, string> pathResolver, ILogger log)
        {
            _pathResolver = pathResolver;
            _log = log ?? NullLogger.Instance;
        }

        public string Name { get { return "python-uwp"; } }
        public bool IsReal { get { return true; } }

        public string Version
        {
            get
            {
                if (_wedged) return "Python (PyUwp) —— 已卡死，需要重启应用";
                if (_initFailed) return "Python (PyUwp) —— 初始化失败";
                if (_initialized) return "Python (PyUwp) 运行时";
                return "Python (PyUwp) 运行时（未初始化）";
            }
        }

        /// <summary>
        /// 只回答"这个设备上有没有这个运行时"（DLL 能不能加载）。
        /// **不把 init 失败算成不可用** —— 初始化失败是另一回事，
        /// 真实错误由 RunAsync 原样带回（之前在这里返回 false，
        /// 调用方就只会看到一句"没有可用的脚本运行时"，真正的 init 错误被吞了）。
        /// </summary>
        public bool IsAvailable
        {
            get
            {
                if (_disposed) return false;
                if (_wedged) return false;
                return true;
            }
        }

        /// <summary>上次初始化/执行的错误，供设置页显示。</summary>
        public string LastError { get { return _initError; } }

        /// <summary>脚本文件根目录（用于把相对路径转成绝对路径给 Python）。</summary>
        public void ConfigurePaths(string runtimeRoot, string localSite)
        {
            _runtimeRoot = runtimeRoot;
            _localSite = localSite;
        }

        // ═════════════════════════════════════════════════════════════════
        // 执行
        // ═════════════════════════════════════════════════════════════════

        public async Task<ScriptResult> RunAsync(ScriptRequest request, CancellationToken ct)
        {
            var swRun = System.Diagnostics.Stopwatch.StartNew();
            if (_disposed) return ScriptResult.Fail("disposed", "运行时已释放。");

            if (_wedged)
            {
                return ScriptResult.Fail("engine_wedged",
                    "上一次脚本超时后 Python 引擎没有恢复，需要重启应用才能再次执行脚本。");
            }

            // 调试日志：每次执行都记一行"现场"——工作目录、脚本长度、超时设定。
            // 真机上"写文件超时"这类问题，没有这行信息根本没法判断是路径错了、
            // 脚本卡在原生 IO 上、还是引擎线程已经死了。
            var runDir = "";
            try { runDir = _pathResolver != null && !string.IsNullOrEmpty(request.WorkingDirectory) ? _pathResolver(request.WorkingDirectory) : ""; }
            catch (Exception) { runDir = ""; }
            _log.Log(LogLevel.Info, "python",
                "run#begin src=" + (request.Source != null ? request.Source.Length : 0)
                + "B timeout=" + Math.Max(1, request.TimeoutSeconds) + "s dir=" + (runDir.Length == 0 ? "(none)" : runDir)
                + " dirExists=" + (runDir.Length == 0 ? "?" : System.IO.Directory.Exists(runDir).ToString()));

            // 初始化失败过：直接带回真实错误，不要再排队。
            // （旧实现这里不拦，队列里的活儿没人处理，调用方会一直等到超时。）
            if (_initFailed)
            {
                return ScriptResult.Fail("init", "Python 运行时不可用：" + _initError);
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Source))
            {
                return ScriptResult.Fail("empty_script", "脚本内容为空。");
            }

            EnsureEngine();

            // 工作目录：请求里是档案内相对路径，Python 需要真实路径
            var workingDirectory = "";
            if (!string.IsNullOrEmpty(request.WorkingDirectory) && _pathResolver != null)
            {
                try { workingDirectory = _pathResolver(request.WorkingDirectory); }
                catch (Exception) { workingDirectory = ""; }
            }

            var payload = PyUwpProtocol.BuildPayload(request.Source, workingDirectory);

            var item = new WorkItem
            {
                Source = payload,
                TimeoutMs = Math.Max(1, request.TimeoutSeconds) * 1000
            };

            _queue.Enqueue(item);

            var timeout = Task.Delay(item.TimeoutMs, ct);
            var completed = await Task.WhenAny(item.Completion.Task, timeout).ConfigureAwait(false);

            if (completed != item.Completion.Task)
            {
                // 超时。引擎线程还在 RunUtf8 里，没法中断 —— 如实标记，不再假装能继续用
                MarkWedged();
                _log.Log(LogLevel.Error, "python",
                    "run#timeout after " + item.TimeoutMs + "ms（引擎线程仍停在原生调用里，已标记 wedge；"
                    + "若脚本在做文件写入，优先检查工作目录是否可写： " + (runDir.Length == 0 ? "(未解析)" : runDir) + "）");
                var stopwatchStuck = new ScriptResult
                {
                    Ok = false,
                    TimedOut = true,
                    ExitCode = 124,
                    DurationMs = item.TimeoutMs,
                    StdErr = "执行超时。PyUwp 运行时没有中断接口，引擎线程仍停在这次执行里。"
                           + "需要重启应用才能再次执行脚本。"
                };
                return stopwatchStuck;
            }

            var result = await item.Completion.Task.ConfigureAwait(false);
            if (result != null) ApplyOutputLimits(result, request.MaxOutputChars);
            _log.Log(LogLevel.Info, "python",
                "run#end ok=" + (result != null && result.Ok) + " exit=" + (result != null ? result.ExitCode : -1)
                + " ms=" + swRun.ElapsedMilliseconds + " out=" + (result != null && result.StdOut != null ? result.StdOut.Length : 0)
                + "B err=" + (result != null && result.StdErr != null ? result.StdErr.Length : 0) + "B");
            return result;
        }

        private void ApplyOutputLimits(ScriptResult result, int maxChars)
        {
            int limit = maxChars > 0 ? maxChars : 20000;

            if (result.StdOut != null && result.StdOut.Length > limit)
            {
                result.StdOut = result.StdOut.Substring(0, limit);
                result.OutputTruncated = true;
            }
            if (result.StdErr != null && result.StdErr.Length > limit)
            {
                result.StdErr = result.StdErr.Substring(0, limit);
                result.OutputTruncated = true;
            }
        }

        private void MarkWedged()
        {
            lock (_gate)
            {
                _wedged = true;
                _initError = "脚本超时后引擎线程未返回。";
            }
            _log.Log(LogLevel.Error, "python", "引擎线程在脚本超时后未返回，已标记运行时不可用");
        }

        // ═════════════════════════════════════════════════════════════════
        // 引擎线程
        // ═════════════════════════════════════════════════════════════════

        private void EnsureEngine()
        {
            lock (_gate)
            {
                if (_engine != null) return;

                // 用 LongRunning 而不是 new Thread()：UWP 的 API 面不提供
                // System.Threading.Thread（编译期就找不到这个类型）。
                // LongRunning 会拿一个**专用**线程而不是线程池线程 ——
                // 这正是 ABI 要的："Create one dedicated worker thread and call
                // every function below on it"。整个循环都跑在那一个线程上。
                _engine = Task.Factory.StartNew(
                    EngineLoop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        private void EngineLoop()
        {
            // 初始化必须也在引擎线程上做 —— ABI 的线程约束覆盖所有函数
            if (!TryInitialize())
            {
                // 初始化失败：把所有排队的工作一次性失败掉，别让调用方一直等
                DrainWithFailure("Python 运行时初始化失败：" + _initError);
                // 引擎线程到此结束。把 _engine 放掉，下次调用重新走一遍
                // EnsureEngine → TryInitialize（会快速失败并带回真实错误），
                // 否则 _engine 一直非空，后来排的队永远没人处理。
                lock (_gate) { _engine = null; }
                return;
            }

            while (!_disposed)
            {
                WorkItem item;
                if (!_queue.TryDequeue(500, out item)) continue;
                if (item == null) break;

                item.Completion.TrySetResult(ExecuteOnEngine(item));
            }

            try { PyUwpRuntime_Finalize(); }
            catch (Exception) { }
        }

        /// <summary>
        /// 只检查 DLL 是否随包发布（不加载、不初始化）。
        /// x86/x64 桌面包里没有这个 ARM DLL，用它把"设备上没有运行时"和
        /// "初始化失败"两种情况分开 —— 后者必须原样报错，不能被吞成前者。
        /// </summary>
        private bool RuntimeDllPresent()
        {
            try
            {
                var root = _runtimeRoot;
                if (string.IsNullOrEmpty(root)) return false;
                return System.IO.File.Exists(System.IO.Path.Combine(root, Dll));
            }
            catch (Exception)
            {
                return false;
            }
        }
        private bool TryInitialize()
        {
            lock (_gate)
            {
                if (_initialized) return true;
                if (_initFailed) return false;
            }

            // 先探 DLL 在不在。**不要用 PyUwpSelfTest() 探**：那个自检在 DLL 内部是
            // 一次完整的 Py_InitializeFromConfig + Py_Finalize()，而 CPython 不支持
            // Finalize 之后再 Initialize（运行时重初始化）——真机上随后真正的初始化
            // 必然失败在 _PyImport_InitExternal（"external importer setup failed"）。
            // 自检不带 module_search_paths 所以它自己总是成功，把问题完全掩盖。
            // 改用纯文件存在性检查：不初始化，零副作用。
            if (!RuntimeDllPresent())
            {
                lock (_gate)
                {
                    _initFailed = true;
                    _initError = "找不到 " + Dll + "。PyUwp 运行时目前只提供 ARM 版本，"
                               + "x86/x64 桌面上本来就没有。";
                }
                return false;
            }

            try
            {
                var code = PyUwpRuntime_Initialize(_runtimeRoot ?? "", _localSite ?? "");
                if (code != 0)
                {
                    lock (_gate)
                    {
                        _initFailed = true;
                        _initError = ReadLastError();
                    }
                    _log.Log(LogLevel.Error, "python", "初始化失败：" + _initError);

                    // 一次性判别诊断：Initialize 失败后，再跑一次自检并记下结果。
                    //   自检也失败 → 冻结导入器表在 DLL 里就是坏的（运行时本身的问题，
                    //                与 module_search_paths / localSite 无关）；
                    //   自检成功     → 纯净配置能起、带路径的配置不能，问题在路径配置。
                    // 自检内部是 初始化+Finalize，所以只能放在这里做事后归因，不能提前。
                    try
                    {
                        int selfCode = PyUwpSelfTest();
                        _log.Log(LogLevel.Error, "python",
                            "归因自检：PyUwpSelfTest code=" + selfCode + " err=" + ReadLastError());
                    }
                    catch (Exception ex)
                    {
                        _log.Log(LogLevel.Error, "python", "归因自检抛异常：" + ex.GetType().Name);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _initFailed = true;
                    _initError = "初始化异常：" + ex.GetType().Name;
                }
                return false;
            }

            lock (_gate)
            {
                _initialized = true;
                _initError = "";
            }
            return true;
        }

        private ScriptResult ExecuteOnEngine(WorkItem item)
        {
            var result = new ScriptResult();
            var startTicks = Environment.TickCount;

            byte[] sourceBytes;
            try
            {
                // ABI 收的是 UTF-8 的 char*，末尾要有 0
                var bytes = Encoding.UTF8.GetBytes(item.Source);
                sourceBytes = new byte[bytes.Length + 1];
                Array.Copy(bytes, sourceBytes, bytes.Length);
            }
            catch (Exception ex)
            {
                return ScriptResult.Fail("encode", "脚本编码失败：" + ex.GetType().Name);
            }

            int code;
            try
            {
                code = PyUwpRuntime_RunUtf8(sourceBytes);
            }
            catch (Exception ex)
            {
                return ScriptResult.Fail("native", "调用 Python 运行时失败：" + ex.GetType().Name);
            }

            result.DurationMs = Environment.TickCount - startTicks;

            if (code == E_WRONG_THREAD)
            {
                MarkWedged();
                return ScriptResult.Fail("wrong_thread", "Python 运行时拒绝了这次调用（线程不符）。");
            }

            var output = ReadLastOutput();

            if (code == E_INITIALIZATION)
            {
                lock (_gate) { _initFailed = true; _initError = ReadLastError(); }
                return ScriptResult.Fail("init", "Python 运行时不可用：" + _initError);
            }

            PyUwpProtocol.SplitOutput(output, result);

            if (code == 0)
            {
                result.Ok = true;
                result.ExitCode = 0;
            }
            else
            {
                result.Ok = false;
                result.ExitCode = 1;

                var lastError = ReadLastError();
                if (string.IsNullOrEmpty(result.StdErr))
                {
                    result.StdErr = string.IsNullOrEmpty(lastError) ? "脚本执行失败（码 " + code + "）。" : lastError;
                }
            }

            _runs++;
            return result;
        }

        private void DrainWithFailure(string message)
        {
            while (true)
            {
                WorkItem item;
                if (!_queue.TryDequeue(0, out item) || item == null) return;
                item.Completion.TrySetResult(ScriptResult.Fail("init", message));
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 原生输出读取
        // ═════════════════════════════════════════════════════════════════

        private static string ReadLastError()
        {
            try
            {
                var ptr = PyUwpRuntime_LastError();
                if (ptr == IntPtr.Zero) return "";
                return MarshalUtf8(ptr);
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string ReadLastOutput()
        {
            try
            {
                var ptr = PyUwpRuntime_LastOutput();
                if (ptr == IntPtr.Zero) return "";
                return MarshalUtf8(ptr);
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string MarshalUtf8(IntPtr ptr)
        {
            // 手动按 0 结尾读 UTF-8，避免依赖 Marshal.PtrToStringAnsi 的编码假设
            int length = 0;
            while (System.Runtime.InteropServices.Marshal.ReadByte(ptr, length) != 0 && length < 1024 * 1024)
            {
                length++;
            }
            if (length == 0) return "";

            var buffer = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _queue.Close();

            var engine = _engine;
            if (engine != null)
            {
                try
                {
                    // 等引擎线程收尾（它会在退出前调 Finalize）。
                    // 超时也不强杀 —— 真卡住时只能等应用退出，杀线程会让 CPython 状态不一致。
                    if (!engine.Wait(2000))
                    {
                        _log.Log(LogLevel.Warning, "python", "引擎线程未在 2 秒内退出");
                    }
                }
                catch (Exception) { }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 辅助
        // ═════════════════════════════════════════════════════════════════

        private sealed class WorkItem
        {
            public string Source;
            public int TimeoutMs;
            public TaskCompletionSource<ScriptResult> Completion =
                new TaskCompletionSource<ScriptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// 极简阻塞队列。不用 BlockingCollection 是因为 UWP 的 .NET Native 下
        /// 它有时会被裁剪掉，而这个场景只需要"入队 / 带超时出队 / 关闭"三件事。
        /// </summary>
        private sealed class BlockingQueue<T> where T : class
        {
            private readonly Queue<T> _items = new Queue<T>();
            private readonly object _gate = new object();
            private bool _closed;

            public void Enqueue(T item)
            {
                lock (_gate)
                {
                    if (_closed) return;
                    _items.Enqueue(item);
                    Monitor.Pulse(_gate);
                }
            }

            public bool TryDequeue(int timeoutMs, out T item)
            {
                item = null;
                lock (_gate)
                {
                    if (_items.Count == 0)
                    {
                        if (_closed) return false;
                        if (timeoutMs <= 0) return true;      // 非阻塞取空：返回"暂时没有"
                        Monitor.Wait(_gate, timeoutMs);
                    }

                    if (_items.Count == 0) return true;
                    item = _items.Dequeue();
                    return true;
                }
            }

            public void Close()
            {
                lock (_gate)
                {
                    _closed = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
