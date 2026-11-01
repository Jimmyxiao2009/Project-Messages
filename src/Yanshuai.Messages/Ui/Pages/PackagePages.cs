// Ui/Pages/PackagePages.cs —— Chronos 本地包管理页（pip 风格的"已安装"列表）
//
// 对应工具侧的 package_list / package_uninstall / package_install：
// 这里只做"看得到、卸得掉、知道哪个还没编译"。安装走 package_install 工具
// （需要一个带 chronos.json + module.tccmod 的目录），UI 里不重复造一遍。

using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Yanshuai.Messages.Core;
using Yanshuai.Messages.Core.Packages;

namespace Yanshuai.Messages.Ui.Pages
{
    public sealed class PackagesPage : DetailPage
    {
        protected override async Task BuildAsync()
        {
            Title("本地包", "工具与扩展");
            Text("每个包是 _packages/ 下的一个目录：chronos.json 清单 + module.tccmod 编译产物。"
               + "编译一次之后反复调用只是加载，不会重新编译。", true);

            var store = new PackageStore(S.Files, S.Log);
            var packages = await store.ListAsync(Lifetime.Token).ConfigureAwait(true);

            if (packages.Count == 0)
            {
                Text("还没有安装任何包。");
                Text("用 package_install 工具把带 chronos.json 与 module.tccmod 的目录装进来。", true);
                return;
            }

            foreach (var package in packages)
            {
                var hasModule = await store.HasModuleAsync(package.Id, Lifetime.Token).ConfigureAwait(true);
                var captured = package;
                Section(package.Name + "（" + package.Id + "）");
                Text("入口：" + package.Entry + (hasModule ? " · 已编译" : " · **未编译，还跑不了**"), true);
                if (!string.IsNullOrEmpty(package.Source)) Text("源码：" + package.Source, true);
                Command("卸载", Symbol.Delete, async delegate
                {
                    RequireIdle();
                    if (!await Confirm("卸载 " + captured.Id + "？", "包目录会从 _packages/ 移除。", "卸载")) return;
                    await store.UninstallAsync(captured.Id, Lifetime.Token);
                    Status.Text = "已卸载 " + captured.Id;
                    await BuildAsync();
                }, true);
            }
        }
    }
}