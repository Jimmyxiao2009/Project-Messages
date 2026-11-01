// Platform/StartupTrace.cs —— 启动阶段埋点（临时诊断，定位完就删）
//
// 为什么需要它：真机上应用启动后 8 秒内进程直接消失，而 crash.log 是空的。
// 说明这不是能被 catch 捕获的托管异常 —— StartAsync 的 try/catch 没被走到，
// 或者崩在更早的地方（.NET Native 的类型加载失败、原生层硬失败等）。
//
// 所以用最朴素的方式记：每到一个阶段就往文件追加一行并立刻关文件（等于 flush）。
// 下一次启动读这个文件，最后一个写成功的阶段就是"活到哪一步"，
// 它后面那一步就是凶手。
//
// 刻意用 System.IO.File 直接写，不走 IFileStore、不走 DiagnosticLog ——
// 那两条路本身可能就是崩的原因，不能拿嫌疑人当工具。

using System;
using System.IO;
using Windows.Storage;

namespace Yanshuai.Messages.Platform
{
    public static class StartupTrace
    {
        private static readonly object Gate = new object();
        private static string _path;

        private static string Path
        {
            get
            {
                if (_path == null)
                {
                    try { _path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup-trace.log"); }
                    catch (Exception) { _path = ""; }
                }
                return _path;
            }
        }

        public static void Mark(string stage)
        {
            Write(stage);
        }

        /// <summary>记一个阶段，并顺手把耗时带上（便于看出哪一步特别慢）。</summary>
        public static void Mark(string stage, string detail)
        {
            Write(stage + "  " + detail);
        }

        public static void Fail(string stage, Exception ex)
        {
            var text = stage + "  !! " + ex.GetType().FullName;
            if (!string.IsNullOrEmpty(ex.Message))
            {
                var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
                text += ": " + (message.Length > 300 ? message.Substring(0, 300) : message);
            }
            Write(text);

            // 内层异常往往是真正的凶手（TypeInitializationException 之类）
            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth++ < 4)
            {
                var innerText = "    ← " + inner.GetType().FullName;
                if (!string.IsNullOrEmpty(inner.Message))
                {
                    var m = inner.Message.Replace('\r', ' ').Replace('\n', ' ');
                    innerText += ": " + (m.Length > 300 ? m.Substring(0, 300) : m);
                }
                Write(innerText);
                inner = inner.InnerException;
            }
        }

        /// <summary>清掉上一次的痕迹（每次冷启动开始时调一次）。</summary>
        public static void Reset()
        {
            try
            {
                var path = Path;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception) { }
        }

        private static void Write(string text)
        {
            try
            {
                var path = Path;
                if (string.IsNullOrEmpty(path)) return;

                lock (Gate)
                {
                    File.AppendAllText(path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // 埋点本身不能影响启动
            }
        }
    }
}
