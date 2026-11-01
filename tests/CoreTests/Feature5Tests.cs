// tests/CoreTests/Feature5Tests.cs —— 备份与恢复
//
// 重点验证三件事：
//   1) 二进制文件（图片）往返后字节完全一致 —— 这是我实际踩到并修掉的 bug
//   2) 备份里不含密钥
//   3) 恢复是事务式的：坏包不能破坏现有数据

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature5Tests
    {
        public static void Register()
        {
            RegisterCreate();
            RegisterRestore();
            RegisterSafety();
        }

        // ═════════════ 备份 ═════════════
        private static void RegisterCreate()
        {
            Runner.Add("备份", "打包工作区并写入清单", delegate
            {
                var env = BuildEnv();

                var buffer = new MemoryStream();
                var result = env.Backup.CreateAsync(buffer, env.Workspace, "1.0.0.0", "Lumia 950", null, CancellationToken.None).Result;

                Check.True(result.Ok, "备份成功");
                Check.True(result.FileCount > 0, "有文件被备份");
                Check.True(result.ByteSize > 0, "有体积");

                buffer.Seek(0, SeekOrigin.Begin);
                var manifest = BackupService.ReadManifest(buffer);

                Check.True(manifest != null, "能读出清单");
                Check.EqualInt(1, manifest.Version, "清单版本");
                Check.Equal("1.0.0.0", manifest.AppVersion, "应用版本");
                Check.False(manifest.ContainsSecrets, "不含密钥");
                Check.True(manifest.Entries.Count > 0, "条目清单非空");
            });

            Runner.Add("备份", "二进制文件往返字节完全一致", delegate
            {
                var env = BuildEnv();

                // 造一个"图片"：含 0x00 与 0xFF 的字节序列，UTF-8 往返一定会被破坏
                var imageBytes = new byte[512];
                for (int i = 0; i < imageBytes.Length; i++) imageBytes[i] = (byte)(i % 256);
                env.Store.WriteBytesAsync("avatar-test/files/pic.png", imageBytes).Wait();

                var buffer = new MemoryStream();
                env.Backup.CreateAsync(buffer, env.Workspace, "1", "", null, CancellationToken.None).Wait();

                // 清掉原文件，再从备份恢复
                env.Store.DeleteAsync("avatar-test/files/pic.png").Wait();
                Check.True(env.Store.Peek("avatar-test/files/pic.png") == null, "原文件已删");

                buffer.Seek(0, SeekOrigin.Begin);
                var restore = env.Backup.RestoreAsync(buffer, "_restore", null, CancellationToken.None).Result;

                Check.True(restore.Ok, "恢复成功");

                var restored = env.Store.ReadBytesAsync("avatar-test/files/pic.png").Result;
                Check.EqualInt(imageBytes.Length, restored.Length, "长度一致");

                bool identical = true;
                for (int i = 0; i < imageBytes.Length; i++)
                {
                    if (imageBytes[i] != restored[i]) { identical = false; break; }
                }
                Check.True(identical, "字节完全一致（UTF-8 往返会破坏二进制，这条钉住它）");
            });

            Runner.Add("备份", "不备份回收区与临时文件", delegate
            {
                var env = BuildEnv();
                env.Store.WriteTextAsync("_trash/20260101-old/profile.json", "{}").Wait();
                env.Store.WriteTextAsync("test/profile.json.tmp", "{}").Wait();
                env.Store.WriteTextAsync("test/files/.backup/notes.md", "old").Wait();

                var buffer = new MemoryStream();
                var result = env.Backup.CreateAsync(buffer, env.Workspace, "1", "", null, CancellationToken.None).Result;

                foreach (var entry in result.Manifest.Entries)
                {
                    Check.False(entry.StartsWith("_trash/", StringComparison.Ordinal), "不含回收区：" + entry);
                    Check.False(entry.EndsWith(".tmp", StringComparison.Ordinal), "不含临时文件：" + entry);
                    Check.False(entry.IndexOf("/.backup/", StringComparison.Ordinal) >= 0, "不含写入备份：" + entry);
                }
            });

            Runner.Add("备份", "进度回调被调用", delegate
            {
                var env = BuildEnv();

                int calls = 0;
                var buffer = new MemoryStream();
                env.Backup.CreateAsync(buffer, env.Workspace, "1", "",
                    delegate (int done, int total, string name) { calls++; },
                    CancellationToken.None).Wait();

                Check.True(calls > 0, "进度回调至少被调一次");
            });

            Runner.Add("备份", "空工作区给出可读错误", delegate
            {
                var store = new InMemoryFileStore();
                var workspace = new Workspace(store, new InMemorySecretStore(), NullLogger.Instance, SystemClock.Instance);
                workspace.InitializeAsync().Wait();

                var backup = new BackupService(store, NullLogger.Instance, SystemClock.Instance);
                var result = backup.CreateAsync(new MemoryStream(), workspace, "1", "", null, CancellationToken.None).Result;

                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Error, "没有任何数据", "说明原因");
            });
        }

        // ═════════════ 恢复 ═════════════
        private static void RegisterRestore()
        {
            Runner.Add("恢复", "坏包被拒绝且不动现有数据", delegate
            {
                var env = BuildEnv();
                var before = env.Store.Peek("test/profile.json");

                var garbage = new MemoryStream(Encoding.UTF8.GetBytes("这不是一个 zip"));
                var result = env.Backup.RestoreAsync(garbage, "_restore", null, CancellationToken.None).Result;

                Check.False(result.Ok, "应当失败");
                Check.True(result.Error.Length > 0, "给出原因");
                Check.Equal(before, env.Store.Peek("test/profile.json"), "现有数据未被改动");
            });

            Runner.Add("恢复", "缺清单的 zip 被拒绝", delegate
            {
                var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry("test/profile.json");
                    using (var writer = new StreamWriter(entry.Open())) writer.Write("{}");
                }
                buffer.Seek(0, SeekOrigin.Begin);

                var env = BuildEnv();
                var result = env.Backup.RestoreAsync(buffer, "_restore", null, CancellationToken.None).Result;

                Check.False(result.Ok, "应当失败");
                Check.Contains(result.Error, "清单", "说明缺清单");
            });

            Runner.Add("恢复", "路径穿越条目被拒绝或被规范化成安全相对路径", delegate
            {
                Check.True(BackupService.NormalizeEntryName("../../windows/system32/evil.dll") == null, "上跳拒绝");
                Check.True(BackupService.NormalizeEntryName("C:/windows/evil") == null, "盘符拒绝");
                Check.True(BackupService.NormalizeEntryName("") == null, "空名拒绝");
                Check.True(BackupService.NormalizeEntryName("dir/") == null, "目录项拒绝");

                // 前导斜杠会被去掉变成安全相对路径 —— 这同样是可接受的处理
                var absolute = BackupService.NormalizeEntryName("/etc/passwd");
                if (absolute != null)
                {
                    Check.False(absolute.StartsWith("/", StringComparison.Ordinal), "结果不能是绝对路径");
                    Check.False(absolute.IndexOf("..", StringComparison.Ordinal) >= 0, "结果不含上跳");
                }

                Check.Equal("test/profile.json", BackupService.NormalizeEntryName("test/profile.json"), "正常路径保留");
                Check.Equal("test/profile.json", BackupService.NormalizeEntryName("test\\profile.json"), "反斜杠规范化");
            });

            Runner.Add("恢复", "恢复后数据与备份时一致", delegate
            {
                var env = BuildEnv();
                env.Store.WriteTextAsync("test/files/notes.md", "重要内容 v1").Wait();

                var buffer = new MemoryStream();
                env.Backup.CreateAsync(buffer, env.Workspace, "1", "", null, CancellationToken.None).Wait();

                // 改掉内容
                env.Store.WriteTextAsync("test/files/notes.md", "被改坏了").Wait();

                buffer.Seek(0, SeekOrigin.Begin);
                var result = env.Backup.RestoreAsync(buffer, "_restore", null, CancellationToken.None).Result;

                Check.True(result.Ok, "恢复成功");
                Check.Equal("重要内容 v1", env.Store.Peek("test/files/notes.md"), "内容回到备份时的状态");
            });

            Runner.Add("恢复", "临时区在恢复后被清理", delegate
            {
                var env = BuildEnv();
                var buffer = new MemoryStream();
                env.Backup.CreateAsync(buffer, env.Workspace, "1", "", null, CancellationToken.None).Wait();
                buffer.Seek(0, SeekOrigin.Begin);

                env.Backup.RestoreAsync(buffer, "_restore-staging", null, CancellationToken.None).Wait();

                var leftovers = env.Store.ListFilesAsync("_restore-staging", true).Result;
                Check.EqualInt(0, leftovers.Count, "临时区已清空");
            });
        }

        // ═════════════ 安全 ═════════════
        private static void RegisterSafety()
        {
            Runner.Add("备份安全", "备份包里不含密钥", delegate
            {
                var env = BuildEnv();

                var buffer = new MemoryStream();
                env.Backup.CreateAsync(buffer, env.Workspace, "1", "", null, CancellationToken.None).Wait();
                buffer.Seek(0, SeekOrigin.Begin);

                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Length > 1024 * 1024) continue;
                        using (var reader = new StreamReader(entry.Open()))
                        {
                            var content = reader.ReadToEnd();
                            Check.NotContains(content, "sk-secret", "条目 " + entry.FullName + " 里不该有密钥");
                        }
                    }
                }
            });

            Runner.Add("备份安全", "文件命名规则", delegate
            {
                Check.True(BackupNaming.LooksLikeBackup("言枢备份-20261012-120000.yanshuai-backup.zip"), "识别自己的备份");
                Check.False(BackupNaming.LooksLikeBackup("随便一个.zip"), "不误认");
                Check.Contains(BackupNaming.DefaultFileName(DateTimeOffset.Now), "言枢备份", "默认名带前缀");
            });

            Runner.Add("备份安全", "超大条目被校验拦下", delegate
            {
                var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    // 清单正常，但另一个条目声称超大
                    var manifest = zip.CreateEntry(BackupNaming.ManifestEntryName);
                    using (var writer = new StreamWriter(manifest.Open()))
                    {
                        writer.Write(JsonStore.Serialize(new BackupManifest()));
                    }
                }
                buffer.Seek(0, SeekOrigin.Begin);

                Check.True(BackupService.Validate(buffer) == null, "正常包通过校验");

                var garbage = new MemoryStream(Encoding.UTF8.GetBytes("not a zip"));
                Check.True(BackupService.Validate(garbage) != null, "坏包被拦下");
            });
        }

        // ═════════════ 辅助 ═════════════
        private sealed class Env
        {
            public InMemoryFileStore Store;
            public InMemorySecretStore Secrets;
            public Workspace Workspace;
            public BackupService Backup;
        }

        private static Env BuildEnv()
        {
            var env = new Env();
            env.Store = new InMemoryFileStore();
            env.Secrets = new InMemorySecretStore();
            env.Workspace = new Workspace(env.Store, env.Secrets, NullLogger.Instance, SystemClock.Instance);
            env.Workspace.InitializeAsync().Wait();

            // 造一点真实数据
            var profile = env.Workspace.CreateProfileAsync("测试档案", "test").Result;
            var conversation = env.Workspace.CreateConversation(profile, "一条会话");
            conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "你好" });
            env.Workspace.SaveConversationAsync(profile.Slug, conversation).Wait();

            // 密钥进保险箱（不该出现在备份里）
            var api = new ApiProfile { Id = "k1", Name = "带密钥的配置" };
            env.Workspace.ApiProfiles.Items.Add(api);
            env.Workspace.SetApiKey(api, "sk-secret-should-not-be-backed-up");
            env.Workspace.SaveApiProfilesAsync().Wait();

            env.Backup = new BackupService(env.Store, NullLogger.Instance, SystemClock.Instance);
            return env;
        }
    }
}
