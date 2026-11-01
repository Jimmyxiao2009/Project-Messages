using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Windows.Foundation;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Yanshuai.Messages.Ui
{
    /// <summary>
    /// 纯 C#/XAML 的迷你 LaTeX 数学排版引擎（无外部依赖、离线、Win10 Mobile 可用）。
    ///
    /// 不是把 LaTeX 替换成 Unicode 文本，而是构建真正的二维盒模型并对齐基线：
    ///   • \frac 渲染为真正的分数线（分子在上 / 分母在下）
    ///   • 矩阵用 Canvas 网格 + 自适应高度的定界符
    ///   • 上/下标用基线偏移的缩小盒
    ///   • \sqrt 用绘制的根号 + 上横线（vinculum）
    ///   • \sum/\int 等大算符在 display 模式把上下限放在上下方
    ///   • 完整希腊字母 / 运算符 / 关系符 / 箭头 / 集合 / 逻辑符号表
    ///
    /// 入口 <see cref="Render(string,double,Brush,bool,out double)"/> 返回一个
    /// FrameworkElement，可放进 RichTextBlock 的 InlineUIContainer。任何解析异常都
    /// 会兜底为原文文本，绝不抛出（避免聊天渲染崩溃）。
    /// </summary>
    internal static class LatexRenderer
    {
        // ── 排版可调常量（相对字号 em）─────────────────────────────────────────
        private const double AxisFactor      = 0.30;  // 数学轴线在基线之上的高度
        private const double FracGapFactor   = 0.18;  // 分数线与分子/分母的间距
        private const double FracBarFactor   = 1.0/16;// 分数线粗细
        private const double SupShiftFactor  = 0.48;  // 上标基线在主基线之上
        private const double SubShiftFactor  = 0.20;  // 下标基线在主基线之下
        private const double ScriptScale     = 0.72;  // 上下标 / 脚标字号缩放
        private const double ScriptMinSize   = 8.5;   // 脚标最小字号
        private const double ThinSpaceFactor = 3.0/18;
        private const double MedSpaceFactor  = 4.0/18;
        private const double ThickSpaceFactor= 5.0/18;

        private static readonly FontFamily MathFont = new FontFamily("Cambria Math, Cambria, Segoe UI");

        // ── 公共入口 ────────────────────────────────────────────────────────────

        /// <param name="display">块级 $$ 为 true（大算符限制放上下、分数更大），行内 $ 为 false。</param>
        /// <param name="baseline">输出：数学基线距元素顶部的像素，供调用方做行内对齐。</param>
        public static FrameworkElement Render(string latex, double fontSize, Brush foreground, bool display, out double baseline)
        {
            baseline = fontSize * 0.7;
            if (string.IsNullOrEmpty(latex)) return new TextBlock { Text = "" };
            try
            {
                var style = new Style
                {
                    FontSize   = fontSize,
                    Foreground = foreground,
                    Display    = display,
                };
                var toks  = Tokenize(latex);
                var cur   = new Cursor(toks);

                // 顶层按 \\ 拆成多行（多行公式块），逐行解析后垂直堆叠
                var rows = new List<Box>();
                while (!cur.End)
                {
                    var atoms = ParseRow(cur, style, StopMode.TopLevel);
                    rows.Add(Compose(atoms, style));
                    if (!cur.End && cur.Peek.IsCmd("\\")) cur.Next(); // 消费行分隔
                    else break;
                }

                Box box = rows.Count == 1 ? rows[0] : VStack(rows, style, center: true);
                baseline = box.Ascent;
                var el = box.Element ?? new TextBlock { Text = "" };
                if (el is FrameworkElement fe) { fe.Width = box.Width; fe.Height = box.Height; }
                return el as FrameworkElement ?? new TextBlock { Text = latex };
            }
            catch
            {
                // 兜底：解析失败时显示原文，绝不让聊天渲染崩溃
                return new TextBlock
                {
                    Text       = latex,
                    FontFamily = MathFont,
                    FontSize   = fontSize,
                    Foreground = foreground,
                    FontStyle  = FontStyle.Italic,
                };
            }
        }

        public static FrameworkElement Render(string latex, double fontSize, Brush foreground, bool display)
            => Render(latex, fontSize, foreground, display, out _);

        // ── 盒模型 ────────────────────────────────────────────────────────────
        // 每个盒子知道自己的宽度，以及基线之上(Ascent)/之下(Descent)的高度，
        // 这样水平行(HBox)就能让所有盒子对齐到同一条基线。

        private sealed class Box
        {
            public FrameworkElement Element;   // null = 纯占位（水平空白 / 空基底）
            public double Width;
            public double Ascent;
            public double Descent;
            public double Height => Ascent + Descent;
            public bool IsBigOp;               // 大算符（决定上下限放法）
            public AtomClass Cls = AtomClass.Ord;
        }

        private enum AtomClass { Ord, Op, Bin, Rel, Open, Close, Punct, Inner }

        private sealed class Style
        {
            public double FontSize;
            public Brush Foreground;
            public bool Display;
            public bool Bold;
            public bool Upright;     // \mathrm / \text / 函数名
            public string Variant;   // null | "bb" | "cal" | "frak" | "sf" | "tt"

            public Style Clone() => new Style
            {
                FontSize = FontSize, Foreground = Foreground, Display = Display,
                Bold = Bold, Upright = Upright, Variant = Variant,
            };
            public Style Scripted()
            {
                var s = Clone();
                s.FontSize = Math.Max(FontSize * ScriptScale, ScriptMinSize);
                s.Display  = false;
                return s;
            }
        }

        // ── 词法分析 ────────────────────────────────────────────────────────────

        private enum TokKind { Char, Cmd, LBrace, RBrace, Super, Sub, Amp, Space }

        private struct Tok
        {
            public TokKind Kind;
            public string Text;   // Char: 单字符；Cmd: 命令名（不含反斜杠）；其余：原文
            public bool IsCmd(string name) => Kind == TokKind.Cmd && Text == name;
        }

        private sealed class Cursor
        {
            private readonly List<Tok> _t;
            private int _i;
            public Cursor(List<Tok> t) { _t = t; _i = 0; }
            public bool End => _i >= _t.Count;
            public Tok Peek => _i < _t.Count ? _t[_i] : new Tok { Kind = TokKind.Space, Text = "" };
            public Tok Next() { var t = Peek; _i++; return t; }
            public void SkipSpaces() { while (!End && _t[_i].Kind == TokKind.Space) _i++; }
        }

        private static List<Tok> Tokenize(string s)
        {
            var list = new List<Tok>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '\\')
                {
                    if (i + 1 < n && IsAsciiLetter(s[i + 1]))
                    {
                        int j = i + 1;
                        while (j < n && IsAsciiLetter(s[j])) j++;
                        list.Add(new Tok { Kind = TokKind.Cmd, Text = s.Substring(i + 1, j - i - 1) });
                        i = j;
                        // 多字母命令吞掉其后的空白
                        while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
                    }
                    else if (i + 1 < n)
                    {
                        // 单字符命令：\\ \{ \} \, \; \: \! \  \# \% \& \_ \| 等
                        list.Add(new Tok { Kind = TokKind.Cmd, Text = s[i + 1].ToString() });
                        i += 2;
                    }
                    else i++;
                    continue;
                }
                switch (c)
                {
                    case '{': list.Add(new Tok { Kind = TokKind.LBrace, Text = "{" }); i++; break;
                    case '}': list.Add(new Tok { Kind = TokKind.RBrace, Text = "}" }); i++; break;
                    case '^': list.Add(new Tok { Kind = TokKind.Super,  Text = "^" }); i++; break;
                    case '_': list.Add(new Tok { Kind = TokKind.Sub,    Text = "_" }); i++; break;
                    case '&': list.Add(new Tok { Kind = TokKind.Amp,    Text = "&" }); i++; break;
                    case ' ':
                    case '\t':
                    case '\n':
                    case '\r':
                        list.Add(new Tok { Kind = TokKind.Space, Text = " " }); i++; break;
                    default:
                        list.Add(new Tok { Kind = TokKind.Char, Text = c.ToString() }); i++; break;
                }
            }
            return list;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        // ── 解析 ────────────────────────────────────────────────────────────────

        private enum StopMode { TopLevel, Group, EnvCell, LeftRight }

        // 解析一“行”的原子序列；遇到对应停止符即返回（不消费停止符，交由调用方处理）。
        private static List<Box> ParseRow(Cursor c, Style st, StopMode mode)
        {
            var atoms = new List<Box>();
            int guard = 0;
            while (!c.End)
            {
                if (++guard > 20000) break; // 防御：防止异常输入下死循环
                c.SkipSpaces();
                if (c.End) break;
                var p = c.Peek;

                // 停止符
                if (p.Kind == TokKind.RBrace && mode == StopMode.Group) break;
                if (mode == StopMode.EnvCell && (p.Kind == TokKind.Amp || p.IsCmd("\\") || p.IsCmd("end"))) break;
                if (mode == StopMode.LeftRight && p.IsCmd("right")) break;
                if (mode == StopMode.TopLevel && p.IsCmd("\\")) break;

                // 基底（允许前置裸上/下标：基底为空）
                Box atom;
                if (p.Kind == TokKind.Super || p.Kind == TokKind.Sub)
                    atom = Empty();
                else
                    atom = ParseAtom(c, st);

                // 附着上/下标
                Box sup = null, sub = null;
                while (true)
                {
                    c.SkipSpaces();
                    if (c.End) break;
                    var q = c.Peek;
                    if (q.Kind == TokKind.Super && sup == null) { c.Next(); sup = ParseArg(c, st.Scripted()); }
                    else if (q.Kind == TokKind.Sub && sub == null) { c.Next(); sub = ParseArg(c, st.Scripted()); }
                    else break;
                }
                if (sup != null || sub != null)
                    atom = MakeScripts(atom, sup, sub, st);

                atoms.Add(atom);
            }
            return atoms;
        }

        // 解析单个原子（含其参数），不附着上下标。
        private static Box ParseAtom(Cursor c, Style st)
        {
            var t = c.Next();
            switch (t.Kind)
            {
                case TokKind.LBrace:
                {
                    var inner = ParseRow(c, st, StopMode.Group);
                    if (!c.End && c.Peek.Kind == TokKind.RBrace) c.Next();
                    var b = Compose(inner, st);
                    b.Cls = AtomClass.Ord;
                    return b;
                }
                case TokKind.Char:
                    return CharBox(t.Text[0], st);
                case TokKind.Cmd:
                    return HandleCommand(t.Text, c, st);
                case TokKind.Amp:
                case TokKind.Super:
                case TokKind.Sub:
                case TokKind.RBrace:
                case TokKind.Space:
                default:
                    return Empty();
            }
        }

        // 读取一个“参数”：{...} 组、或单个 token（命令/字符）。
        private static Box ParseArg(Cursor c, Style st)
        {
            c.SkipSpaces();
            if (c.End) return Empty();
            return ParseAtom(c, st);
        }

        // ── 命令分发 ──────────────────────────────────────────────────────────

        private static Box HandleCommand(string name, Cursor c, Style st)
        {
            // 符号表（希腊字母 / 运算符 / 关系符 / 箭头 / 集合 / 逻辑…）
            string glyph;
            if (Symbols.TryGetValue(name, out glyph))
            {
                var b = TextBox(glyph, st, upright: true);
                b.Cls = ClassOf(name);
                return b;
            }

            // 大算符
            string bigGlyph;
            if (BigOps.TryGetValue(name, out bigGlyph))
            {
                var big = st.Clone();
                big.FontSize = st.FontSize * (st.Display ? 1.6 : 1.25);
                var b = TextBox(bigGlyph, big, upright: true);
                b.Cls = AtomClass.Op;
                b.IsBigOp = true;
                return b;
            }

            // 函数名 / 取极限类（lim、max… 上下限放下方）
            if (Functions.Contains(name))
            {
                var b = TextBox(name, st, upright: true);
                b.Cls = AtomClass.Op;
                if (LimitFuncs.Contains(name)) b.IsBigOp = true;
                return b;
            }

            switch (name)
            {
                // 分数
                case "frac":
                case "dfrac":
                case "tfrac":
                {
                    var inner = name == "dfrac" ? WithDisplay(st, true) : name == "tfrac" ? WithDisplay(st, false) : st;
                    var num = ParseArg(c, inner);
                    var den = ParseArg(c, inner);
                    return FracBox(num, den, inner, withBar: true);
                }
                case "binom":
                case "dbinom":
                case "tbinom":
                {
                    var num = ParseArg(c, st);
                    var den = ParseArg(c, st);
                    var stack = FracBox(num, den, st, withBar: false);
                    return DelimBox("(", stack, ")", st);
                }
                case "sqrt":
                {
                    Box index = null;
                    c.SkipSpaces();
                    if (!c.End && c.Peek.Kind == TokKind.Char && c.Peek.Text == "[")
                        index = ReadBracketArg(c, st.Scripted());
                    var rad = ParseArg(c, st);
                    return SqrtBox(rad, index, st);
                }

                // 样式 / 字体
                case "text":
                case "textrm":
                case "textnormal":
                case "mbox":
                    return TextLiteral(c, st);
                case "mathrm":
                case "operatorname":
                case "mathsf":   // 退化为正体
                {
                    var s = st.Clone(); s.Upright = true; s.Variant = name == "mathsf" ? "sf" : null;
                    return ParseArg(c, s);
                }
                case "mathbf":
                {
                    var s = st.Clone(); s.Bold = true; s.Upright = true;
                    return ParseArg(c, s);
                }
                case "boldsymbol":
                case "bm":
                {
                    var s = st.Clone(); s.Bold = true;
                    return ParseArg(c, s);
                }
                case "mathit":
                {
                    var s = st.Clone(); s.Upright = false;
                    return ParseArg(c, s);
                }
                case "mathtt":
                {
                    var s = st.Clone(); s.Variant = "tt"; s.Upright = true;
                    return ParseArg(c, s);
                }
                case "mathbb":
                {
                    var s = st.Clone(); s.Variant = "bb"; s.Upright = true;
                    return ParseArg(c, s);
                }
                case "mathcal":
                case "mathscr":
                {
                    var s = st.Clone(); s.Variant = "cal"; s.Upright = true;
                    return ParseArg(c, s);
                }
                case "mathfrak":
                {
                    var s = st.Clone(); s.Variant = "frak"; s.Upright = true;
                    return ParseArg(c, s);
                }
                case "color":
                case "textcolor":
                case "mathchoice":
                    // 忽略颜色等修饰，渲染其内容参数
                    ParseArg(c, st); // 吞掉颜色名 {..}
                    return ParseArg(c, st);

                // 重音 / 上下装饰
                case "hat":            return AccentBox(ParseArg(c, st), "^", st, stretch: false);
                case "widehat":        return AccentBox(ParseArg(c, st), "^", st, stretch: true);
                case "tilde":          return AccentBox(ParseArg(c, st), "~", st, stretch: false);
                case "widetilde":      return AccentBox(ParseArg(c, st), "~", st, stretch: true);
                case "dot":            return AccentBox(ParseArg(c, st), "˙", st, stretch: false);
                case "ddot":           return AccentBox(ParseArg(c, st), "¨", st, stretch: false);
                case "check":          return AccentBox(ParseArg(c, st), "ˇ", st, stretch: false);
                case "breve":          return AccentBox(ParseArg(c, st), "˘", st, stretch: false);
                case "acute":          return AccentBox(ParseArg(c, st), "´", st, stretch: false);
                case "grave":          return AccentBox(ParseArg(c, st), "`",      st, stretch: false);
                case "mathring":       return AccentBox(ParseArg(c, st), "˚", st, stretch: false);
                case "bar":
                case "overline":       return OverUnderLineBox(ParseArg(c, st), st, over: true);
                case "underline":      return OverUnderLineBox(ParseArg(c, st), st, over: false);
                case "vec":
                case "overrightarrow": return ArrowOverBox(ParseArg(c, st), st, rightward: true);
                case "overleftarrow":  return ArrowOverBox(ParseArg(c, st), st, rightward: false);
                case "overbrace":      return OverUnderLineBox(ParseArg(c, st), st, over: true);
                case "underbrace":     return OverUnderLineBox(ParseArg(c, st), st, over: false);

                // \left … \right 自适应定界符
                case "left":
                {
                    string ld = ReadDelim(c);
                    var inner = ParseRow(c, st, StopMode.LeftRight);
                    string rd = ".";
                    if (!c.End && c.Peek.IsCmd("right")) { c.Next(); rd = ReadDelim(c); }
                    return DelimBox(ld, Compose(inner, st), rd, st);
                }
                case "right":
                    return Empty(); // 落单的 \right，忽略

                // 环境（矩阵 / cases）
                case "begin":
                    return ParseEnvironment(c, st);
                case "end":
                    ReadGroupName(c); // 吞掉 {name}
                    return Empty();

                // 间距
                case ",": return Space(st.FontSize * ThinSpaceFactor);
                case ":":
                case ">": return Space(st.FontSize * MedSpaceFactor);
                case ";": return Space(st.FontSize * ThickSpaceFactor);
                case "!": return Space(0); // 负薄空：从简，置 0
                case " ": return Space(st.FontSize * MedSpaceFactor);
                case "quad":  return Space(st.FontSize);
                case "qquad": return Space(st.FontSize * 2);
                case "enspace": return Space(st.FontSize * 0.5);
                case "thinspace": return Space(st.FontSize * ThinSpaceFactor);

                // 样式声明（从简：消费但不改变后续样式）
                case "displaystyle":
                case "textstyle":
                case "scriptstyle":
                case "scriptscriptstyle":
                case "limits":
                case "nolimits":
                case "nonumber":
                case "\\":   // 组内落单的换行：忽略
                    return Empty();

                // 转义字符
                case "{": return CharBox('{', st);
                case "}": return CharBox('}', st);
                case "#": return CharBox('#', st);
                case "%": return CharBox('%', st);
                case "&": return CharBox('&', st);
                case "_": return CharBox('_', st);
                case "$": return CharBox('$', st);
                case "|": { var b = TextBox("∥", st, upright: true); b.Cls = AtomClass.Ord; return b; }

                default:
                    // 未知命令：退化为正体文本（去掉反斜杠），保证可读且不崩溃
                    return TextBox(name, st, upright: true);
            }
        }

        private static Style WithDisplay(Style st, bool display)
        {
            var s = st.Clone(); s.Display = display; return s;
        }

        // 读取 \text{...} 字面文本（保留空格，正体）
        private static Box TextLiteral(Cursor c, Style st)
        {
            c.SkipSpaces();
            var sb = new StringBuilder();
            if (!c.End && c.Peek.Kind == TokKind.LBrace)
            {
                c.Next();
                int depth = 1;
                while (!c.End)
                {
                    var t = c.Next();
                    if (t.Kind == TokKind.LBrace) { depth++; sb.Append('{'); continue; }
                    if (t.Kind == TokKind.RBrace) { depth--; if (depth == 0) break; sb.Append('}'); continue; }
                    sb.Append(TokRaw(t));
                }
            }
            else if (!c.End)
            {
                sb.Append(TokRaw(c.Next()));
            }
            var s = st.Clone(); s.Upright = true;
            var b = TextBox(sb.ToString(), s, upright: true);
            b.Cls = AtomClass.Ord;
            return b;
        }

        private static string TokRaw(Tok t)
        {
            switch (t.Kind)
            {
                case TokKind.Cmd:   return t.Text.Length == 1 ? t.Text : "\\" + t.Text;
                case TokKind.Space: return " ";
                case TokKind.Super: return "^";
                case TokKind.Sub:   return "_";
                case TokKind.Amp:   return "&";
                default:            return t.Text;
            }
        }

        // 读取 [..] 可选参数（如 \sqrt[3]{x}）
        private static Box ReadBracketArg(Cursor c, Style st)
        {
            if (c.End || c.Peek.Text != "[") return Empty();
            c.Next(); // 消费 [
            var atoms = new List<Box>();
            int guard = 0;
            while (!c.End && !(c.Peek.Kind == TokKind.Char && c.Peek.Text == "]"))
            {
                if (++guard > 5000) break;
                atoms.Add(ParseAtom(c, st));
            }
            if (!c.End && c.Peek.Text == "]") c.Next();
            return Compose(atoms, st);
        }

        // 读取定界符（\left / \right 之后）：单字符、. (无)、或 \{ \} \| \langle 等命令
        private static string ReadDelim(Cursor c)
        {
            c.SkipSpaces();
            if (c.End) return ".";
            var t = c.Next();
            if (t.Kind == TokKind.Char || t.Kind == TokKind.LBrace || t.Kind == TokKind.RBrace)
                return t.Text;
            if (t.Kind == TokKind.Cmd)
            {
                switch (t.Text)
                {
                    case "{": return "{";
                    case "}": return "}";
                    case "|": return "∥";
                    case "langle": return "⟨";
                    case "rangle": return "⟩";
                    case "lfloor": return "⌊";
                    case "rfloor": return "⌋";
                    case "lceil":  return "⌈";
                    case "rceil":  return "⌉";
                    case "vert":   return "|";
                    case "Vert":   return "∥";
                    default: return ".";
                }
            }
            return ".";
        }

        private static string ReadGroupName(Cursor c)
        {
            c.SkipSpaces();
            var sb = new StringBuilder();
            if (!c.End && c.Peek.Kind == TokKind.LBrace)
            {
                c.Next();
                while (!c.End && c.Peek.Kind != TokKind.RBrace) sb.Append(TokRaw(c.Next()));
                if (!c.End) c.Next();
            }
            return sb.ToString();
        }

        // ── 环境（矩阵 / cases / aligned）─────────────────────────────────────
        private static Box ParseEnvironment(Cursor c, Style st)
        {
            string env = ReadGroupName(c).Trim();
            string ld = ".", rd = ".";
            bool center = true;
            switch (env)
            {
                case "pmatrix":  ld = "("; rd = ")"; break;
                case "bmatrix":  ld = "["; rd = "]"; break;
                case "Bmatrix":  ld = "{"; rd = "}"; break;
                case "vmatrix":  ld = "|"; rd = "|"; break;
                case "Vmatrix":  ld = "∥"; rd = "∥"; break;
                case "cases":    ld = "{"; rd = "."; center = false; break;
                case "matrix":
                case "array":
                case "aligned":
                case "align":
                case "align*":
                case "gathered":
                default:         center = (env == "matrix"); break;
            }

            // 逐单元解析：先解析一个单元（停在分隔符前），再根据停止符决定下一步：
            //   &  → 同行下一列；\\ → 换行；\end → 结束
            var rows = new List<List<Box>>();
            var row = new List<Box>();
            int guard = 0;
            while (!c.End)
            {
                if (++guard > 20000) break;
                var cellAtoms = ParseRow(c, st, StopMode.EnvCell);
                row.Add(Compose(cellAtoms, st));

                c.SkipSpaces();
                if (c.End) break;
                var p = c.Peek;
                if (p.Kind == TokKind.Amp) { c.Next(); continue; }
                if (p.IsCmd("\\")) { c.Next(); rows.Add(row); row = new List<Box>(); continue; }
                if (p.IsCmd("end")) { c.Next(); ReadGroupName(c); break; }
                c.Next(); // 其他意外 token：消费以保证前进
            }
            // 收尾：丢弃由结尾 \\ 造成的全空尾行
            bool tailEmpty = row.Count <= 1 && (row.Count == 0 || (row[0].Element == null && row[0].Width == 0));
            if (!tailEmpty) rows.Add(row);
            if (rows.Count == 0) rows.Add(new List<Box>());

            var matrix = MatrixBox(rows, st, center);
            if (ld == "." && rd == ".") return matrix;
            return DelimBox(ld, matrix, rd, st);
        }

        // ── 叶子：字符 / 文本盒 ────────────────────────────────────────────────

        private static Box CharBox(char ch, Style st)
        {
            var b = TextBox(MapChar(ch, st), st, upright: st.Upright || !IsAsciiLetter(ch));
            b.Cls = ClassOfChar(ch);
            return b;
        }

        // 单字符在数学模式下的字形（含 \mathbb/\mathcal 等变体映射）
        private static string MapChar(char ch, Style st)
        {
            if (st.Variant == "bb")  return VariantString(ch, 0x1D538, 0x1D552, 0x1D7D8, BbExceptions);
            if (st.Variant == "cal") return VariantString(ch, 0x1D49C, 0x1D4B6, -1, CalExceptions);
            if (st.Variant == "frak")return VariantString(ch, 0x1D504, 0x1D51E, -1, FrakExceptions);
            return ch.ToString();
        }

        private static string VariantString(char ch, int upperBase, int lowerBase, int digitBase, Dictionary<char, string> exceptions)
        {
            string ex;
            if (exceptions != null && exceptions.TryGetValue(ch, out ex)) return ex;
            try
            {
                if (ch >= 'A' && ch <= 'Z') return char.ConvertFromUtf32(upperBase + (ch - 'A'));
                if (ch >= 'a' && ch <= 'z') return char.ConvertFromUtf32(lowerBase + (ch - 'a'));
                if (digitBase > 0 && ch >= '0' && ch <= '9') return char.ConvertFromUtf32(digitBase + (ch - '0'));
            }
            catch { }
            return ch.ToString();
        }

        private static Box TextBox(string s, Style st, bool upright)
        {
            var tb = new TextBlock
            {
                Text       = s,
                FontFamily = MathFont,
                FontSize   = st.FontSize,
                Foreground = st.Foreground,
                FontStyle  = upright ? FontStyle.Normal : FontStyle.Italic,
                FontWeight = st.Bold ? FontWeights.Bold : FontWeights.Normal,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = false,
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w  = tb.DesiredSize.Width;
            double h  = tb.DesiredSize.Height;
            double bl = tb.BaselineOffset;
            if (bl <= 0 || bl > h) bl = h * 0.8;  // BaselineOffset 不可用时的近似
            return new Box { Element = tb, Width = w, Ascent = bl, Descent = h - bl };
        }

        private static Box Empty() => new Box { Element = null, Width = 0, Ascent = 0, Descent = 0 };

        private static Box Space(double w) => new Box { Element = null, Width = Math.Max(0, w), Ascent = 0, Descent = 0 };

        // ── 组合：水平行（基线对齐 + 原子间距）─────────────────────────────────

        private static Box Compose(List<Box> atoms, Style st)
        {
            // 过滤纯空盒但保留空白盒（Width>0）
            var items = new List<Box>();
            foreach (var a in atoms)
                if (a != null && (a.Element != null || a.Width > 0)) items.Add(a);

            if (items.Count == 0) return Empty();
            if (items.Count == 1) return items[0];

            // 计算原子间距（同时把行首/紧跟 Open/Bin/Rel 的 +/- 当作正号，不加宽）
            double em = st.FontSize;
            double ascent = 0, descent = 0, width = 0;
            var xs = new double[items.Count];
            AtomClass prevEff = AtomClass.Open; // 行首视作 Open，抑制前导二元符空白
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                AtomClass cls = it.Cls;
                if (cls == AtomClass.Bin &&
                    (i == 0 || prevEff == AtomClass.Bin || prevEff == AtomClass.Rel ||
                     prevEff == AtomClass.Open || prevEff == AtomClass.Op || prevEff == AtomClass.Punct))
                    cls = AtomClass.Ord; // 退化为一元（正负号）

                if (i > 0)
                    width += InterSpace(prevEff, cls, em);

                xs[i] = width;
                width += it.Width;
                ascent = Math.Max(ascent, it.Ascent);
                descent = Math.Max(descent, it.Descent);
                prevEff = cls;
            }

            var canvas = new Canvas { Width = width, Height = ascent + descent };
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Element == null) continue;
                Canvas.SetLeft(it.Element, xs[i]);
                Canvas.SetTop(it.Element, ascent - it.Ascent);
                canvas.Children.Add(it.Element);
            }
            return new Box { Element = canvas, Width = width, Ascent = ascent, Descent = descent, Cls = AtomClass.Ord };
        }

        private static double InterSpace(AtomClass l, AtomClass r, double em)
        {
            if (l == AtomClass.Rel || r == AtomClass.Rel) return em * ThickSpaceFactor;
            if (l == AtomClass.Bin || r == AtomClass.Bin) return em * MedSpaceFactor;
            if (l == AtomClass.Op  || r == AtomClass.Op)  return em * ThinSpaceFactor;
            if (l == AtomClass.Punct)                     return em * ThinSpaceFactor;
            return 0;
        }

        // ── 上 / 下标 ────────────────────────────────────────────────────────

        private static Box MakeScripts(Box baseB, Box sup, Box sub, Style st)
        {
            bool limits = baseB.IsBigOp && st.Display;
            return limits ? LimitsBox(baseB, sup, sub, st) : SideScriptBox(baseB, sup, sub, st);
        }

        // 上下限放在算符的上方/下方（display 模式的 \sum、\lim 等）
        private static Box LimitsBox(Box baseB, Box sup, Box sub, Style st)
        {
            double gap = st.FontSize * 0.12;
            double w = baseB.Width;
            if (sup != null) w = Math.Max(w, sup.Width);
            if (sub != null) w = Math.Max(w, sub.Width);

            double supH = sup != null ? sup.Height + gap : 0;
            double subH = sub != null ? sub.Height + gap : 0;
            double baseTop = supH;
            double H = supH + baseB.Height + subH;

            var canvas = new Canvas { Width = w, Height = H };
            if (sup?.Element != null)
            {
                Canvas.SetLeft(sup.Element, (w - sup.Width) / 2);
                Canvas.SetTop(sup.Element, 0);
                canvas.Children.Add(sup.Element);
            }
            if (baseB.Element != null)
            {
                Canvas.SetLeft(baseB.Element, (w - baseB.Width) / 2);
                Canvas.SetTop(baseB.Element, baseTop);
                canvas.Children.Add(baseB.Element);
            }
            if (sub?.Element != null)
            {
                Canvas.SetLeft(sub.Element, (w - sub.Width) / 2);
                Canvas.SetTop(sub.Element, baseTop + baseB.Height + gap);
                canvas.Children.Add(sub.Element);
            }
            double ascent = baseTop + baseB.Ascent;
            return new Box { Element = canvas, Width = w, Ascent = ascent, Descent = H - ascent, Cls = AtomClass.Op };
        }

        // 上下标放在右侧（常规）
        private static Box SideScriptBox(Box baseB, Box sup, Box sub, Style st)
        {
            double supShift = st.FontSize * SupShiftFactor;
            double subShift = st.FontSize * SubShiftFactor;
            double gap = st.FontSize * 0.05;

            double scriptW = 0;
            if (sup != null) scriptW = Math.Max(scriptW, sup.Width);
            if (sub != null) scriptW = Math.Max(scriptW, sub.Width);
            double W = baseB.Width + (scriptW > 0 ? gap + scriptW : 0);

            double ascent = baseB.Ascent;
            double descent = baseB.Descent;
            if (sup != null) ascent = Math.Max(ascent, supShift + sup.Ascent);
            if (sup != null) descent = Math.Max(descent, sup.Descent - supShift);
            if (sub != null) descent = Math.Max(descent, subShift + sub.Descent);
            if (sub != null) ascent = Math.Max(ascent, sub.Ascent - subShift);

            var canvas = new Canvas { Width = W, Height = ascent + descent };
            double baseline = ascent;
            if (baseB.Element != null)
            {
                Canvas.SetLeft(baseB.Element, 0);
                Canvas.SetTop(baseB.Element, baseline - baseB.Ascent);
                canvas.Children.Add(baseB.Element);
            }
            double sx = baseB.Width + gap;
            if (sup?.Element != null)
            {
                Canvas.SetLeft(sup.Element, sx);
                Canvas.SetTop(sup.Element, baseline - supShift - sup.Ascent);
                canvas.Children.Add(sup.Element);
            }
            if (sub?.Element != null)
            {
                Canvas.SetLeft(sub.Element, sx);
                Canvas.SetTop(sub.Element, baseline + subShift - sub.Ascent);
                canvas.Children.Add(sub.Element);
            }
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = descent, Cls = baseB.Cls };
        }

        // ── 分数 ────────────────────────────────────────────────────────────

        private static Box FracBox(Box num, Box den, Style st, bool withBar)
        {
            double pad = Math.Max(1, st.FontSize * 0.12);
            double W = Math.Max(num.Width, den.Width) + 2 * pad;
            double barThk = Math.Max(1, st.FontSize * FracBarFactor);
            double gap = st.FontSize * FracGapFactor;
            double axis = st.FontSize * AxisFactor;

            double H = num.Height + gap + barThk + gap + den.Height;
            var canvas = new Canvas { Width = W, Height = H };

            if (num.Element != null)
            {
                Canvas.SetLeft(num.Element, (W - num.Width) / 2);
                Canvas.SetTop(num.Element, 0);
                canvas.Children.Add(num.Element);
            }
            double barY = num.Height + gap;
            if (withBar)
            {
                var bar = new Rectangle { Width = W, Height = barThk, Fill = st.Foreground };
                Canvas.SetLeft(bar, 0);
                Canvas.SetTop(bar, barY);
                canvas.Children.Add(bar);
            }
            if (den.Element != null)
            {
                Canvas.SetLeft(den.Element, (W - den.Width) / 2);
                Canvas.SetTop(den.Element, barY + barThk + gap);
                canvas.Children.Add(den.Element);
            }
            double ascent = barY + barThk / 2 + axis;
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = H - ascent, Cls = AtomClass.Inner };
        }

        // ── 根号 ────────────────────────────────────────────────────────────

        private static Box SqrtBox(Box content, Box index, Style st)
        {
            double thk = Math.Max(1, st.FontSize * 0.06);
            double topGap = st.FontSize * 0.14 + thk;       // vinculum 与内容顶部间距
            double rW = st.FontSize * 0.55;                  // 根号勾的宽度
            double pad = st.FontSize * 0.08;
            double indexW = index != null ? Math.Max(0, index.Width - rW * 0.35) : 0;
            double leftPad = indexW;

            double Hc = topGap + content.Height;             // 不含上方延伸
            double W = leftPad + rW + content.Width + 2 * pad;

            var canvas = new Canvas { Width = W, Height = Hc };

            // 根号勾 + vinculum（折线）
            var poly = new Polyline
            {
                Points = new PointCollection(),
                Stroke = st.Foreground,
                StrokeThickness = thk,
                StrokeLineJoin = PenLineJoin.Miter,
                StrokeStartLineCap = PenLineCap.Round,
            };
            double x0 = leftPad;
            poly.Points.Add(new Point(x0, Hc * 0.55));
            poly.Points.Add(new Point(x0 + rW * 0.25, Hc * 0.42));
            poly.Points.Add(new Point(x0 + rW * 0.5, Hc - thk / 2));
            poly.Points.Add(new Point(x0 + rW, thk / 2));
            poly.Points.Add(new Point(W, thk / 2));
            canvas.Children.Add(poly);

            if (content.Element != null)
            {
                Canvas.SetLeft(content.Element, leftPad + rW + pad);
                Canvas.SetTop(content.Element, topGap);
                canvas.Children.Add(content.Element);
            }
            if (index?.Element != null)
            {
                Canvas.SetLeft(index.Element, 0);
                Canvas.SetTop(index.Element, Hc * 0.20);
                canvas.Children.Add(index.Element);
            }

            double ascent = topGap + content.Ascent;
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = Hc - ascent, Cls = AtomClass.Ord };
        }

        // ── 重音 ────────────────────────────────────────────────────────────

        private static Box AccentBox(Box content, string accent, Style st, bool stretch)
        {
            var accStyle = st.Clone();
            accStyle.FontSize = st.FontSize * (stretch ? 0.9 : 0.8);
            var accBox = TextBox(accent, accStyle, upright: true);
            double gap = st.FontSize * 0.02;
            double accH = accBox.Height;
            double W = Math.Max(content.Width, accBox.Width);

            double H = accH + gap + content.Height;
            var canvas = new Canvas { Width = W, Height = H };

            Canvas.SetLeft(accBox.Element, (W - accBox.Width) / 2);
            Canvas.SetTop(accBox.Element, 0);
            canvas.Children.Add(accBox.Element);

            double contentTop = accH + gap;
            if (content.Element != null)
            {
                Canvas.SetLeft(content.Element, (W - content.Width) / 2);
                Canvas.SetTop(content.Element, contentTop);
                canvas.Children.Add(content.Element);
            }
            double ascent = contentTop + content.Ascent;
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = H - ascent, Cls = content.Cls };
        }

        // 上/下横线（\overline、\underline、\bar 退化）
        private static Box OverUnderLineBox(Box content, Style st, bool over)
        {
            double thk = Math.Max(1, st.FontSize * 0.055);
            double gap = st.FontSize * 0.12;
            double W = content.Width;
            double H = content.Height + gap + thk;
            var canvas = new Canvas { Width = W, Height = H };

            double contentTop = over ? gap + thk : 0;
            if (content.Element != null)
            {
                Canvas.SetLeft(content.Element, 0);
                Canvas.SetTop(content.Element, contentTop);
                canvas.Children.Add(content.Element);
            }
            var line = new Rectangle { Width = W, Height = thk, Fill = st.Foreground };
            Canvas.SetLeft(line, 0);
            Canvas.SetTop(line, over ? 0 : content.Height + gap);
            canvas.Children.Add(line);

            double ascent = contentTop + content.Ascent;
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = H - ascent, Cls = content.Cls };
        }

        // 上方箭头（\vec、\overrightarrow、\overleftarrow）
        private static Box ArrowOverBox(Box content, Style st, bool rightward)
        {
            double thk = Math.Max(1, st.FontSize * 0.05);
            double gap = st.FontSize * 0.1;
            double headH = st.FontSize * 0.16;
            double arrowZone = headH + thk;
            double W = Math.Max(content.Width, st.FontSize * 0.5);
            double H = arrowZone + gap + content.Height;
            var canvas = new Canvas { Width = W, Height = H };

            double ay = headH / 2;
            var shaft = new Line { X1 = 0, Y1 = ay, X2 = W, Y2 = ay, Stroke = st.Foreground, StrokeThickness = thk };
            canvas.Children.Add(shaft);
            var head = new Polyline { Points = new PointCollection(), Stroke = st.Foreground, StrokeThickness = thk, StrokeLineJoin = PenLineJoin.Round };
            if (rightward)
            {
                head.Points.Add(new Point(W - headH, ay - headH / 2));
                head.Points.Add(new Point(W, ay));
                head.Points.Add(new Point(W - headH, ay + headH / 2));
            }
            else
            {
                head.Points.Add(new Point(headH, ay - headH / 2));
                head.Points.Add(new Point(0, ay));
                head.Points.Add(new Point(headH, ay + headH / 2));
            }
            canvas.Children.Add(head);

            double contentTop = arrowZone + gap;
            if (content.Element != null)
            {
                Canvas.SetLeft(content.Element, (W - content.Width) / 2);
                Canvas.SetTop(content.Element, contentTop);
                canvas.Children.Add(content.Element);
            }
            double ascent = contentTop + content.Ascent;
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = H - ascent, Cls = content.Cls };
        }

        // ── 定界符（\left \right、矩阵外框）─────────────────────────────────────

        private static Box DelimBox(string left, Box content, string right, Style st)
        {
            double pad = st.FontSize * 0.1;
            double targetH = content.Height + 2 * pad;
            var lb = MakeDelim(left, targetH, st);
            var rb = MakeDelim(right, targetH, st);

            double lw = lb?.Width ?? 0;
            double rw = rb?.Width ?? 0;
            double W = lw + content.Width + rw;
            double H = targetH;
            double ascent = content.Ascent + pad;

            var canvas = new Canvas { Width = W, Height = H };
            if (lb?.Element != null)
            {
                Canvas.SetLeft(lb.Element, 0);
                Canvas.SetTop(lb.Element, 0);
                canvas.Children.Add(lb.Element);
            }
            if (content.Element != null)
            {
                Canvas.SetLeft(content.Element, lw);
                Canvas.SetTop(content.Element, pad);
                canvas.Children.Add(content.Element);
            }
            if (rb?.Element != null)
            {
                Canvas.SetLeft(rb.Element, lw + content.Width);
                Canvas.SetTop(rb.Element, 0);
                canvas.Children.Add(rb.Element);
            }
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = H - ascent, Cls = AtomClass.Inner };
        }

        // 生成一个高度为 h 的定界符（"." 表示无）
        private static Box MakeDelim(string d, double h, Style st)
        {
            if (string.IsNullOrEmpty(d) || d == ".") return null;
            double thk = Math.Max(1.2, st.FontSize * 0.06);

            // 竖线类：直接画矩形
            if (d == "|" )
                return ShapeBox(new Rectangle { Width = thk, Height = h, Fill = st.Foreground }, thk, h);
            if (d == "∥") // 双竖线
            {
                var cv = new Canvas { Width = thk * 3, Height = h };
                var l1 = new Rectangle { Width = thk, Height = h, Fill = st.Foreground };
                var l2 = new Rectangle { Width = thk, Height = h, Fill = st.Foreground };
                Canvas.SetLeft(l1, 0); Canvas.SetLeft(l2, thk * 2);
                cv.Children.Add(l1); cv.Children.Add(l2);
                return ShapeBox(cv, thk * 3, h);
            }
            // 方括号：三段折线
            if (d == "[" || d == "]")
            {
                double w = st.FontSize * 0.28;
                var poly = new Polyline { Points = new PointCollection(), Stroke = st.Foreground, StrokeThickness = thk };
                if (d == "[")
                {
                    poly.Points.Add(new Point(w, 0));
                    poly.Points.Add(new Point(thk / 2, 0));
                    poly.Points.Add(new Point(thk / 2, h));
                    poly.Points.Add(new Point(w, h));
                }
                else
                {
                    poly.Points.Add(new Point(0, 0));
                    poly.Points.Add(new Point(w - thk / 2, 0));
                    poly.Points.Add(new Point(w - thk / 2, h));
                    poly.Points.Add(new Point(0, h));
                }
                return ShapeBox(poly, w, h);
            }

            // 其余（圆括号、花括号、尖括号、地板/天花板）：竖向拉伸字形
            var tb = new TextBlock
            {
                Text = d, FontFamily = MathFont, FontSize = st.FontSize, Foreground = st.Foreground,
                TextWrapping = TextWrapping.NoWrap,
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double natH = tb.DesiredSize.Height > 0 ? tb.DesiredSize.Height : st.FontSize;
            double natW = tb.DesiredSize.Width;
            double scaleY = h / natH;
            tb.RenderTransform = new ScaleTransform { ScaleY = scaleY, CenterY = 0 };
            // 拉伸后高度变为 h；宽度不变
            var holder = new Canvas { Width = natW, Height = h };
            Canvas.SetLeft(tb, 0); Canvas.SetTop(tb, 0);
            holder.Children.Add(tb);
            return new Box { Element = holder, Width = natW, Ascent = h, Descent = 0 };
        }

        private static Box ShapeBox(FrameworkElement el, double w, double h)
        {
            el.Width = w; el.Height = h;
            return new Box { Element = el, Width = w, Ascent = h, Descent = 0 };
        }

        // ── 矩阵 ────────────────────────────────────────────────────────────

        private static Box MatrixBox(List<List<Box>> rows, Style st, bool center)
        {
            int rowCount = rows.Count;
            int colCount = 0;
            foreach (var r in rows) colCount = Math.Max(colCount, r.Count);
            if (colCount == 0) return Empty();

            var colW = new double[colCount];
            var rowAsc = new double[rowCount];
            var rowDesc = new double[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                for (int j = 0; j < rows[i].Count; j++)
                {
                    var cell = rows[i][j];
                    colW[j] = Math.Max(colW[j], cell.Width);
                    rowAsc[i] = Math.Max(rowAsc[i], cell.Ascent);
                    rowDesc[i] = Math.Max(rowDesc[i], cell.Descent);
                }
                if (rowAsc[i] + rowDesc[i] == 0) rowAsc[i] = st.FontSize * 0.7; // 空行兜底
            }

            double colGap = st.FontSize * 0.8;
            double rowGap = st.FontSize * 0.35;

            double totalW = 0;
            var colX = new double[colCount];
            for (int j = 0; j < colCount; j++)
            {
                colX[j] = totalW;
                totalW += colW[j];
                if (j < colCount - 1) totalW += colGap;
            }
            double totalH = 0;
            var rowY = new double[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                rowY[i] = totalH;
                totalH += rowAsc[i] + rowDesc[i];
                if (i < rowCount - 1) totalH += rowGap;
            }

            var canvas = new Canvas { Width = totalW, Height = totalH };
            for (int i = 0; i < rowCount; i++)
            {
                for (int j = 0; j < rows[i].Count; j++)
                {
                    var cell = rows[i][j];
                    if (cell.Element == null) continue;
                    double x = center ? colX[j] + (colW[j] - cell.Width) / 2 : colX[j];
                    double y = rowY[i] + (rowAsc[i] - cell.Ascent);
                    Canvas.SetLeft(cell.Element, x);
                    Canvas.SetTop(cell.Element, y);
                    canvas.Children.Add(cell.Element);
                }
            }

            double axis = st.FontSize * AxisFactor;
            double ascent = totalH / 2 + axis;
            return new Box { Element = canvas, Width = totalW, Ascent = ascent, Descent = totalH - ascent, Cls = AtomClass.Inner };
        }

        // ── 垂直堆叠（多行公式块）──────────────────────────────────────────────

        private static Box VStack(List<Box> rows, Style st, bool center)
        {
            double W = 0;
            foreach (var r in rows) W = Math.Max(W, r.Width);
            double rowGap = st.FontSize * 0.4;
            double H = 0;
            var ys = new double[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                ys[i] = H;
                H += rows[i].Height;
                if (i < rows.Count - 1) H += rowGap;
            }
            var canvas = new Canvas { Width = W, Height = H };
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Element == null) continue;
                Canvas.SetLeft(r.Element, center ? (W - r.Width) / 2 : 0);
                Canvas.SetTop(r.Element, ys[i]);
                canvas.Children.Add(r.Element);
            }
            // 整体基线取首行基线
            double ascent = rows.Count > 0 ? rows[0].Ascent : H;
            return new Box { Element = canvas, Width = W, Ascent = ascent, Descent = H - ascent, Cls = AtomClass.Ord };
        }

        // ── 原子分类（决定间距）────────────────────────────────────────────────

        private static AtomClass ClassOfChar(char ch)
        {
            switch (ch)
            {
                case '+': case '-': case '*': return AtomClass.Bin;
                case '=': case '<': case '>': return AtomClass.Rel;
                case '(': case '[': return AtomClass.Open;
                case ')': case ']': return AtomClass.Close;
                case ',': case ';': return AtomClass.Punct;
                default: return AtomClass.Ord;
            }
        }

        private static AtomClass ClassOf(string cmd)
        {
            if (RelCmds.Contains(cmd)) return AtomClass.Rel;
            if (BinCmds.Contains(cmd)) return AtomClass.Bin;
            return AtomClass.Ord;
        }

        // ── 数据表 ──────────────────────────────────────────────────────────

        private static readonly HashSet<string> Functions = new HashSet<string>
        {
            "sin","cos","tan","cot","sec","csc","sinh","cosh","tanh","coth",
            "arcsin","arccos","arctan","log","ln","lg","exp","deg","dim","hom",
            "ker","arg","det","gcd","Pr","lim","limsup","liminf","max","min","sup","inf",
        };
        private static readonly HashSet<string> LimitFuncs = new HashSet<string>
        {
            "lim","limsup","liminf","max","min","sup","inf","det","gcd","Pr",
        };

        private static readonly Dictionary<string, string> BigOps = new Dictionary<string, string>
        {
            ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐",
            ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭",
            ["oint"] = "∮", ["bigcup"] = "⋃", ["bigcap"] = "⋂",
            ["bigvee"] = "⋁", ["bigwedge"] = "⋀", ["bigsqcup"] = "⨆",
            ["biguplus"] = "⨄", ["bigoplus"] = "⨁", ["bigotimes"] = "⨂",
            ["bigodot"] = "⨀",
        };

        private static readonly HashSet<string> RelCmds = new HashSet<string>
        {
            "leq","le","geq","ge","neq","ne","equiv","approx","cong","sim","simeq",
            "propto","ll","gg","prec","succ","preceq","succeq","subset","supset",
            "subseteq","supseteq","sqsubseteq","sqsupseteq","in","ni","notin",
            "mid","parallel","perp","models","vdash","dashv","doteq","asymp",
            "leftarrow","rightarrow","Leftarrow","Rightarrow","leftrightarrow",
            "Leftrightarrow","mapsto","to","gets","implies","iff","longrightarrow",
            "longleftarrow","longleftrightarrow","hookrightarrow","hookleftarrow",
            "Longrightarrow","Longleftarrow","uparrow","downarrow","nearrow","searrow",
        };
        private static readonly HashSet<string> BinCmds = new HashSet<string>
        {
            "pm","mp","times","div","cdot","ast","star","circ","bullet","oplus",
            "ominus","otimes","oslash","odot","cup","cap","sqcup","sqcap","uplus",
            "wedge","vee","setminus","wr","amalg","diamond","bigtriangleup",
            "bigtriangledown","triangleleft","triangleright","dagger","ddagger",
        };

        private static readonly Dictionary<string, string> Symbols = new Dictionary<string, string>
        {
            // 小写希腊
            ["alpha"]="α",["beta"]="β",["gamma"]="γ",["delta"]="δ",
            ["epsilon"]="ϵ",["varepsilon"]="ε",["zeta"]="ζ",["eta"]="η",
            ["theta"]="θ",["vartheta"]="ϑ",["iota"]="ι",["kappa"]="κ",
            ["lambda"]="λ",["mu"]="μ",["nu"]="ν",["xi"]="ξ",
            ["omicron"]="ο",["pi"]="π",["varpi"]="ϖ",["rho"]="ρ",
            ["varrho"]="ϱ",["sigma"]="σ",["varsigma"]="ς",["tau"]="τ",
            ["upsilon"]="υ",["phi"]="ϕ",["varphi"]="φ",["chi"]="χ",
            ["psi"]="ψ",["omega"]="ω",
            // 大写希腊
            ["Gamma"]="Γ",["Delta"]="Δ",["Theta"]="Θ",["Lambda"]="Λ",
            ["Xi"]="Ξ",["Pi"]="Π",["Sigma"]="Σ",["Upsilon"]="Υ",
            ["Phi"]="Φ",["Psi"]="Ψ",["Omega"]="Ω",
            // 运算符（二元）
            ["pm"]="±",["mp"]="∓",["times"]="×",["div"]="÷",
            ["cdot"]="⋅",["ast"]="∗",["star"]="⋆",["circ"]="∘",
            ["bullet"]="∙",["oplus"]="⊕",["ominus"]="⊖",["otimes"]="⊗",
            ["oslash"]="⊘",["odot"]="⊙",["cup"]="∪",["cap"]="∩",
            ["sqcup"]="⊔",["sqcap"]="⊓",["uplus"]="⊎",["wedge"]="∧",
            ["vee"]="∨",["land"]="∧",["lor"]="∨",["setminus"]="∖",
            ["wr"]="≀",["amalg"]="⨿",["diamond"]="⋄",["dagger"]="†",
            ["ddagger"]="‡",["bigtriangleup"]="△",["bigtriangledown"]="▽",
            ["triangleleft"]="◁",["triangleright"]="▷",
            // 关系符
            ["leq"]="≤",["le"]="≤",["geq"]="≥",["ge"]="≥",
            ["neq"]="≠",["ne"]="≠",["equiv"]="≡",["approx"]="≈",
            ["cong"]="≅",["sim"]="∼",["simeq"]="≃",["propto"]="∝",
            ["ll"]="≪",["gg"]="≫",["prec"]="≺",["succ"]="≻",
            ["preceq"]="⪯",["succeq"]="⪰",["subset"]="⊂",["supset"]="⊃",
            ["subseteq"]="⊆",["supseteq"]="⊇",["sqsubseteq"]="⊑",
            ["sqsupseteq"]="⊒",["in"]="∈",["ni"]="∋",["notin"]="∉",
            ["mid"]="∣",["parallel"]="∥",["perp"]="⊥",["models"]="⊨",
            ["vdash"]="⊢",["dashv"]="⊣",["doteq"]="≐",["asymp"]="≍",
            // 箭头
            ["leftarrow"]="←",["gets"]="←",["rightarrow"]="→",["to"]="→",
            ["Leftarrow"]="⇐",["Rightarrow"]="⇒",["implies"]="⇒",
            ["leftrightarrow"]="↔",["Leftrightarrow"]="⇔",["iff"]="⇔",
            ["mapsto"]="↦",["longrightarrow"]="⟶",["longleftarrow"]="⟵",
            ["longleftrightarrow"]="⟷",["Longrightarrow"]="⟹",["Longleftarrow"]="⟸",
            ["hookrightarrow"]="↪",["hookleftarrow"]="↩",["uparrow"]="↑",
            ["downarrow"]="↓",["updownarrow"]="↕",["nearrow"]="↗",
            ["searrow"]="↘",["nwarrow"]="↖",["swarrow"]="↙",
            // 集合 / 逻辑 / 杂项
            ["forall"]="∀",["exists"]="∃",["nexists"]="∄",["neg"]="¬",
            ["lnot"]="¬",["emptyset"]="∅",["varnothing"]="∅",
            ["infty"]="∞",["partial"]="∂",["nabla"]="∇",["aleph"]="ℵ",
            ["hbar"]="ℏ",["ell"]="ℓ",["Re"]="ℜ",["Im"]="ℑ",
            ["wp"]="℘",["angle"]="∠",["triangle"]="△",["square"]="□",
            ["top"]="⊤",["bot"]="⊥",["surd"]="√",["flat"]="♭",
            ["natural"]="♮",["sharp"]="♯",["clubsuit"]="♣",
            ["diamondsuit"]="♦",["heartsuit"]="♥",["spadesuit"]="♠",
            ["prime"]="′",["backprime"]="‵",["dots"]="…",["ldots"]="…",
            ["cdots"]="⋯",["vdots"]="⋮",["ddots"]="⋱",["because"]="∵",
            ["therefore"]="∴",
            ["langle"]="⟨",["rangle"]="⟩",["lfloor"]="⌊",["rfloor"]="⌋",
            ["lceil"]="⌈",["rceil"]="⌉",["backslash"]="\\",["%"]="%",
            ["lbrace"]="{",["rbrace"]="}",["lbrack"]="[",["rbrack"]="]",
            ["cdotp"]="·",["colon"]=":",
        };

        // 变体字母例外（已占用 BMP 码位的双线体 / 花体 / 哥特体）
        private static readonly Dictionary<char, string> BbExceptions = new Dictionary<char, string>
        {
            ['C']="ℂ",['H']="ℍ",['N']="ℕ",['P']="ℙ",
            ['Q']="ℚ",['R']="ℝ",['Z']="ℤ",
        };
        private static readonly Dictionary<char, string> CalExceptions = new Dictionary<char, string>
        {
            ['B']="ℬ",['E']="ℰ",['F']="ℱ",['H']="ℋ",['I']="ℐ",
            ['L']="ℒ",['M']="ℳ",['R']="ℛ",['e']="ℯ",['g']="ℊ",['o']="ℴ",
        };
        private static readonly Dictionary<char, string> FrakExceptions = new Dictionary<char, string>
        {
            ['C']="ℭ",['H']="ℌ",['I']="ℑ",['R']="ℜ",['Z']="ℨ",
        };
    }
}
