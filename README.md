# 言枢 Messages

面向 Windows 10 Mobile 的原生 UWP AI 对话客户端，也提供 Windows 桌面调试构建。会话、档案和附件保存在本地工作区；模型服务需要自行配置 API 地址、模型与密钥，本项目不提供默认服务或额度。

## 当前功能

- OpenAI 兼容、Anthropic 与 Gemini 接口，支持流式输出、模型配置和切换。
- 助手与角色档案、角色卡导入、多角色会话、消息编辑、重新生成与会话分支。
- 工具调用及过程时间线、分档权限和敏感操作审批；支持 MCP HTTP 接入。
- 本地文件与附件、Office 文档解析和 PDF 预览，以及记忆、上下文压缩、备份恢复、朗读与翻译。
- 可选 ARM32 Python 运行时接入。未提供运行时载荷时，脚本工具会报告运行时不可用；这不影响普通聊天构建。

这些功能依赖所选模型的能力、服务端协议及相应权限，不代表所有模型都支持全部功能。

## 安装

从 [Releases](https://github.com/Jimmyxiao2009/Project-Messages/releases) 下载 ARM 安装 ZIP，按包内 `INSTALL.txt` 操作：在手机开启开发者模式，安装或信任公钥 `.cer`，安装 `Dependencies/ARM` 中的框架依赖，再侧载主 `.appx`；也可使用 Device Portal 部署。依赖必须是 ARM32，ARM64 包不能代替。

当前备份发布版本为 **1.3.17.1**，最低系统声明为 Windows 10 Mobile **10.0.14393.0**。本次发布完成 ARM Release 构建、签名和安装包静态校验，**未进行手机真机安装及交互实测**。

## 源码结构

```text
src/Yanshuai.Messages/
  Core/       模型、服务协议、会话、工具、权限与工作区逻辑
  Platform/   Windows 存储、密钥、语音、PDF 与原生运行时适配
  Ui/         根页面、会话界面、详情页及嵌套导航
  Themes/     原生 UWP 样式与资源
  Assets/     应用图标及磁贴资源
tests/CoreTests/  仅链接 Core 的桌面测试运行器
tools/           构建、签名、部署与诊断脚本
docs/            功能索引、设计与历史验证记录
```

新版入口是 `src/Yanshuai.Messages/Yanshuai.Messages.csproj`。相关说明见 [UI 交接](docs/UI-HANDOFF.md)、[模型配置](docs/PROVIDER-MODELS.md)与[原生页面覆盖](docs/NATIVE-PAGE-COVERAGE.md)；其中带日期的能力和验证记录保留了历史状态。

## 构建

使用 **Visual Studio 2017 15.9 / MSBuild 15**、UWP 开发工具和 **Windows SDK 10.0.19041.0**（含 Windows Mobile 扩展）。工程锁定 C# 7.3、`Microsoft.NETCore.UniversalWindowsPlatform 5.4.7` 与 `Newtonsoft.Json 10.0.3`，以兼容手机；桌面测试工程的较新 .NET SDK 不改变手机目标。

在仓库根目录运行 PowerShell：

```powershell
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'
& $msbuild src\Yanshuai.Messages\Yanshuai.Messages.csproj /restore /t:Build `
    /p:Configuration=Release /p:Platform=ARM `
    /p:AppxBundle=Never /p:AppxPackageSigningEnabled=false
```

上述命令生成未签名包。需要签名时可运行 `tools/Build-AppxBundle.ps1 -Platforms ARM`；它依次构建、签名单架构包并生成 bundle。证书需带私钥且主题与清单 `Publisher` 一致，脚本默认查找 `CN=LockWizard`。产物位于 `src/Yanshuai.Messages/AppPackages/`，手机侧载使用其中的 ARM `.appx`。

Python 为可选载荷，可通过构建参数 `PyUwpRuntimeDir` 指向 ARM 原生运行时目录。普通克隆缺少该载荷时仍可构建；详情见工程文件中的条件引用说明。

## 验证状态

2026-11-01 的 Core 测试结果为 **356 / 375 通过，19 项失败**。失败涉及旧测试对工具流式响应、权限默认行为、脚本开关及端到端请求的假设，不能宣称测试全绿。测试工程只链接 `Core`，不覆盖 UWP 页面、手机硬件返回键或原生运行时。

```powershell
dotnet run --project tests\CoreTests\CoreTests.csproj -c Release
```

该测试运行器目前需要 .NET 10 SDK。本次已调整详情页与工作区切换的返回层级；手机实际交互仍需后续真机验证。
