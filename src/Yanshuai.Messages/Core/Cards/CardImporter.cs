// Core/Cards/CardImporter.cs —— SillyTavern 角色卡导入
//
// 支持两种入口：
//   • PNG 卡：解析 tEXt / zTXt / iTXt 块里的 chara（v2）与 ccv3（v3）字段，
//     值是 base64 编码的 JSON。zTXt 是 zlib 压缩，得先解压。
//   • JSON 卡：直接给 v1 / v2 / v3 结构的 JSON 文本。
//
// 三条纪律（都是旧项目踩过的）：
//   1) 解析前的体积上限：PNG 超过 5MB 直接拒，避免超大图把内存吃穿；
//   2) 畸形块不能死循环：chunk 长度越界立刻停止；
//   3) 解析出的字段为 0 条要明确报错，不能静默成功。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public sealed class CardImportResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public List<string> Warnings { get; private set; }

        public Profile Profile { get; set; }
        public Lorebook Lorebook { get; set; }
        /// <summary>卡图原图字节，用来存成 card.png 与生成头像。</summary>
        public byte[] ImageBytes { get; set; }
        public int CardSpecVersion { get; set; }
        /// <summary>解析到多少个非空人设字段，0 表示这张卡没东西。</summary>
        public int FilledFieldCount { get; set; }

        public CardImportResult()
        {
            Warnings = new List<string>();
        }

        public static CardImportResult Fail(string message)
        {
            return new CardImportResult { Ok = false, Error = message };
        }
    }

    public static class CardImporter
    {
        public const long MaxPngBytes = Limits.MaxCardPngBytes;

        private static readonly byte[] PngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // ═════════════════════════════════════════════════════════════════
        // 入口
        // ═════════════════════════════════════════════════════════════════

        /// <summary>按扩展名自动选 PNG 或 JSON 路径。</summary>
        public static CardImportResult Import(byte[] bytes, string fileName)
        {
            if (bytes == null || bytes.Length == 0) return CardImportResult.Fail("文件是空的。");

            var extension = WorkspaceLayout.Extension(fileName ?? "");
            bool looksPng = HasPngSignature(bytes);

            if (looksPng) return ImportPng(bytes);
            if (extension == ".json" || LooksLikeJson(bytes))
            {
                return ImportJson(DecodeUtf8(bytes), fileName);
            }

            return CardImportResult.Fail("无法识别的文件格式。请选择 SillyTavern 的 PNG 角色卡或 JSON 卡。");
        }

        public static bool HasPngSignature(byte[] bytes)
        {
            if (bytes == null || bytes.Length < PngSignature.Length) return false;
            for (int i = 0; i < PngSignature.Length; i++)
            {
                if (bytes[i] != PngSignature[i]) return false;
            }
            return true;
        }

        private static bool LooksLikeJson(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length && i < 64; i++)
            {
                var ch = (char)bytes[i];
                if (char.IsWhiteSpace(ch)) continue;
                return ch == '{' || ch == '[';
            }
            return false;
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            // 去掉 UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        // ═════════════════════════════════════════════════════════════════
        // PNG
        // ═════════════════════════════════════════════════════════════════

        public static CardImportResult ImportPng(byte[] bytes)
        {
            if (bytes.Length > MaxPngBytes)
            {
                return CardImportResult.Fail("图片超过 " +
                    (MaxPngBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB，已拒绝以防内存不足。");
            }

            string charaBase64 = null;
            string ccv3Base64 = null;
            var sawUnknownCompression = false;

            // 用 long 计算偏移：块长度是外部输入，累加时很容易溢出成负数，
            // 一旦游标变负，后面的下标访问就会 IndexOutOfRange。这里全程按 long 比较。
            long offset = PngSignature.Length;
            while (offset + 8 <= bytes.Length)
            {
                int length = ReadInt32BigEndian(bytes, (int)offset);
                if (length < 0) break;

                long dataStart = offset + 8;
                long chunkEnd = dataStart + length;
                if (chunkEnd + 4 > bytes.Length) break;      // 含 4 字节 CRC

                var type = Encoding.ASCII.GetString(bytes, (int)offset + 4, 4);

                if (type == "tEXt" || type == "zTXt" || type == "iTXt")
                {
                    string keyword, text;
                    if (TryReadTextChunk(bytes, (int)dataStart, length, type, out keyword, out text, out sawUnknownCompression))
                    {
                        if (keyword == "chara" && !string.IsNullOrEmpty(text)) charaBase64 = text;
                        else if (keyword == "ccv3" && !string.IsNullOrEmpty(text)) ccv3Base64 = text;
                    }
                }

                if (type == "IEND") break;

                offset = chunkEnd + 4;                        // 跳过 CRC
            }

            var payload = ccv3Base64 ?? charaBase64;
            if (string.IsNullOrEmpty(payload))
            {
                var message = sawUnknownCompression
                    ? "卡里的压缩块用了不认识的压缩方式，无法解析。"
                    : "这张 PNG 里没有角色卡数据（找不到 chara 块）。如果只是想用这张图当头像，请改用「选择头像」。";
                return CardImportResult.Fail(message);
            }

            string json;
            try
            {
                json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch (Exception)
            {
                return CardImportResult.Fail("角色卡数据不是合法的 base64。");
            }

            var result = ImportJson(json, "card.png");
            if (result.Ok)
            {
                result.ImageBytes = bytes;
                if (!string.IsNullOrEmpty(ccv3Base64) && string.IsNullOrEmpty(charaBase64))
                {
                    result.CardSpecVersion = 3;
                }
            }
            return result;
        }

        /// <summary>
        /// 读一个文本块。tEXt 是明文；zTXt 的正文是 zlib 压缩；
        /// iTXt 多两个标志字节，之后才可能是压缩正文。
        /// </summary>
        private static bool TryReadTextChunk(
            byte[] bytes, int start, int length, string type,
            out string keyword, out string text, out bool unknownCompression)
        {
            keyword = "";
            text = "";
            unknownCompression = false;

            int end = start + length;
            int zero = start;
            while (zero < end && bytes[zero] != 0) zero++;
            if (zero >= end) return false;

            keyword = Encoding.ASCII.GetString(bytes, start, zero - start).ToLowerInvariant();
            int cursor = zero + 1;

            if (type == "tEXt")
            {
                text = Encoding.UTF8.GetString(bytes, cursor, end - cursor);
                return true;
            }

            if (type == "zTXt")
            {
                if (cursor >= end) return false;
                byte method = bytes[cursor];
                cursor++;
                if (method != 0) { unknownCompression = true; return false; }

                var compressed = new byte[end - cursor];
                Buffer.BlockCopy(bytes, cursor, compressed, 0, compressed.Length);
                text = Inflate(compressed);
                return !string.IsNullOrEmpty(text);
            }

            if (type == "iTXt")
            {
                // keyword\0 compFlag(1) compMethod(1) langTag\0 translatedKeyword\0 text
                if (cursor + 2 > end) return false;
                byte compFlag = bytes[cursor];
                byte compMethod = bytes[cursor + 1];
                cursor += 2;

                int langEnd = cursor;
                while (langEnd < end && bytes[langEnd] != 0) langEnd++;
                cursor = langEnd + 1;

                int transEnd = cursor;
                while (transEnd < end && bytes[transEnd] != 0) transEnd++;
                cursor = transEnd + 1;
                if (cursor >= end) return false;

                var body = new byte[end - cursor];
                Buffer.BlockCopy(bytes, cursor, body, 0, body.Length);

                if (compFlag == 1)
                {
                    if (compMethod != 0) { unknownCompression = true; return false; }
                    text = Inflate(body);
                }
                else
                {
                    text = Encoding.UTF8.GetString(body, 0, body.Length);
                }
                return !string.IsNullOrEmpty(text);
            }

            return false;
        }

        /// <summary>解开 zlib 包装的 deflate 数据（跳过 2 字节头与 4 字节校验尾）。</summary>
        private static string Inflate(byte[] zlibData)
        {
            if (zlibData == null || zlibData.Length < 6) return "";

            try
            {
                using (var input = new MemoryStream(zlibData, 2, zlibData.Length - 6))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        if (output.Length > 8 * 1024 * 1024) break;   // 防御：解压炸弹
                    }
                    return Encoding.UTF8.GetString(output.ToArray(), 0, (int)output.Length);
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static int ReadInt32BigEndian(byte[] bytes, int offset)
        {
            if (offset + 4 > bytes.Length) return -1;
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        // ═════════════════════════════════════════════════════════════════
        // JSON（v1 / v2 / v3）
        // ═════════════════════════════════════════════════════════════════

        public static CardImportResult ImportJson(string json, string fileName)
        {
            if (string.IsNullOrWhiteSpace(json)) return CardImportResult.Fail("内容为空。");

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                return CardImportResult.Fail("不是合法的 JSON。");
            }

            var result = new CardImportResult { Ok = true };

            var spec = root["spec"] != null ? root["spec"].ToString() : "";
            int specVersion = 2;
            if (TextUtil.Contains(spec, "v3", StringComparison.OrdinalIgnoreCase)) specVersion = 3;
            else if (TextUtil.Contains(spec, "v2", StringComparison.OrdinalIgnoreCase)) specVersion = 2;
            else if (root["data"] == null) specVersion = 1;      // 老卡：字段在顶层

            result.CardSpecVersion = specVersion;

            // v2/v3 的有效载荷在 data 里；v1 在顶层
            var data = root["data"] as JObject ?? root;

            var persona = new Persona
            {
                CardSpecVersion = specVersion,
                Name = Str(data, "name"),
                Description = Str(data, "description"),
                Personality = Str(data, "personality"),
                Scenario = Str(data, "scenario"),
                FirstMessage = Str(data, "first_mes"),
                ExampleDialogue = Str(data, "mes_example"),
                SystemPrompt = Str(data, "system_prompt"),
                PostHistoryInstructions = Str(data, "post_history_instructions"),
                CreatorNotes = Str(data, "creator_notes"),
                Creator = Str(data, "creator"),
                CharacterVersion = Str(data, "character_version")
            };

            // 标签
            var tags = data["tags"] as JArray;
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    var value = tag != null ? tag.ToString().Trim() : "";
                    if (value.Length > 0) persona.Tags.Add(value);
                }
            }

            // 备用开场白：不丢，收进 extensions 以便导出时还原
            var alternatives = data["alternate_greetings"] as JArray;
            if (alternatives != null && alternatives.Count > 0)
            {
                var list = new List<object>();
                foreach (var item in alternatives) list.Add(item != null ? item.ToString() : "");
                persona.Extensions["alternateGreetings"] = list;
            }

            // 未知的 extensions 原样保留
            var extensions = data["extensions"] as JObject;
            if (extensions != null)
            {
                foreach (var property in extensions.Properties())
                {
                    if (persona.Extensions.ContainsKey(property.Name)) continue;
                    persona.Extensions[property.Name] = property.Value != null ? property.Value.ToString() : "";
                }
            }

            if (string.IsNullOrWhiteSpace(persona.Name))
            {
                persona.Name = string.IsNullOrEmpty(fileName)
                    ? "导入的档案"
                    : WorkspaceLayout.FileNameWithoutExtension(fileName);
                result.Warnings.Add("卡片没有名字，已用文件名代替。");
            }

            // 数一数到底解析出了多少内容
            int filled = 0;
            if (!string.IsNullOrEmpty(persona.Description)) filled++;
            if (!string.IsNullOrEmpty(persona.Personality)) filled++;
            if (!string.IsNullOrEmpty(persona.Scenario)) filled++;
            if (!string.IsNullOrEmpty(persona.FirstMessage)) filled++;
            if (!string.IsNullOrEmpty(persona.ExampleDialogue)) filled++;
            if (!string.IsNullOrEmpty(persona.SystemPrompt)) filled++;
            if (!string.IsNullOrEmpty(persona.PostHistoryInstructions)) filled++;
            result.FilledFieldCount = filled;

            if (filled == 0)
            {
                return CardImportResult.Fail("这张卡里没有解析到任何可用内容（描述、性格、场景、开场白都是空的）。");
            }

            if (string.IsNullOrEmpty(persona.ExampleDialogue))
            {
                result.Warnings.Add("没有示例对话，不影响使用。");
            }

            result.Lorebook = ParseCharacterBook(data["character_book"] as JObject, persona.Name);

            var profile = new Profile
            {
                Name = persona.Name,
                Persona = persona
            };
            profile.Repair();

            result.Profile = profile;
            return result;
        }

        /// <summary>卡内嵌世界书 → 我们的 Lorebook。</summary>
        public static Lorebook ParseCharacterBook(JObject book, string ownerName)
        {
            if (book == null) return null;

            var lorebook = new Lorebook
            {
                Name = string.IsNullOrWhiteSpace(ownerName) ? "导入的世界书" : ownerName + " 的世界书"
            };

            var entries = book["entries"] as JArray;
            if (entries == null) return lorebook;

            foreach (var token in entries)
            {
                var item = token as JObject;
                if (item == null) continue;

                var entry = new LorebookEntry
                {
                    Name = Str(item, "comment"),
                    Content = Str(item, "content"),
                    Enabled = !Bool(item, "disable", false),
                    AlwaysActive = Bool(item, "constant", false),
                    CaseSensitive = Bool(item, "caseSensitive", false),
                    UseRegex = Bool(item, "useRegex", false),
                    Priority = Int(item, "order", 100)
                };

                if (string.IsNullOrEmpty(entry.Name)) entry.Name = TextUtil.Truncate(entry.Content, 24);

                if (!entry.Enabled) continue;      // 明确禁用的条目直接不导入

                // 关键词：主键 + 次键
                var keys = item["keys"] as JArray;
                if (keys != null)
                {
                    foreach (var key in keys)
                    {
                        var value = key != null ? key.ToString().Trim() : "";
                        if (value.Length > 0) entry.Keywords.Add(value);
                    }
                }
                var secondary = item["secondary_keys"] as JArray;
                if (secondary != null)
                {
                    foreach (var key in secondary)
                    {
                        var value = key != null ? key.ToString().Trim() : "";
                        if (value.Length > 0 && !entry.Keywords.Contains(value)) entry.Keywords.Add(value);
                    }
                }

                // 常驻条目：SillyTavern 里 constant=1 表示无条件注入
                if (entry.AlwaysActive && entry.Keywords.Count == 0) { /* 就这样，注入时无条件 */ }

                entry.Position = MapPosition(Int(item, "position", 0), Bool(item, "extensions.position_after_char", false));

                if (string.IsNullOrEmpty(entry.Content)) continue;

                lorebook.Entries.Add(entry);
            }

            return lorebook;
        }

        /// <summary>SillyTavern 的 position：0=角色定义之前，1=之后，2/3=AN 顶部/底部，4=指定深度。</summary>
        private static InjectionPosition MapPosition(int position, bool afterChar)
        {
            switch (position)
            {
                case 0: return InjectionPosition.BeforeSystem;
                case 1: return InjectionPosition.AfterSystem;
                case 2: return InjectionPosition.TopOfChat;
                case 3: return InjectionPosition.BottomOfChat;
                case 4: return InjectionPosition.AtDepth;
                default: return afterChar ? InjectionPosition.AfterSystem : InjectionPosition.BeforeSystem;
            }
        }

        // ── JSON 取值小工具（容忍类型不一致） ───────────────────────────

        private static string Str(JObject obj, string key)
        {
            if (obj == null) return "";
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return "";
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }

        private static bool Bool(JObject obj, string key, bool fallback)
        {
            if (obj == null) return fallback;
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.Integer) return token.Value<int>() != 0;
            var text = token.ToString().Trim().ToLowerInvariant();
            if (text == "true" || text == "1") return true;
            if (text == "false" || text == "0") return false;
            return fallback;
        }

        private static int Int(JObject obj, string key, int fallback)
        {
            if (obj == null) return fallback;
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return fallback;

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                double value;
                if (double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return (int)Math.Round(value);
                }
            }
            var text = token.ToString();
            int parsed;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }

        // ═════════════════════════════════════════════════════════════════
        // 导出
        // ═════════════════════════════════════════════════════════════════

        /// <summary>把档案导出成 v2 规范的 JSON（能被 SillyTavern 与别的客户端读回）。</summary>
        public static string ExportJson(Profile profile)
        {
            var persona = profile.Persona ?? new Persona();

            var data = new JObject
            {
                ["name"] = persona.Name,
                ["description"] = persona.Description,
                ["personality"] = persona.Personality,
                ["scenario"] = persona.Scenario,
                ["first_mes"] = persona.FirstMessage,
                ["mes_example"] = persona.ExampleDialogue,
                ["creator_notes"] = persona.CreatorNotes,
                ["system_prompt"] = persona.SystemPrompt,
                ["post_history_instructions"] = persona.PostHistoryInstructions,
                ["creator"] = persona.Creator,
                ["character_version"] = persona.CharacterVersion
            };

            var tags = new JArray();
            foreach (var tag in persona.Tags) tags.Add(tag);
            data["tags"] = tags;

            var extensions = new JObject();
            foreach (var pair in persona.Extensions)
            {
                if (pair.Key == "alternateGreetings")
                {
                    var list = new JArray();
                    var items = pair.Value as IEnumerable<object>;
                    if (items != null)
                    {
                        foreach (var item in items) list.Add(item != null ? item.ToString() : "");
                    }
                    data["alternate_greetings"] = list;
                    continue;
                }
                extensions[pair.Key] = pair.Value != null ? pair.Value.ToString() : "";
            }
            data["extensions"] = extensions;

            var root = new JObject
            {
                ["spec"] = persona.CardSpecVersion >= 3 ? "chara_card_v3" : "chara_card_v2",
                ["spec_version"] = persona.CardSpecVersion >= 3 ? "3.0" : "2.0",
                ["data"] = data
            };

            return root.ToString();
        }
    }
}
