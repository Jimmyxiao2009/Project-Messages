// Core/Storage/WorkspaceLayout.cs —— 工作区目录布局，集中一处
//
// 一个档案 = 一个文件夹。路径规则只有这一份，别处一律引用这里，
// 免得像旧项目那样 SafeFileName 逻辑散落好几处、备份时还漏掉某些目录。

using System;

namespace Yanshuai.Messages.Core
{
    public static class WorkspaceLayout
    {
        public const string SharedDir = "_shared";
        public const string TrashDir = "_trash";

    /// <summary>
    /// Chronos 本地包目录（pip 风格的"已安装包"）。每个包一个子目录：
    ///   _packages/&lt;id&gt;/chronos.json    清单（source / view / entry）
    ///   _packages/&lt;id&gt;/module.tccmod  编译产物（link 文件，加载时不再编译）
    ///   _packages/&lt;id&gt;/main.c         源码（留档，便于重新编译）
    /// </summary>
    public const string PackagesDir = "_packages";

        // ── 共享区（跨档案） ─────────────────────────────────────────────
        public static string SharedApiProfiles { get { return SharedDir + "/api-profiles.json"; } }
        public static string SharedGlobalMemory { get { return SharedDir + "/global-memory.json"; } }
        public static string SharedLibrary { get { return SharedDir + "/library.json"; } }
        public static string SharedPermissions { get { return SharedDir + "/permissions.json"; } }
        public static string SharedMcp { get { return SharedDir + "/mcp.json"; } }
        public static string SharedSkills { get { return SharedDir + "/skills.json"; } }
        public static string SharedUserPersonas { get { return SharedDir + "/user-personas.json"; } }

        // ── 档案区 ──────────────────────────────────────────────────────
        public static string ProfileDir(string slug)
        {
            return slug;
        }

        public static string ProfileFile(string slug)
        {
            return slug + "/profile.json";
        }

        public static string MemoryFile(string slug)
        {
            return slug + "/memory.json";
        }

        public static string AvatarFile(string slug)
        {
            return slug + "/avatar.png";
        }

        public static string CardPngFile(string slug)
        {
            return slug + "/card.png";
        }

        public static string CardJsonFile(string slug)
        {
            return slug + "/card.json";
        }

        public static string KnowledgeFile(string slug)
        {
            return slug + "/knowledge.json";
        }

        public static string FilesDir(string slug)
        {
            return slug + "/files";
        }

        public static string AttachmentsDir(string slug)
        {
            return slug + "/files/attachments";
        }

        public static string ConversationDir(string slug)
        {
            return slug + "/conversations";
        }

        public static string ConversationIndexFile(string slug)
        {
            return slug + "/conversations/index.json";
        }

        public static string ConversationFile(string slug, string conversationId)
        {
            return slug + "/conversations/" + conversationId + ".json";
        }

        public static string ExportsDir(string slug)
        {
            return slug + "/exports";
        }

        // ── 回收区 ──────────────────────────────────────────────────────
        /// <summary>
        /// 删除档案时不直接抹掉，先移到这里。误触的代价从"数据没了"降为"翻一下回收区"。
        /// </summary>
        public static string TrashDirFor(string slug, DateTimeOffset nowUtc)
        {
            return TrashDir + "/" + nowUtc.ToString("yyyyMMdd-HHmmss") + "-" + slug;
        }

        /// <summary>把路径拆成 目录 + 文件名，供只提供 ListFiles/ListDirectories 的存储实现使用。</summary>
        public static void Split(string relativePath, out string dir, out string name)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                dir = "";
                name = "";
                return;
            }
            int idx = relativePath.LastIndexOf('/');
            if (idx < 0)
            {
                dir = "";
                name = relativePath;
            }
            else
            {
                dir = relativePath.Substring(0, idx);
                name = relativePath.Substring(idx + 1);
            }
        }

        /// <summary>取文件名（含扩展名）。</summary>
        public static string FileName(string relativePath)
        {
            string dir, name;
            Split(relativePath, out dir, out name);
            return name;
        }

        /// <summary>取不含扩展名的文件名。</summary>
        public static string FileNameWithoutExtension(string relativePath)
        {
            var name = FileName(relativePath);
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        /// <summary>取扩展名（含点，小写）。没有则返回空串。</summary>
        public static string Extension(string relativePath)
        {
            var name = FileName(relativePath);
            int dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot).ToLowerInvariant() : string.Empty;
        }
    }
}
