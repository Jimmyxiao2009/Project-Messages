// tests/CoreTests/Feature8Tests.cs —— 快捷消息与存储统计

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature8Tests
    {
        public static void Register()
        {
            RegisterQuickMessages();
            RegisterStorageStats();
        }

        // ═════════════ 快捷消息 ═════════════
        private static void RegisterQuickMessages()
        {
            Runner.Add("快捷消息", "默认列表非空且排序连续", delegate
            {
                var list = QuickMessages.CreateDefaults();

                Check.True(list.Count >= 4, "有若干条默认项");
                for (int i = 0; i < list.Count; i++)
                {
                    Check.EqualInt(i, list[i].Sort, "排序连续");
                    Check.True(list[i].Title.Length > 0, "有标题");
                    Check.True(list[i].Content.Length > 0, "有内容");
                }
            });

            Runner.Add("快捷消息", "占位符被替换", delegate
            {
                var context = new QuickMessageContext
                {
                    CharacterName = "小明",
                    UserName = "阿强",
                    Now = new DateTimeOffset(2026, 10, 12, 15, 30, 0, TimeSpan.FromHours(8))
                };

                var text = QuickMessages.Expand("你好 {char}，我是 {user}，现在是 {cur_datetime}。", context);

                Check.Contains(text, "小明", "角色名");
                Check.Contains(text, "阿强", "用户名");
                Check.Contains(text, "2026-10-12 15:30", "时间");
                Check.False(text.Contains("{"), "没有残留占位符");
            });

            Runner.Add("快捷消息", "未知占位符原样保留", delegate
            {
                var context = new QuickMessageContext { CharacterName = "小明" };
                var text = QuickMessages.Expand("你好 {char}，{不认识的}。", context);

                Check.Contains(text, "小明", "认识的被替换");
                Check.Contains(text, "{不认识的}", "不认识的留着让用户看得见");
            });

            Runner.Add("快捷消息", "不可见的项被过滤且按序返回", delegate
            {
                var list = new List<QuickMessage>
                {
                    new QuickMessage { Title = "第三", Sort = 3 },
                    new QuickMessage { Title = "第一", Sort = 1 },
                    new QuickMessage { Title = "隐藏的", Sort = 0, Visible = false },
                    new QuickMessage { Title = "第二", Sort = 2 }
                };

                var visible = QuickMessages.Visible(list);

                Check.EqualInt(3, visible.Count, "隐藏项被过滤");
                Check.Equal("第一", visible[0].Title, "按 Sort 排序");
                Check.Equal("第三", visible[2].Title, "按 Sort 排序");
            });

            Runner.Add("快捷消息", "空标题空内容的项不显示", delegate
            {
                var list = new List<QuickMessage>
                {
                    new QuickMessage { Title = "", Content = "", Sort = 0 },
                    new QuickMessage { Title = "有内容", Content = "内容", Sort = 1 }
                };

                Check.EqualInt(1, QuickMessages.Visible(list).Count, "空项被过滤");
            });

            Runner.Add("快捷消息", "下一个排序值递增", delegate
            {
                var list = new List<QuickMessage>
                {
                    new QuickMessage { Sort = 5 },
                    new QuickMessage { Sort = 9 }
                };

                Check.EqualInt(10, QuickMessages.NextSort(list), "取最大值加一");
                Check.EqualInt(0, QuickMessages.NextSort(null), "空列表从 0 开始");
            });
        }

        // ═════════════ 存储统计 ═════════════
        private static void RegisterStorageStats()
        {
            Runner.Add("存储统计", "分类归得对", delegate
            {
                var store = new InMemoryFileStore();
                store.WriteTextAsync("test/conversations/abc.json", new string('x', 1000)).Wait();
                store.WriteTextAsync("test/conversations/tech/abc.jsonl", new string('y', 500)).Wait();
                store.WriteBytesAsync("test/files/attachments/pic.png", new byte[2000]).Wait();
                store.WriteTextAsync("test/memory.json", new string('z', 300)).Wait();
                store.WriteTextAsync("_backups/20260101.zip", new string('b', 700)).Wait();
                store.WriteTextAsync("_trash/20260101-test/old.json", new string('t', 100)).Wait();

                var service = new StorageStatsService(store, NullLogger.Instance);
                var stats = service.MeasureAsync("test", CancellationToken.None).Result;

                Check.EqualInt(1000, (int)stats.Find("conversations").Bytes, "会话");
                Check.EqualInt(500, (int)stats.Find("logs").Bytes, "用量日志（不被会话规则吃掉）");
                Check.EqualInt(2000, (int)stats.Find("attachments").Bytes, "附件");
                Check.EqualInt(300, (int)stats.Find("memory").Bytes, "记忆");
                Check.EqualInt(700, (int)stats.Find("backups").Bytes, "备份");
                Check.EqualInt(100, (int)stats.Find("trash").Bytes, "回收区");
            });

            Runner.Add("存储统计", "中文按 UTF-8 字节数算而不是字符数", delegate
            {
                var store = new InMemoryFileStore();
                var text = new string('中', 100);   // 100 字符，UTF-8 下 300 字节
                store.WriteTextAsync("test/memory.json", text).Wait();

                var service = new StorageStatsService(store, NullLogger.Instance);
                var stats = service.MeasureAsync("test", CancellationToken.None).Result;

                Check.EqualInt(300, (int)stats.TotalBytes, "按字节算（按字符算会低估三倍）");
            });

            Runner.Add("存储统计", "总量与文件数一致", delegate
            {
                var store = new InMemoryFileStore();
                store.WriteTextAsync("test/a.json", "12345").Wait();
                store.WriteTextAsync("test/b.json", "1234567890").Wait();

                var service = new StorageStatsService(store, NullLogger.Instance);
                var stats = service.MeasureAsync("test", CancellationToken.None).Result;

                Check.EqualInt(2, stats.TotalFiles, "文件数");
                Check.EqualInt(15, (int)stats.TotalBytes, "总字节");
            });

            Runner.Add("存储统计", "按体积从大到小排", delegate
            {
                var store = new InMemoryFileStore();
                store.WriteTextAsync("test/conversations/a.json", new string('a', 100)).Wait();
                store.WriteBytesAsync("test/files/attachments/b.png", new byte[5000]).Wait();
                store.WriteTextAsync("_trash/x/old.json", new string('c', 1000)).Wait();

                var service = new StorageStatsService(store, NullLogger.Instance);
                var stats = service.MeasureAsync("test", CancellationToken.None).Result;

                Check.Equal("attachments", stats.Buckets[0].Key, "附件最大排第一");
                Check.True(stats.Buckets[0].Bytes >= stats.Buckets[1].Bytes, "递减");
            });

            Runner.Add("存储统计", "空工作区不报错", delegate
            {
                var store = new InMemoryFileStore();
                var service = new StorageStatsService(store, NullLogger.Instance);
                var stats = service.MeasureAsync("test", CancellationToken.None).Result;

                Check.EqualInt(0, stats.TotalFiles, "没有文件");
                Check.EqualInt(0, stats.Buckets.Count, "没有分类");
                Check.Contains(StorageStatsService.Format(stats), "没有数据", "渲染不报错");
            });

            Runner.Add("存储统计", "体积格式化可读", delegate
            {
                Check.Equal("512 B", StorageStats.FormatBytes(512), "字节");
                Check.Equal("1.0 KB", StorageStats.FormatBytes(1024), "千字节");
                Check.Equal("1.5 MB", StorageStats.FormatBytes(1572864), "兆字节");
                Check.Equal("1.0 GB", StorageStats.FormatBytes(1073741824), "吉字节");
                Check.Equal("0 B", StorageStats.FormatBytes(-5), "负数当零");
            });

            Runner.Add("存储统计", "渲染文本含占比与可清理标记", delegate
            {
                var store = new InMemoryFileStore();
                store.WriteTextAsync("test/conversations/a.json", new string('a', 100)).Wait();
                store.WriteTextAsync("_backups/b.zip", new string('b', 300)).Wait();

                var service = new StorageStatsService(store, NullLogger.Instance);
                var stats = service.MeasureAsync("test", CancellationToken.None).Result;
                var text = StorageStatsService.Format(stats);

                Check.Contains(text, "共占用", "总量");
                Check.Contains(text, "75%", "占比");
                Check.Contains(text, "备份与导出", "分类名");
                Check.Contains(text, "可清理", "标出可清理项");
                Check.Contains(text, "不影响当前数据", "给出可清理项的做法");
            });

            Runner.Add("存储统计", "回收区有内容时才提示清空", delegate
            {
                var store = new InMemoryFileStore();
                store.WriteTextAsync("_trash/20260101-test/old.json", new string('t', 100)).Wait();

                var service = new StorageStatsService(store, NullLogger.Instance);
                var text = StorageStatsService.Format(service.MeasureAsync("test", CancellationToken.None).Result);

                Check.Contains(text, "回收区", "有回收区分类");
                Check.Contains(text, "无法恢复", "提醒清空不可恢复");
            });

            Runner.Add("存储统计", "备份与日志标为可清理，会话与附件不可", delegate
            {
                var store = new InMemoryFileStore();
                store.WriteTextAsync("test/conversations/a.json", "x").Wait();
                store.WriteBytesAsync("test/files/attachments/p.png", new byte[10]).Wait();
                store.WriteTextAsync("_backups/b.zip", "y").Wait();

                var service = new StorageStatsService(store, NullLogger.Instance);
                var stats = service.MeasureAsync("test", CancellationToken.None).Result;

                Check.True(stats.Find("backups").Cleanable, "备份可清理");
                Check.False(stats.Find("conversations").Cleanable, "会话不标可清理（要用户逐个决定）");
                Check.False(stats.Find("attachments").Cleanable, "附件不标可清理");
            });
        }
    }
}
