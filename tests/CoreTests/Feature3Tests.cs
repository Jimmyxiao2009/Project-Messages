// tests/CoreTests/Feature3Tests.cs —— 文档解析（Office 三件套）
//
// 测试用的 docx/xlsx/pptx 都是**现场合成**的最小合法包（zip + XML），
// 不往仓库塞二进制样本。这样测试自解释：读测试就能知道解析器期望什么结构。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature3Tests
    {
        public static void Register()
        {
            RegisterDocx();
            RegisterXlsx();
            RegisterPptx();
            RegisterDefense();
        }

        // ═════════════ docx ═════════════
        private static void RegisterDocx()
        {
            Runner.Add("docx", "抽取段落文字并保持段落分隔", delegate
            {
                var docx = BuildDocx(new[]
                {
                    "<w:p><w:r><w:t>第一段</w:t></w:r></w:p>",
                    "<w:p><w:r><w:t>第二段</w:t></w:r></w:p>",
                    "<w:p><w:r><w:t>第三段</w:t></w:r></w:p>"
                });

                var result = OoxmlReader.ReadDocx(docx);

                Check.True(result.Ok, "解析成功");
                Check.Equal("docx", result.Kind, "类型");
                Check.Contains(result.Text, "第一段", "第一段");
                Check.Contains(result.Text, "第三段", "第三段");

                // 顺序必须保持
                int first = result.Text.IndexOf("第一段", StringComparison.Ordinal);
                int third = result.Text.IndexOf("第三段", StringComparison.Ordinal);
                Check.True(first < third, "段落顺序正确");
            });

            Runner.Add("docx", "同一个段落里的多个 run 拼起来", delegate
            {
                var docx = BuildDocx(new[]
                {
                    "<w:p><w:r><w:t>前半</w:t></w:r><w:r><w:t>后半</w:t></w:r></w:p>"
                });

                var result = OoxmlReader.ReadDocx(docx);
                Check.Contains(result.Text, "前半后半", "run 按顺序拼接");
            });

            Runner.Add("docx", "表格按管道分隔输出", delegate
            {
                var table = "<w:tbl>" +
                    "<w:tr><w:tc><w:p><w:r><w:t>姓名</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>年龄</w:t></w:r></w:p></w:tc></w:tr>" +
                    "<w:tr><w:tc><w:p><w:r><w:t>小明</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>18</w:t></w:r></w:p></w:tc></w:tr>" +
                    "</w:tbl>";

                var docx = BuildDocx(new[] { "<w:p><w:r><w:t>表格如下</w:t></w:r></w:p>", table });
                var result = OoxmlReader.ReadDocx(docx);

                Check.Contains(result.Text, "姓名 | 年龄", "表头用管道分隔");
                Check.Contains(result.Text, "小明 | 18", "数据行");
                Check.Contains(result.Text, "表格 1", "表格编号");
            });

            Runner.Add("docx", "制表符与换行被还原", delegate
            {
                var docx = BuildDocx(new[]
                {
                    "<w:p><w:r><w:t>列一</w:t><w:tab/><w:t>列二</w:t></w:r></w:p>",
                    "<w:p><w:r><w:t>上行</w:t><w:br/><w:t>下行</w:t></w:r></w:p>"
                });

                var result = OoxmlReader.ReadDocx(docx);
                Check.Contains(result.Text, "列一\t列二", "tab 还原成制表符");
                Check.Contains(result.Text, "上行\n下行", "br 还原成换行");
            });

            Runner.Add("docx", "缺 document.xml 时给出可读原因", delegate
            {
                var broken = BuildZip(new Dictionary<string, string>
                {
                    { "word/other.xml", "<root/>" }
                });

                var result = OoxmlReader.ReadDocx(broken);
                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Warnings[0], "document.xml", "说明缺什么");
            });
        }

        // ═════════════ xlsx ═════════════
        private static void RegisterXlsx()
        {
            Runner.Add("xlsx", "共享字符串与数字混合的表格", delegate
            {
                var xlsx = BuildXlsx(
                    new[] { "姓名", "年龄", "城市" },
                    new[]
                    {
                        // t="s" 表示值是共享字符串下标
                        "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>",
                        "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>3</v></c><c r=\"B2\"><v>18</v></c><c r=\"C2\" t=\"s\"><v>4</v></c></row>"
                    },
                    new[] { "姓名", "年龄", "城市", "小明", "北京" },
                    "数据表");

                var result = OoxmlReader.ReadXlsx(xlsx);

                Check.True(result.Ok, "解析成功");
                Check.Equal("xlsx", result.Kind, "类型");
                Check.EqualInt(1, result.PageCount, "一个工作表");
                Check.Contains(result.Text, "工作表：数据表", "工作表名");
                Check.Contains(result.Text, "姓名 | 年龄 | 城市", "表头");
                Check.Contains(result.Text, "小明 | 18 | 北京", "数据行（字符串与数字混排）");
            });

            Runner.Add("xlsx", "稀疏单元格按列坐标补空位", delegate
            {
                // 只有 A 列和 C 列有值，B 列是空的
                var xlsx = BuildXlsx(
                    new[] { "A", "C" },
                    new[]
                    {
                        "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\" t=\"s\"><v>1</v></c></row>"
                    },
                    new[] { "第一列", "第三列" },
                    "稀疏表");

                var result = OoxmlReader.ReadXlsx(xlsx);
                Check.Contains(result.Text, "第一列 |  | 第三列", "空列补成空字符串，不右移");
            });

            Runner.Add("xlsx", "多个工作表都解析", delegate
            {
                var xlsx = BuildXlsxMultiSheet(
                    new[] { "表一", "表二" },
                    new[]
                    {
                        "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>表一内容</t></is></c></row>",
                        "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>表二内容</t></is></c></row>"
                    });

                var result = OoxmlReader.ReadXlsx(xlsx);
                Check.EqualInt(2, result.PageCount, "两个表");
                Check.Contains(result.Text, "表一内容", "第一个表");
                Check.Contains(result.Text, "表二内容", "第二个表");
            });

            Runner.Add("xlsx", "布尔值转成 TRUE/FALSE", delegate
            {
                var xlsx = BuildXlsx(
                    new[] { "flag" },
                    new[] { "<row r=\"1\"><c r=\"A1\" t=\"b\"><v>1</v></c></row>" },
                    new string[0],
                    "布尔表");

                var result = OoxmlReader.ReadXlsx(xlsx);
                Check.Contains(result.Text, "TRUE", "布尔值");
            });

            Runner.Add("xlsx", "列坐标换算正确", delegate
            {
                Check.EqualInt(0, OoxmlReader.ColumnIndexFromReference("A1"), "A");
                Check.EqualInt(1, OoxmlReader.ColumnIndexFromReference("B2"), "B");
                Check.EqualInt(25, OoxmlReader.ColumnIndexFromReference("Z9"), "Z");
                Check.EqualInt(26, OoxmlReader.ColumnIndexFromReference("AA1"), "AA");
                Check.EqualInt(27, OoxmlReader.ColumnIndexFromReference("AB100"), "AB");
            });
        }

        // ═════════════ pptx ═════════════
        private static void RegisterPptx()
        {
            Runner.Add("pptx", "逐页抽取并按页号排序", delegate
            {
                var pptx = BuildPptx(new[]
                {
                    "<p:sp><p:txBody><a:p><a:r><a:t>第一页标题</a:t></a:r></a:p></p:txBody></p:sp>",
                    "<p:sp><p:txBody><a:p><a:r><a:t>第二页内容</a:t></a:r></a:p></p:txBody></p:sp>",
                    "<p:sp><p:txBody><a:p><a:r><a:t>第十页内容</a:t></a:r></a:p></p:txBody></p:sp>"
                });

                var result = OoxmlReader.ReadPptx(pptx);

                Check.True(result.Ok, "解析成功");
                Check.Equal("pptx", result.Kind, "类型");
                Check.EqualInt(3, result.PageCount, "三页");
                Check.Contains(result.Text, "第 1 页", "页码");
                Check.Contains(result.Text, "第一页标题", "第一页内容");

                // slide10 必须排在 slide2 之后（不能按字符串排序）
                int page2 = result.Text.IndexOf("第二页内容", StringComparison.Ordinal);
                int page10 = result.Text.IndexOf("第十页内容", StringComparison.Ordinal);
                Check.True(page2 < page10, "slide2 排在 slide10 前面");
            });

            Runner.Add("pptx", "同一页的多个段落分行", delegate
            {
                var pptx = BuildPptx(new[]
                {
                    "<p:sp><p:txBody><a:p><a:r><a:t>要点一</a:t></a:r></a:p><a:p><a:r><a:t>要点二</a:t></a:r></a:p></p:txBody></p:sp>"
                });

                var result = OoxmlReader.ReadPptx(pptx);
                Check.Contains(result.Text, "要点一\n要点二", "段落分行");
            });

            Runner.Add("pptx", "没有幻灯片时给出可读原因", delegate
            {
                var broken = BuildZip(new Dictionary<string, string> { { "docProps/app.xml", "<root/>" } });
                var result = OoxmlReader.ReadPptx(broken);
                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Warnings[0], "幻灯片", "说明缺什么");
            });
        }

        // ═════════════ 防御 ═════════════
        private static void RegisterDefense()
        {
            Runner.Add("文档防御", "坏 zip 不抛异常", delegate
            {
                var garbage = Encoding.UTF8.GetBytes("这不是一个 zip 文件");
                var result = OoxmlReader.ReadDocx(garbage);
                Check.False(result.Ok, "应当失败");
                Check.True(result.Warnings.Count > 0, "给出原因");
            });

            Runner.Add("文档防御", "XXE：禁止外部实体", delegate
            {
                // 带 DTD 与外部实体的 XML：解析器必须拒绝，不能去读文件
                var evil = "<?xml version=\"1.0\"?><!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>" +
                           "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                           "<w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>";

                var docx = BuildZip(new Dictionary<string, string> { { "word/document.xml", evil } });
                var result = OoxmlReader.ReadDocx(docx);

                // 要么解析失败，要么实体没有被展开（绝不能出现 win.ini 的内容）
                if (result.Ok)
                {
                    Check.False(result.Text.IndexOf("[fonts]", StringComparison.OrdinalIgnoreCase) >= 0,
                        "外部实体不能被展开");
                }
                else
                {
                    Check.True(result.Warnings.Count > 0, "拒绝时给出原因");
                }
            });

            Runner.Add("文档防御", "空文件与不支持的格式", delegate
            {
                Check.False(OoxmlReader.Extract(new byte[0], "a.docx").Ok, "空文件");
                Check.False(OoxmlReader.Extract(new byte[] { 1, 2, 3 }, "a.xyz").Ok, "不支持的扩展名");
                Check.True(OoxmlReader.IsOfficeExtension(".docx"), "识别 docx");
                Check.True(OoxmlReader.IsOfficeExtension(".xlsx"), "识别 xlsx");
                Check.False(OoxmlReader.IsOfficeExtension(".pdf"), "pdf 不算 Office");
            });
        }

        // ═════════════ 合成样本 ═════════════
        private static byte[] BuildZip(Dictionary<string, string> files)
        {
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    foreach (var pair in files)
                    {
                        var entry = zip.CreateEntry(pair.Key, CompressionLevel.Optimal);
                        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        {
                            writer.Write(pair.Value);
                        }
                    }
                }
                return stream.ToArray();
            }
        }

        private static byte[] BuildDocx(string[] bodyXml)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            foreach (var item in bodyXml) sb.Append(item);
            sb.Append("</w:body></w:document>");

            return BuildZip(new Dictionary<string, string>
            {
                { "word/document.xml", sb.ToString() },
                { "[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>" }
            });
        }

        private static byte[] BuildXlsx(string[] headerNames, string[] rows, string[] sharedStrings, string sheetName)
        {
            var shared = new StringBuilder();
            shared.Append("<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            foreach (var value in sharedStrings)
            {
                shared.Append("<si><t>").Append(Escape(value)).Append("</t></si>");
            }
            shared.Append("</sst>");

            var sheet = new StringBuilder();
            sheet.Append("<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            foreach (var row in rows) sheet.Append(row);
            sheet.Append("</sheetData></worksheet>");

            var workbook = new StringBuilder();
            workbook.Append("<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            workbook.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            workbook.Append("<sheet name=\"").Append(Escape(sheetName)).Append("\" sheetId=\"1\" r:id=\"rId1\"/>");
            workbook.Append("</sheets></workbook>");

            var rels = "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                       "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                       "</Relationships>";

            return BuildZip(new Dictionary<string, string>
            {
                { "xl/workbook.xml", workbook.ToString() },
                { "xl/_rels/workbook.xml.rels", rels },
                { "xl/sharedStrings.xml", shared.ToString() },
                { "xl/worksheets/sheet1.xml", sheet.ToString() }
            });
        }

        private static byte[] BuildXlsxMultiSheet(string[] sheetNames, string[] rows)
        {
            var workbook = new StringBuilder();
            workbook.Append("<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            workbook.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");

            var rels = new StringBuilder();
            rels.Append("<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

            var files = new Dictionary<string, string>();

            for (int i = 0; i < sheetNames.Length; i++)
            {
                var relId = "rId" + (i + 1).ToString(CultureInfo.InvariantCulture);
                var path = "worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml";

                workbook.Append("<sheet name=\"").Append(Escape(sheetNames[i])).Append("\" sheetId=\"")
                        .Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\" r:id=\"").Append(relId).Append("\"/>");

                rels.Append("<Relationship Id=\"").Append(relId)
                     .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"")
                     .Append(path).Append("\"/>");

                var sheet = "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                          + (i < rows.Length ? rows[i] : "")
                          + "</sheetData></worksheet>";
                files["xl/" + path] = sheet;
            }

            workbook.Append("</sheets></workbook>");
            rels.Append("</Relationships>");

            files["xl/workbook.xml"] = workbook.ToString();
            files["xl/_rels/workbook.xml.rels"] = rels.ToString();
            return BuildZip(files);
        }

        private static byte[] BuildPptx(string[] slideBodies)
        {
            var files = new Dictionary<string, string>();

            for (int i = 0; i < slideBodies.Length; i++)
            {
                // 注意：第 3 个故意用 slide10 这个编号，用来验证按数字排序
                var number = i == 2 ? 10 : (i + 1);
                var path = "ppt/slides/slide" + number.ToString(CultureInfo.InvariantCulture) + ".xml";

                var xml = "<?xml version=\"1.0\"?><p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" " +
                          "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>" +
                          slideBodies[i] +
                          "</p:spTree></p:cSld></p:sld>";

                files[path] = xml;
            }

            files["ppt/presentation.xml"] = "<?xml version=\"1.0\"?><p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"/>";
            return BuildZip(files);
        }

        private static string Escape(string text)
        {
            return (text ?? "")
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }
}
