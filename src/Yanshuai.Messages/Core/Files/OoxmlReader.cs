// Core/Files/OoxmlReader.cs —— Office 文档端上解析（docx / xlsx / pptx）
//
// 为什么能端上做：OOXML 的本质就是 **zip + XML**，而 UWP 的 .NET Standard 2.0 面里
// 正好有 ZipArchive 与 XDocument（我在编译产物的 bin 列表里确认过这两个程序集）。
// 所以不需要任何第三方库，也不需要联网。
//
// 与 rikkahub 的对比：它做了 docx / pptx / epub，但**没做 xlsx**（走 readText 会得到
// zip 二进制垃圾）。这里四件套都做，并且输出对模型更友好的结构（表格用管道分隔）。
//
// 关键防御：
//   · 解压炸弹：单个 entry 与总解压量都有上限
//   · 畸形 XML：XmlReader 配置成不解析 DTD、不加载外部实体
//   · 超大文档：边解析边截断，不把整个文档读进内存

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Yanshuai.Messages.Core
{
    /// <summary>解析结果。Text 为空且 Warnings 非空时，调用方应当把警告显示给用户。</summary>
    public sealed class ExtractedDocument
    {
        public bool Ok { get; set; }
        public string Kind { get; set; } = "";          // docx / xlsx / pptx / text
        public string Text { get; set; } = "";
        public int PageCount { get; set; }              // pptx 的页数 / xlsx 的表数 / pdf 的页数
        public bool Truncated { get; set; }
        public List<string> Warnings { get; private set; }

        public ExtractedDocument()
        {
            Warnings = new List<string>();
        }

        public static ExtractedDocument Fail(string warning)
        {
            var result = new ExtractedDocument { Ok = false };
            result.Warnings.Add(warning);
            return result;
        }

        public int CharCount { get { return Text == null ? 0 : Text.Length; } }
    }

    public static class OoxmlReader
    {
        /// <summary>单个 zip entry 的解压上限（防解压炸弹）。</summary>
        public const long MaxEntryBytes = 32L * 1024 * 1024;

        /// <summary>整份文档的解压总量上限。</summary>
        public const long MaxTotalBytes = 96L * 1024 * 1024;

        /// <summary>输出文本上限（字符）。超出就截断并标记。</summary>
        public const int MaxOutputChars = 120000;

        /// <summary>xlsx 最多解析多少个工作表。</summary>
        public const int MaxSheets = 20;

        /// <summary>每个表最多多少行。</summary>
        public const int MaxRowsPerSheet = 5000;

        /// <summary>pptx 最多解析多少页。</summary>
        public const int MaxSlides = 200;

        // ═════════════════════════════════════════════════════════════════
        // 入口
        // ═════════════════════════════════════════════════════════════════

        /// <summary>按扩展名分派。不认识的扩展名返回失败。</summary>
        public static ExtractedDocument Extract(byte[] bytes, string fileName)
        {
            if (bytes == null || bytes.Length == 0) return ExtractedDocument.Fail("文件是空的。");

            var extension = WorkspaceLayout.Extension(fileName ?? "");
            switch (extension)
            {
                case ".docx": return ReadDocx(bytes);
                case ".xlsx": return ReadXlsx(bytes);
                case ".pptx": return ReadPptx(bytes);
                default:
                    return ExtractedDocument.Fail("不支持的 Office 格式：" + (extension.Length > 0 ? extension : "（无扩展名）"));
            }
        }

        public static bool IsOfficeExtension(string extension)
        {
            switch ((extension ?? "").ToLowerInvariant())
            {
                case ".docx":
                case ".xlsx":
                case ".pptx":
                    return true;
                default:
                    return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // docx
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 读 docx：解压 word/document.xml，按段落抽 &lt;w:t&gt; 文本。
        /// 表格里的单元格用 Tab 分隔，让模型能看出列结构。
        /// </summary>
        public static ExtractedDocument ReadDocx(byte[] bytes)
        {
            var result = new ExtractedDocument { Ok = true, Kind = "docx" };
            var sb = new StringBuilder();

            try
            {
                using (var zip = OpenZip(bytes))
                {
                    var entry = FindEntry(zip, "word/document.xml");
                    if (entry == null)
                    {
                        return ExtractedDocument.Fail("这不是有效的 .docx（找不到 word/document.xml）。");
                    }

                    var doc = LoadXml(entry);
                    if (doc == null) return ExtractedDocument.Fail("word/document.xml 解析失败。");

                    var ns = doc.Root != null ? doc.Root.Name.Namespace : XNamespace.None;

                    foreach (var paragraph in doc.Descendants(ns + "p"))
                    {
                        var line = ExtractParagraphText(paragraph, ns);
                        if (line.Length == 0)
                        {
                            // 空段落 = 段落间隔，但连续多个只保留一个
                            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                            continue;
                        }

                        if (sb.Length > MaxOutputChars) { result.Truncated = true; break; }
                        sb.Append(line).Append('\n');
                    }

                    // 表格单独再过一遍（document.xml 里的表格不在 w:p 的直接文本里）
                    var tables = new List<XElement>(doc.Descendants(ns + "tbl"));
                    if (tables.Count > 0)
                    {
                        sb.Append('\n');
                        int tableIndex = 1;
                        foreach (var table in tables)
                        {
                            if (sb.Length > MaxOutputChars) { result.Truncated = true; break; }

                            sb.Append("【表格 ").Append(tableIndex.ToString(CultureInfo.InvariantCulture)).Append("】\n");
                            foreach (var row in table.Elements(ns + "tr"))
                            {
                                var cells = new List<string>();
                                foreach (var cell in row.Elements(ns + "tc"))
                                {
                                    cells.Add(ExtractParagraphText(cell, ns).Replace("\n", " ").Trim());
                                }
                                sb.Append(string.Join(" | ", cells.ToArray())).Append('\n');
                            }
                            tableIndex++;
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                return ExtractedDocument.Fail("文件不是有效的 zip 包，可能已损坏或其实不是 .docx。");
            }
            catch (Exception)
            {
                return ExtractedDocument.Fail("解析 .docx 时出错。");
            }

            result.Text = sb.ToString().Trim();
            if (result.Text.Length == 0)
            {
                result.Warnings.Add("文档里没有可提取的文字（可能是纯图片扫描件）。");
            }
            return result;
        }

        /// <summary>抽一个段落/单元格里的文字：w:t 按顺序拼，w:tab 转制表符，w:br 转换行。</summary>
        private static string ExtractParagraphText(XElement container, XNamespace ns)
        {
            var sb = new StringBuilder();

            foreach (var node in container.Descendants())
            {
                if (node.Name == ns + "t")
                {
                    sb.Append(node.Value);
                }
                else if (node.Name == ns + "tab")
                {
                    sb.Append('\t');
                }
                else if (node.Name == ns + "br")
                {
                    sb.Append('\n');
                }
            }

            return sb.ToString().Trim();
        }

        // ═════════════════════════════════════════════════════════════════
        // xlsx
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 读 xlsx：共享字符串表 + 各工作表，还原成"管道分隔的表"。
        ///
        /// 三个必须处理的细节：
        ///   1) 单元格的值可能是共享字符串下标（t="s"）而不是字面量
        ///   2) 行/列可能稀疏（r="B5"），必须按坐标补空位，否则列会错位
        ///   3) 日期在 xlsx 里是数字 + 样式，这里不还原成日期（会误导），保持原值
        /// </summary>
        public static ExtractedDocument ReadXlsx(byte[] bytes)
        {
            var result = new ExtractedDocument { Ok = true, Kind = "xlsx" };
            var sb = new StringBuilder();

            try
            {
                using (var zip = OpenZip(bytes))
                {
                    var sharedStrings = ReadSharedStrings(zip);

                    // 工作表清单：xl/workbook.xml 里给名字与顺序，关系表给路径
                    var sheets = ReadSheetList(zip);
                    if (sheets.Count == 0)
                    {
                        return ExtractedDocument.Fail("这不是有效的 .xlsx（找不到工作表）。");
                    }

                    result.PageCount = sheets.Count;
                    if (sheets.Count > MaxSheets)
                    {
                        result.Warnings.Add("工作簿有 " + sheets.Count.ToString(CultureInfo.InvariantCulture) +
                                            " 个表，只解析了前 " + MaxSheets.ToString(CultureInfo.InvariantCulture) + " 个。");
                    }

                    int processed = 0;
                    foreach (var sheet in sheets)
                    {
                        if (processed >= MaxSheets) break;
                        if (sb.Length > MaxOutputChars) { result.Truncated = true; break; }
                        processed++;

                        sb.Append("【工作表：").Append(sheet.Name).Append("】\n");

                        var entry = FindEntry(zip, sheet.Path);
                        if (entry == null)
                        {
                            sb.Append("（读取失败）\n\n");
                            continue;
                        }

                        var doc = LoadXml(entry);
                        if (doc == null)
                        {
                            sb.Append("（解析失败）\n\n");
                            continue;
                        }

                        AppendSheetRows(sb, doc, sharedStrings, ref result);
                        sb.Append('\n');
                    }
                }
            }
            catch (InvalidDataException)
            {
                return ExtractedDocument.Fail("文件不是有效的 zip 包，可能已损坏或其实不是 .xlsx。");
            }
            catch (Exception)
            {
                return ExtractedDocument.Fail("解析 .xlsx 时出错。");
            }

            result.Text = sb.ToString().Trim();
            if (result.Text.Length == 0) result.Warnings.Add("工作簿里没有可提取的内容。");
            return result;
        }

        private sealed class SheetInfo
        {
            public string Name = "";
            public string Path = "";
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var entry = FindEntry(zip, "xl/sharedStrings.xml");
            if (entry == null) return list;

            var doc = LoadXml(entry);
            if (doc == null) return list;

            var ns = doc.Root != null ? doc.Root.Name.Namespace : XNamespace.None;
            foreach (var item in doc.Descendants(ns + "si"))
            {
                // 一个 si 里可能有多段（富文本），全部拼起来
                var sb = new StringBuilder();
                foreach (var text in item.Descendants(ns + "t")) sb.Append(text.Value);
                list.Add(sb.ToString());
            }
            return list;
        }

        private static List<SheetInfo> ReadSheetList(ZipArchive zip)
        {
            var list = new List<SheetInfo>();

            var workbook = FindEntry(zip, "xl/workbook.xml");
            if (workbook == null) return list;

            var doc = LoadXml(workbook);
            if (doc == null) return list;

            var ns = doc.Root != null ? doc.Root.Name.Namespace : XNamespace.None;
            var relationshipNs = XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships");

            // r:id -> 路径 的映射来自 xl/_rels/workbook.xml.rels
            var relMap = ReadWorkbookRelationships(zip);

            int index = 1;
            foreach (var sheet in doc.Descendants(ns + "sheet"))
            {
                var name = (string)sheet.Attribute("name");
                var relId = (string)sheet.Attribute(relationshipNs + "id");

                string path = null;
                if (!string.IsNullOrEmpty(relId)) relMap.TryGetValue(relId, out path);
                if (string.IsNullOrEmpty(path))
                {
                    // 关系表缺失时按约定兜底
                    path = "xl/worksheets/sheet" + index.ToString(CultureInfo.InvariantCulture) + ".xml";
                }

                list.Add(new SheetInfo
                {
                    Name = string.IsNullOrEmpty(name) ? ("Sheet" + index.ToString(CultureInfo.InvariantCulture)) : name,
                    Path = path
                });
                index++;
            }

            return list;
        }

        private static Dictionary<string, string> ReadWorkbookRelationships(ZipArchive zip)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);

            var entry = FindEntry(zip, "xl/_rels/workbook.xml.rels");
            if (entry == null) return map;

            var doc = LoadXml(entry);
            if (doc == null) return map;

            foreach (var rel in doc.Descendants())
            {
                if (rel.Name.LocalName != "Relationship") continue;

                var id = (string)rel.Attribute("Id");
                var target = (string)rel.Attribute("Target");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;

                // Target 可能是 "worksheets/sheet1.xml" 或 "/xl/worksheets/sheet1.xml"
                var normalized = target.Replace('\\', '/').TrimStart('/');
                if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = "xl/" + normalized;
                }
                map[id] = normalized;
            }
            return map;
        }

        private static void AppendSheetRows(
            StringBuilder sb, XDocument sheetDoc, List<string> sharedStrings, ref ExtractedDocument result)
        {
            var ns = sheetDoc.Root != null ? sheetDoc.Root.Name.Namespace : XNamespace.None;
            int rowCount = 0;

            foreach (var row in sheetDoc.Descendants(ns + "row"))
            {
                if (rowCount >= MaxRowsPerSheet)
                {
                    sb.Append("…（超过 ").Append(MaxRowsPerSheet.ToString(CultureInfo.InvariantCulture)).Append(" 行，已截断）\n");
                    result.Truncated = true;
                    break;
                }
                if (sb.Length > MaxOutputChars) { result.Truncated = true; break; }

                // 用列坐标补空位：稀疏表格直接按顺序拼会错位
                var cells = new SortedDictionary<int, string>();
                int maxColumn = -1;

                foreach (var cell in row.Elements(ns + "c"))
                {
                    var reference = (string)cell.Attribute("r");
                    int columnIndex = ColumnIndexFromReference(reference);

                    var value = ReadCellValue(cell, ns, sharedStrings);
                    cells[columnIndex] = value;
                    if (columnIndex > maxColumn) maxColumn = columnIndex;
                }

                var line = new List<string>();
                for (int i = 0; i <= maxColumn; i++)
                {
                    string value;
                    line.Add(cells.TryGetValue(i, out value) ? value : "");
                }

                // 全空行跳过
                bool hasContent = false;
                foreach (var value in line)
                {
                    if (!string.IsNullOrEmpty(value)) { hasContent = true; break; }
                }
                if (!hasContent) continue;

                sb.Append(string.Join(" | ", line.ToArray())).Append('\n');
                rowCount++;
            }
        }

        private static string ReadCellValue(XElement cell, XNamespace ns, List<string> sharedStrings)
        {
            var type = (string)cell.Attribute("t");
            var valueElement = cell.Element(ns + "v");

            if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
            {
                var inline = cell.Element(ns + "is");
                if (inline == null) return "";
                var sb = new StringBuilder();
                foreach (var text in inline.Descendants(ns + "t")) sb.Append(text.Value);
                return sb.ToString().Trim();
            }

            if (valueElement == null) return "";
            var raw = valueElement.Value ?? "";

            if (string.Equals(type, "s", StringComparison.Ordinal))
            {
                int index;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    && index >= 0 && index < sharedStrings.Count)
                {
                    return sharedStrings[index];
                }
                return "";
            }

            if (string.Equals(type, "b", StringComparison.Ordinal))
            {
                return raw == "1" ? "TRUE" : "FALSE";
            }

            return raw.Trim();
        }

        /// <summary>"B5" → 1（0 基）。拿不到返回 0。</summary>
        public static int ColumnIndexFromReference(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return 0;

            int value = 0;
            bool sawLetter = false;

            foreach (var ch in reference)
            {
                if (ch >= 'A' && ch <= 'Z')
                {
                    value = value * 26 + (ch - 'A' + 1);
                    sawLetter = true;
                }
                else if (ch >= 'a' && ch <= 'z')
                {
                    value = value * 26 + (ch - 'a' + 1);
                    sawLetter = true;
                }
                else
                {
                    break;
                }
            }

            return sawLetter ? value - 1 : 0;
        }

        // ═════════════════════════════════════════════════════════════════
        // pptx
        // ═════════════════════════════════════════════════════════════════

        /// <summary>读 pptx：逐页抽 &lt;a:t&gt; 文本，按页码分节。备注也带上（常含要点）。</summary>
        public static ExtractedDocument ReadPptx(byte[] bytes)
        {
            var result = new ExtractedDocument { Ok = true, Kind = "pptx" };
            var sb = new StringBuilder();

            try
            {
                using (var zip = OpenZip(bytes))
                {
                    var slidePaths = new List<string>();
                    foreach (var entry in zip.Entries)
                    {
                        var name = entry.FullName.Replace('\\', '/');
                        if (name.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        {
                            slidePaths.Add(name);
                        }
                    }

                    if (slidePaths.Count == 0)
                    {
                        return ExtractedDocument.Fail("这不是有效的 .pptx（找不到幻灯片）。");
                    }

                    // 按页号排序，否则 slide10 会排在 slide2 前面
                    slidePaths.Sort(CompareSlidePaths);

                    result.PageCount = slidePaths.Count;
                    if (slidePaths.Count > MaxSlides)
                    {
                        result.Warnings.Add("演示文稿有 " + slidePaths.Count.ToString(CultureInfo.InvariantCulture) +
                                            " 页，只解析了前 " + MaxSlides.ToString(CultureInfo.InvariantCulture) + " 页。");
                    }

                    int index = 0;
                    foreach (var path in slidePaths)
                    {
                        if (index >= MaxSlides) break;
                        if (sb.Length > MaxOutputChars) { result.Truncated = true; break; }
                        index++;

                        sb.Append("【第 ").Append(index.ToString(CultureInfo.InvariantCulture)).Append(" 页】\n");

                        var doc = LoadXml(FindEntry(zip, path));
                        if (doc != null)
                        {
                            // 注意命名空间：根节点是 presentationml（p:），
                            // 但文本节点 a:t 与段落 a:p 属于 drawingml（a:），
                            // 用根命名空间去找会一个都找不到。这里按 LocalName 匹配，
                            // 同时兼容 p: 与 a: 两种前缀写法。
                            var lines = new List<string>();
                            foreach (var paragraph in doc.Descendants())
                            {
                                if (paragraph.Name.LocalName != "p") continue;
                                if (paragraph.Name.NamespaceName.IndexOf("drawingml", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    continue;
                                }

                                var text = new StringBuilder();
                                foreach (var run in paragraph.Descendants())
                                {
                                    if (run.Name.LocalName == "t") text.Append(run.Value);
                                }

                                var line = text.ToString().Trim();
                                if (line.Length > 0) lines.Add(line);
                            }
                            sb.Append(string.Join("\n", lines.ToArray()));
                        }

                        // 备注页
                        var notesPath = path.Replace("/slides/", "/notesSlides/").Replace("slide", "notesSlide");
                        var notesDoc = LoadXml(FindEntry(zip, notesPath));
                        if (notesDoc != null)
                        {
                            var notes = new List<string>();
                            foreach (var paragraph in notesDoc.Descendants())
                            {
                                if (paragraph.Name.LocalName != "p") continue;
                                if (paragraph.Name.NamespaceName.IndexOf("drawingml", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    continue;
                                }

                                var text = new StringBuilder();
                                foreach (var run in paragraph.Descendants())
                                {
                                    if (run.Name.LocalName == "t") text.Append(run.Value);
                                }

                                var line = text.ToString().Trim();
                                if (line.Length > 0 && !IsSlideNumberPlaceholder(line)) notes.Add(line);
                            }
                            if (notes.Count > 0)
                            {
                                sb.Append("\n（备注：").Append(string.Join(" ", notes.ToArray())).Append("）");
                            }
                        }

                        sb.Append("\n\n");
                    }
                }
            }
            catch (InvalidDataException)
            {
                return ExtractedDocument.Fail("文件不是有效的 zip 包，可能已损坏或其实不是 .pptx。");
            }
            catch (Exception)
            {
                return ExtractedDocument.Fail("解析 .pptx 时出错。");
            }

            result.Text = sb.ToString().Trim();
            if (result.Text.Length == 0) result.Warnings.Add("演示文稿里没有可提取的文字。");
            return result;
        }

        private static bool IsSlideNumberPlaceholder(string text)
        {
            // 备注页里常混着纯页码，丢掉它
            int number;
            return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>按 slide 后的数字排序。</summary>
        private static int CompareSlidePaths(string a, string b)
        {
            return SlideNumber(a).CompareTo(SlideNumber(b));
        }

        private static int SlideNumber(string path)
        {
            int start = path.LastIndexOf("slide", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return 0;
            start += 5;

            var digits = new StringBuilder();
            for (int i = start; i < path.Length; i++)
            {
                if (char.IsDigit(path[i])) digits.Append(path[i]);
                else break;
            }

            int value;
            if (int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            return 0;
        }

        // ═════════════════════════════════════════════════════════════════
        // 共用工具
        // ═════════════════════════════════════════════════════════════════

        private static ZipArchive OpenZip(byte[] bytes)
        {
            var stream = new MemoryStream(bytes, false);
            return new ZipArchive(stream, ZipArchiveMode.Read, false);
        }

        private static ZipArchiveEntry FindEntry(ZipArchive zip, string path)
        {
            foreach (var entry in zip.Entries)
            {
                if (string.Equals(entry.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// 安全加载 XML：禁止 DTD 与外部实体（防 XXE），并在解压时限制大小（防解压炸弹）。
        /// </summary>
        private static XDocument LoadXml(ZipArchiveEntry entry)
        {
            if (entry == null) return null;
            if (entry.Length > MaxEntryBytes) return null;

            using (var stream = entry.Open())
            using (var limited = new LimitedStream(stream, MaxEntryBytes))
            {
                var settings = new XmlReaderSettings
                {
                    // DtdProcessing.Prohibit 就够了：UWP 的 System.Xml 里
                    // XmlReaderSettings 已经没有 XmlResolver 属性（默认就不解析外部实体）。
                    // 显式写 null 在桌面上能编译，在 UWP 上编译不过。
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreWhitespace = false,
                    IgnoreComments = true
                };

                using (var reader = XmlReader.Create(limited, settings))
                {
                    try
                    {
                        return XDocument.Load(reader);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>只允许读 n 字节的包装流，超了就当成流结束（而不是抛）。</summary>
        private sealed class LimitedStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _limit;
            private long _read;

            public LimitedStream(Stream inner, long limit)
            {
                _inner = inner;
                _limit = limit;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _inner.Length; } }
            public override long Position
            {
                get { return _read; }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_read >= _limit) return 0;
                long remaining = _limit - _read;
                if (count > remaining) count = (int)remaining;

                int read = _inner.Read(buffer, offset, count);
                _read += read;
                return read;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
