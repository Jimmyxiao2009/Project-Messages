// Core/Scripting/PyUwpProtocol.cs —— PyUwp 的脚本包装与输出拆解（纯逻辑）
//
// 这段逻辑为什么放在 Core 而不是 Platform：
// 它是整条脚本链路里最容易悄悄出错的地方 —— 字符串拼 Python 代码、
// 再从合并输出里切出 stdout/stderr，任何一处错位都会让"报错被当成正常输出"
// 或者"结果凭空少一截"，而且不会有任何异常提示。
// 放在 Core 就能在桌面上直接单测，不用每次都部署到手机上试。

using System;
using System.Globalization;
using System.Text;

namespace Yanshuai.Messages.Core
{
    public static class PyUwpProtocol
    {
        /// <summary>分隔标记。前后各一个，中间夹 base64 段。</summary>
        public const string Marker = "__PYUWP_SPLIT__";

        /// <summary>
        /// 把用户脚本包成一段能分离 stdout/stderr 的驱动脚本。
        ///
        /// 用户源码与工作目录都经 base64 传入 —— 这样无论里面有什么引号、
        /// 反斜杠、换行、非 ASCII 字符，都不会破坏包装脚本本身的语法。
        /// 直接字符串插值在这里是行不通的：用户脚本里一个三引号就能把包装炸掉。
        /// </summary>
        public static string BuildPayload(string userSource, string workingDirectory)
        {
            var sourceB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(userSource ?? ""));
            var dirB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory ?? ""));

            var sb = new StringBuilder();
            sb.Append("import sys, io, os, base64, traceback\n");
            sb.Append("_src = base64.b64decode('").Append(sourceB64).Append("').decode('utf-8')\n");
            sb.Append("_dir = base64.b64decode('").Append(dirB64).Append("').decode('utf-8')\n");
            sb.Append("if _dir:\n");
            sb.Append("    try: os.chdir(_dir)\n");
            sb.Append("    except Exception as _e: sys.stderr.write('无法进入工作目录: %s\\n' % _e)\n");
            sb.Append("_out = io.StringIO()\n");
            sb.Append("_err = io.StringIO()\n");
            sb.Append("_code = 0\n");
            sb.Append("_saved_out, _saved_err = sys.stdout, sys.stderr\n");
            sb.Append("sys.stdout, sys.stderr = _out, _err\n");
            // 写入探针：真机上出现过"脚本写文件卡到超时"。在跑用户脚本之前先自己写一个
            // 临时文件再删掉，把结果放进输出前缀 —— 探针失败时模型和日志都能直接看到
            // 真实异常（权限？路径？只读目录？），不用再去猜。
            sb.Append("_probe = 'write-probe:skipped'\n");
            sb.Append("if _dir:\n");
            sb.Append("    try:\n");
            sb.Append("        _pf = os.path.join(_dir, '__chronos_probe__.tmp')\n");
            sb.Append("        with open(_pf, 'w') as _ph:\n");
            sb.Append("            _ph.write('ok')\n");
            sb.Append("        os.remove(_pf)\n");
            sb.Append("        _probe = 'write-probe:ok cwd=' + _dir\n");
            sb.Append("    except BaseException as _pe:\n");
            sb.Append("        _probe = 'write-probe:FAILED cwd=' + _dir + ' err=' + repr(_pe)\n");
            sb.Append("try:\n");
            sb.Append("    exec(compile(_src, '<script>', 'exec'), {'__name__': '__main__', '__file__': '<script>'})\n");
            sb.Append("except SystemExit as _e:\n");
            sb.Append("    _code = _e.code if isinstance(_e.code, int) else 0\n");
            sb.Append("except BaseException:\n");
            sb.Append("    traceback.print_exc(file=_err)\n");
            sb.Append("    _code = 1\n");
            sb.Append("finally:\n");
            sb.Append("    sys.stdout, sys.stderr = _saved_out, _saved_err\n");
            sb.Append("_o = base64.b64encode(_out.getvalue().encode('utf-8', 'replace')).decode('ascii')\n");
            sb.Append("_e2 = base64.b64encode(_err.getvalue().encode('utf-8', 'replace')).decode('ascii')\n");
            // 必须**一次**写完：分两次 write 会在两段之间多出一个空片段，
            // 拆解时下标就整体错一位，stdout 与 stderr 会互换。
            sb.Append("_saved_out.write(_probe + '\\n')\n");
            sb.Append("_saved_out.write('").Append(Marker).Append("' + _o + '").Append(Marker)
              .Append("' + _e2 + '").Append(Marker).Append("code=' + str(_code) + '").Append(Marker).Append("')\n");
            sb.Append("_saved_out.flush()\n");

            return sb.ToString();
        }

        /// <summary>
        /// 从合并输出里拆出 stdout / stderr / 退出码。
        ///
        /// 期望形状：
        ///   [前置输出] M &lt;out_b64&gt; M &lt;err_b64&gt; M code=N M
        /// 分隔符共出现 4 次，把内容切成 5 段，所以从右往左数：
        ///   倒数第二段是 "code=N"，倒数第三是 stderr，倒数第四是 stdout。
        /// 倒数第五之前的所有内容（Python 自己打的警告等）接到 stdout 前面。
        /// </summary>
        public static void SplitOutput(string combined, ScriptResult result)
        {
            if (result == null) return;

            if (string.IsNullOrEmpty(combined))
            {
                result.StdOut = "";
                result.StdErr = "";
                return;
            }

            var parts = combined.Split(new string[] { Marker }, StringSplitOptions.None);

            // 标记对不上（解释器被搞崩、或 ABI 输出格式变了）→ 全部当 stdout，
            // 至少不丢信息；宁可让模型多看到一点，也不要静默吞掉
            if (parts.Length < 5)
            {
                result.StdOut = combined;
                return;
            }

            int count = parts.Length;
            var codePart = parts[count - 2];
            var errPart = parts[count - 3];
            var outPart = parts[count - 4];

            result.StdOut = DecodeBase64(outPart);
            result.StdErr = DecodeBase64(errPart);

            var codeText = codePart.Trim();
            int eq = codeText.IndexOf('=');
            if (eq >= 0)
            {
                int parsed;
                if (int.TryParse(codeText.Substring(eq + 1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsed))
                {
                    result.ExitCode = parsed;
                    result.Ok = parsed == 0;
                }
            }

            // 倒数第四段之前的内容（Python 自己打的启动警告、原生库的提示等）
            // 都是脚本运行前的输出，接到 stdout 前面。注意边界是 count-4，
            // 不是 count-5 —— 用错会把紧邻 stdout 的那一段整个丢掉。
            if (count > 4)
            {
                var prefix = new StringBuilder();
                for (int i = 0; i < count - 4; i++) prefix.Append(parts[i]);
                if (prefix.Length > 0) result.StdOut = prefix.ToString() + result.StdOut;
            }
        }

        private static string DecodeBase64(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                var bytes = Convert.FromBase64String(value);
                return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
                // 不是合法 base64：原样返回，不吞掉内容
                return value;
            }
        }
    }
}
