using System;
using System.Linq;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class StorageCleanupPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            Title("清理存储空间", "数据");
            var files = await S.Files.ListFilesAsync("", true);
            var backups = files.Where(x => x.StartsWith("_backups/") || x.StartsWith("_exports/") || x.StartsWith("_migration-backup/"))
                .Where(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x, StringComparer.Ordinal).ToList();
            var logs = files.Where(x => x == DiagnosticLog.RotatedPath).ToList();
            var backup = Check("旧备份与导出（保留文件名排序最新的一份）", false);
            var log = Check("轮换诊断日志（保留当前日志）", false);
            var trash = Check("永久清空回收区", false);
            Text("会话、附件与记忆不在这里清理。", true);
            Command("清理所选", Windows.UI.Xaml.Controls.Symbol.Delete, async delegate {
                RequireIdle();
                if (!await Confirm("执行存储清理？", "所选文件将永久删除，回收区也无法恢复。", "清理")) return;
                int count = 0;
                if (backup.IsChecked == true) foreach (var file in backups.Skip(1)) { await S.Files.DeleteAsync(file); count++; }
                if (log.IsChecked == true) foreach (var file in logs) { await S.Files.DeleteAsync(file); count++; }
                if (trash.IsChecked == true) {
                    foreach (var entry in await S.Operations.ListTrashAsync()) { await S.Operations.PurgeAsync(entry); count++; }
                    await W.PurgeTrashAsync();
                }
                Status.Text = "已清理 " + count + " 项。";
            });
        }
    }
}
