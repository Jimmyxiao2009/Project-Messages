# W10M 设计与原生实现对应

更新时间：2026-10-14；应用版本：1.2.0.0。

设计定义 179 屏（包括规范样张和状态），实现 56 个独立原生页面类；根 Pivot 和会话时间线仍在 MainPage。以下表格记录源码对应关系，不代表逐屏视觉验收通过。

已验证：x86 Debug 与 ARM Release 构建通过；核心回归 375/375。ARM Release 包含 .NET Native 与 PyUwp 原生载荷；两包内清单均为 1.2.7.0，架构和 SHA256 记录在 build-artifacts.json。包未签名，真机安装使用项目 Deploy-Phone.ps1 的签名流程。

真机验证（1.2.4.0–1.2.6.0，Lumia / W10M 15254.603 ARM）：启动不再闪退，引导页可正常构建与渲染，进程持续存活。此前"全新安装启动即退出"的根因是纯代码页面缺 XAML 激活器，见 `Ui\PageTypeRegistry.xaml` 与 UI-HANDOFF §5。

桌面启动补验：重新编译并注册 x86 Debug 1.1.0.0，修复 StatusBar 手机类型在桌面提前加载导致的导航失败；本次启动日志确认 MainPage 主题与根列表初始化、窗口激活完成，应用进程持续响应。此项为进程和日志验证，尚未完成截图与逐页视觉验收。

实际界面检查：Computer Use 自动权限检查返回“Computer Use was not approved to use 言枢·消息”。用户已同意检查，但工具权限仍未生效。没有使用其他自动化方式绕过限制。

仍需完成：179 屏逐项视觉对照、320/360/430 宽度、软键盘、旋转、挂起恢复及真机 PyUwp/TTS/PDF/外部文件夹验证。模型/MCP 网络连接通过模拟传输及本地 HTTP 回归验证，未验证用户实际服务。

本轮补齐：头像拍照/裁剪与用户人设头像、API 复制与列表长按菜单、应用前的压缩预览、长历史滚动保持/自动跟随/回到底部、MCP 输入 schema、离线发送限制和上下文额度警告。额度是本地估算，摘要计入一次并排除已压缩历史，阈值采用 Limits.CompactThreshold。

后端回归覆盖：草稿首条消息落盘、会话模型及密钥覆盖、可恢复回收区、跨档案移动与附件复制、文件编辑冲突、授权目录范围和只读限制、搜索、选择性备份、结构化追问、MCP 会话协商与分页，以及不修改正文的压缩预览。

旧数据迁移支持 Roleplay AppData 的模型配置、角色、会话分支、图片与会话记忆；不迁移旧密钥/授权，不修改旧文件。独立旧 memory_store 和其他旧配置格式未纳入本轮迁移。

网络状态使用系统 NetworkStatusChanged 事件刷新，参见 [Microsoft API 文档](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.networkinformation.networkstatuschanged?view=winrt-26100)。

Provider 多模型改版：新增 ProviderModelsPage，支持按服务管理/刷新模型、会话顶部跨 Provider 切换、自动获取能力与独立手动覆盖。详细使用与验证记录见 [PROVIDER-MODELS.md](PROVIDER-MODELS.md)。

| 设计 ID | 页面 / 状态 | 原生对应 | 当前状态 |
| --- | --- | --- | --- |
| spec-type | 文字刻度与页头 | Design.xaml / DetailPage / 时间线规范 | 规范参照 |
| spec-controls | 基础控件 | Design.xaml / DetailPage / 时间线规范 | 规范参照 |
| spec-lists | 列表行与状态 | Design.xaml / DetailPage / 时间线规范 | 规范参照 |
| spec-appbar | 应用栏的三种状态 | Design.xaml / DetailPage / 时间线规范 | 规范参照 |
| spec-dialog | 对话框与菜单 | Design.xaml / DetailPage / 时间线规范 | 规范参照 |
| spec-timeline | 时间线行类型 | Design.xaml / DetailPage / 时间线规范 | 规范参照 |
| startup | 启动与初始化 | App / RestoreStartupAsync | 代码接线已构建；未做逐屏界面验收 |
| startup-error | 初始化失败 | App / RestoreStartupAsync | 代码接线已构建；未做逐屏界面验收 |
| crash-notice | 上次异常退出 | App / RestoreStartupAsync | 代码接线已构建；未做逐屏界面验收 |
| migration | 旧数据迁移 | MigrationPage | 代码接线已构建；未做逐屏界面验收 |
| migration-running | 迁移进行中 | MigrationPage | 代码接线已构建；未做逐屏界面验收 |
| oobe-1 | 引导 1 / 3 · 连接模型 | OnboardingPage | 代码接线已构建；未做逐屏界面验收 |
| oobe-2 | 引导 2 / 3 · 第一个档案 | OnboardingPage | 代码接线已构建；未做逐屏界面验收 |
| oobe-3 | 引导 3 / 3 · 完成 | OnboardingPage | 代码接线已构建；未做逐屏界面验收 |
| home | 对话（根 Pivot 第 1 页） | MainPage 根 Pivot | 代码接线已构建；未做逐屏界面验收 |
| home-filtered | 对话 · 按档案筛选 | MainPage 根 Pivot | 代码接线已构建；未做逐屏界面验收 |
| home-appbar | 对话 · 应用栏展开 | MainPage 根 Pivot | 代码接线已构建；未做逐屏界面验收 |
| home-select | 批量管理会话 | ConversationsPage | 代码接线已构建；未做逐屏界面验收 |
| home-empty | 对话 · 一条会话都没有 | MainPage 根 Pivot | 代码接线已构建；未做逐屏界面验收 |
| search | 全局搜索 | GlobalSearchPage | 代码接线已构建；未做逐屏界面验收 |
| search-empty | 搜索无结果 | GlobalSearchPage | 代码接线已构建；未做逐屏界面验收 |
| profiles | 档案（根 Pivot 第 2 页） | MainPage 根 Pivot | 代码接线已构建；未做逐屏界面验收 |
| profiles-empty | 档案 · 空态 | MainPage 根 Pivot | 代码接线已构建；未做逐屏界面验收 |
| me | 我的（根 Pivot 第 3 页） | MainPage 根 Pivot | 代码接线已构建；未做逐屏界面验收 |
| new-chat | 新建会话（草稿设置） | NewConversationPage | 代码接线已构建；未做逐屏界面验收 |
| profile-select | 选择档案 | NewConversationPage | 代码接线已构建；未做逐屏界面验收 |
| chat-draft | 会话 · 草稿空态 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat | 会话 · 完成 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-markdown | 会话 · Markdown 与公式 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-menu | 会话 · 「⋯」菜单 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-running | 会话 · 运行中 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-approval | 会话 · 待审批 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-error | 会话 · 请求失败 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-offline | 会话 · 离线 | MainPage.Status / 会话提示栏 | 代码接线已构建；未做逐屏界面验收 |
| chat-context-limit | 会话 · 上下文将满 | MainPage.Status / 会话提示栏 | 代码接线已构建；未做逐屏界面验收 |
| chat-roleplay | 会话 · 角色对话 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-branches | 会话 · 候选分支 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-speaking | 会话 · 朗读 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-translated | 会话 · 译文叠加 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-keyboard | 会话 · 软键盘弹出 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-compose-attach | 会话 · 带附件待发送 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-history | 会话 · 长会话与回到底部 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| message-actions | 长按 · 用户消息 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| message-actions-ai | 长按 · 模型输出 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| message-edit | 编辑消息 | MessageEditPage | 代码接线已构建；未做逐屏界面验收 |
| fork | 从这里分支 | MessageEditPage | 代码接线已构建；未做逐屏界面验收 |
| chat-landscape | 会话 · 横屏 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| chat-resumed | 会话 · 离开后切回 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| approval | 敏感工具审批 · 写入文件 | InteractionPage | 代码接线已构建；未做逐屏界面验收 |
| approval-python | 敏感工具审批 · 运行脚本 | InteractionPage | 代码接线已构建；未做逐屏界面验收 |
| approval-device | 敏感工具审批 · 发短信 | InteractionPage | 代码接线已构建；未做逐屏界面验收 |
| ask-user | 向你追问 | InteractionPage | 代码接线已构建；未做逐屏界面验收 |
| schema-form | 结构化输入 | InteractionPage | 代码接线已构建；未做逐屏界面验收 |
| group-chat | 群聊会话 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| group-members | 群聊成员 | GroupPage | 代码接线已构建；未做逐屏界面验收 |
| group-add | 添加群聊成员 | GroupMemberPage | 代码接线已构建；未做逐屏界面验收 |
| group-member-edit | 编辑群聊成员 | GroupMemberPage | 代码接线已构建；未做逐屏界面验收 |
| group-mode | 群聊回复模式 | GroupPage | 代码接线已构建；未做逐屏界面验收 |
| profile | 档案详情 | ProfilePage | 代码接线已构建；未做逐屏界面验收 |
| profile-new | 新建档案 | NewProfilePage | 代码接线已构建；未做逐屏界面验收 |
| profile-identity | 编辑身份 | ProfileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| avatar | 选择头像 | FilesPage avatar / AvatarCropPage | 代码接线已构建；未做逐屏界面验收 |
| profile-persona | 编辑人设 | ProfileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| profile-persona-keyboard | 编辑人设 · 键盘弹出 | ProfileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| profile-unsaved | 未保存改动 | DetailPage 确认 / ProfilePage | 代码接线已构建；未做逐屏界面验收 |
| profile-model | 模型与生成参数 | ProfileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| request-overrides | 自定义请求字段 | ProfileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| profile-tools | 工具 · MCP · 技能 | ToolsPage | 代码接线已构建；未做逐屏界面验收 |
| profile-memory | 记忆设置 | ProfileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| profile-prompts | 世界书 · 注入（档案侧勾选） | ResourcesPage selection | 代码接线已构建；未做逐屏界面验收 |
| profile-appearance | 档案外观 | AppearancePage | 代码接线已构建；未做逐屏界面验收 |
| prompt-assembly | 查看最终提示词 | PromptPreviewPage | 代码接线已构建；未做逐屏界面验收 |
| profile-delete | 删除档案 | DetailPage 确认 / ProfilePage | 代码接线已构建；未做逐屏界面验收 |
| conversations | 档案的会话 | ConversationsPage | 代码接线已构建；未做逐屏界面验收 |
| user-personas | 用户人设 | ResourcesPage user-personas | 代码接线已构建；未做逐屏界面验收 |
| user-persona-edit | 编辑用户人设 | ResourceEditorPage user | 代码接线已构建；未做逐屏界面验收 |
| user-persona-new | 新建用户人设 | ResourceEditorPage user | 代码接线已构建；未做逐屏界面验收 |
| import-source | 导入 1 / 3 · 选来源 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-url | 导入 · 从链接 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-parsing | 导入 · 解析中 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-preview | 导入 2 / 3 · 预览与补齐 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-options | 导入 3 / 3 · 落盘选项 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-done | 导入完成 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-error | 导入失败 · 文件过大 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-not-card | 导入 · 这不是角色卡 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-error-ztxt | 导入失败 · 解压失败 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| import-error-empty | 导入失败 · 没有字段 | ImportPage | 代码接线已构建；未做逐屏界面验收 |
| api-profiles | 模型档案列表 | ApiProfilesPage / ProviderModelsPage | 代码接线已构建；未做逐屏界面验收 |
| api-actions | 模型档案 · 长按菜单 | ApiProfilesPage / Actions | 代码接线已构建；未做逐屏界面验收 |
| api-new | 新建模型档案 · 选类型 | ApiEditorPage | 代码接线已构建；未做逐屏界面验收 |
| api-edit | 编辑模型档案 | ApiEditorPage | 代码接线已构建；未做逐屏界面验收 |
| api-test | 连接测试结果 | ApiTestPage | 代码接线已构建；未做逐屏界面验收 |
| model-picker | 模型选择器 | ModelPickerPage / ProviderModelsPage | 代码接线已构建；未做逐屏界面验收 |
| model-picker-search | 模型选择器 · 搜索 | ModelPickerPage | 代码接线已构建；未做逐屏界面验收 |
| model-manual | 手动添加模型 | ModelDetailsPage | 代码接线已构建；未做逐屏界面验收 |
| model-details | 模型元数据与覆盖 | ModelDetailsPage | 代码接线已构建；未做逐屏界面验收 |
| search-settings | 联网搜索 | SearchSettingsPage | 代码接线已构建；未做逐屏界面验收 |
| tool-catalog | 全部内置工具 | ToolsPage | 代码接线已构建；未做逐屏界面验收 |
| tool-detail | 工具详情 | ToolDetailsPage | 代码接线已构建；未做逐屏界面验收 |
| script-runtime | 脚本运行时（run_python） | ToolDetailsPage | 代码接线已构建；未做逐屏界面验收 |
| mcp-list | MCP 服务器 | ExtensionsPage mcp | 代码接线已构建；未做逐屏界面验收 |
| mcp-edit | 编辑 MCP 服务器 | ExtensionEditorPage mcp | 代码接线已构建；未做逐屏界面验收 |
| mcp-new | 新增 MCP 服务器 | ExtensionEditorPage mcp | 代码接线已构建；未做逐屏界面验收 |
| mcp-detail | MCP 工具清单 | McpToolsPage / McpSchemaPage | 代码接线已构建；未做逐屏界面验收 |
| skills | 技能 | ExtensionsPage skills | 代码接线已构建；未做逐屏界面验收 |
| skill-edit | 编辑技能 | ExtensionEditorPage skill | 代码接线已构建；未做逐屏界面验收 |
| skill-new | 新建技能 | ExtensionEditorPage skill | 代码接线已构建；未做逐屏界面验收 |
| permissions | 权限记录 | PermissionsPage | 代码接线已构建；未做逐屏界面验收 |
| permission-detail | 授权详情与撤销 | PermissionsPage | 代码接线已构建；未做逐屏界面验收 |
| authorized-folders | 授权目录 | FolderAccessPage | 代码接线已构建；未做逐屏界面验收 |
| folder-permission | 目录授权设置 | FolderPermissionPage | 代码接线已构建；未做逐屏界面验收 |
| memory | 记忆列表 | MemoryPage | 代码接线已构建；未做逐屏界面验收 |
| memory-empty | 记忆 · 空态 | MemoryPage | 代码接线已构建；未做逐屏界面验收 |
| memory-edit | 编辑记忆 | MemoryEditorPage | 代码接线已构建；未做逐屏界面验收 |
| memory-new | 新增记忆 | MemoryEditorPage | 代码接线已构建；未做逐屏界面验收 |
| memory-clean | 整理记忆 · 预览 | MemoryMaintenancePage | 代码接线已构建；未做逐屏界面验收 |
| memory-index | 记忆索引状态 | MemoryMaintenancePage | 代码接线已构建；未做逐屏界面验收 |
| memory-global | 全局记忆池 | MemoryPage | 代码接线已构建；未做逐屏界面验收 |
| memory-clear | 清空记忆 | MemoryPage | 代码接线已构建；未做逐屏界面验收 |
| lorebooks | 世界书列表 | ResourcesPage | 代码接线已构建；未做逐屏界面验收 |
| lorebook-new | 新建世界书 | ResourceEditorPage | 代码接线已构建；未做逐屏界面验收 |
| lorebook-detail | 世界书详情 | ResourcesPage | 代码接线已构建；未做逐屏界面验收 |
| lore-entry-edit | 编辑世界书条目 | ResourceEditorPage | 代码接线已构建；未做逐屏界面验收 |
| injections | 模式注入列表 | ResourcesPage | 代码接线已构建；未做逐屏界面验收 |
| injection-edit | 编辑模式注入 | ResourceEditorPage | 代码接线已构建；未做逐屏界面验收 |
| chat-settings | 会话设置 | ChatSettingsPage | 代码接线已构建；未做逐屏界面验收 |
| chat-delete | 删除会话 | MainPage 会话 / 时间线 / 长按菜单 | 代码接线已构建；未做逐屏界面验收 |
| archived | 已归档会话 | ConversationsPage | 代码接线已构建；未做逐屏界面验收 |
| move-conversations | 移动到档案 | MoveConversationsPage | 代码接线已构建；未做逐屏界面验收 |
| chat-appearance | 会话外观 | AppearancePage | 代码接线已构建；未做逐屏界面验收 |
| context | 上下文与压缩 | ContextPage | 代码接线已构建；未做逐屏界面验收 |
| context-preview | 上下文压缩预览 | CompactPreviewPage | 代码接线已构建；未做逐屏界面验收 |
| speech-settings | 朗读设置 | SettingsPage | 代码接线已构建；未做逐屏界面验收 |
| translation | 消息翻译 | SettingsPage | 代码接线已构建；未做逐屏界面验收 |
| usage | 会话用量 | DiagnosticsPage | 代码接线已构建；未做逐屏界面验收 |
| conversation-export | 导出会话 | DataPage | 代码接线已构建；未做逐屏界面验收 |
| attachments | 会话附件 | FilesPage | 代码接线已构建；未做逐屏界面验收 |
| attachment-add | 添加附件 | FilesPage | 代码接线已构建；未做逐屏界面验收 |
| attachment-preview | 附件发送前预览 | FilePreviewPage | 代码接线已构建；未做逐屏界面验收 |
| attachment-settings | 附件处理设置 | SettingsPage | 代码接线已构建；未做逐屏界面验收 |
| files | 档案文件 | FilesPage | 代码接线已构建；未做逐屏界面验收 |
| files-empty | 文件 · 空态 | FilesPage | 代码接线已构建；未做逐屏界面验收 |
| file-folder | 文件夹内容 | FilesPage | 代码接线已构建；未做逐屏界面验收 |
| file-preview | 文件解析预览 | FilePreviewPage | 代码接线已构建；未做逐屏界面验收 |
| file-editor | 文本文件编辑 | FileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| file-new | 新建文本文件 | FileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| file-conflict | 文件保存冲突 | FileEditorPage | 代码接线已构建；未做逐屏界面验收 |
| image-viewer | 图片查看 | FilePreviewPage | 代码接线已构建；未做逐屏界面验收 |
| general | 通用设置 | SettingsPage | 代码接线已构建；未做逐屏界面验收 |
| appearance | 主题与强调色 | AppearancePage | 代码接线已构建；未做逐屏界面验收 |
| default-appearance | 默认会话外观 | AppearancePage | 代码接线已构建；未做逐屏界面验收 |
| typography | 文字与可读性 | SettingsPage | 代码接线已构建；未做逐屏界面验收 |
| workspace | 工作区位置 | SettingsPage | 代码接线已构建；未做逐屏界面验收 |
| quick-messages | 快捷消息 | ContentRulesPage | 代码接线已构建；未做逐屏界面验收 |
| quick-edit | 编辑快捷消息 | RuleEditorPage | 代码接线已构建；未做逐屏界面验收 |
| quick-new | 新增快捷消息 | RuleEditorPage | 代码接线已构建；未做逐屏界面验收 |
| regex-list | 正则转换器 | ContentRulesPage | 代码接线已构建；未做逐屏界面验收 |
| regex-edit | 编辑正则规则 | RuleEditorPage | 代码接线已构建；未做逐屏界面验收 |
| regex-new | 新增正则规则 · 校验失败 | RuleEditorPage | 代码接线已构建；未做逐屏界面验收 |
| regex-test | 正则测试 | RegexTestPage | 代码接线已构建；未做逐屏界面验收 |
| storage | 存储空间 | StoragePage | 代码接线已构建；未做逐屏界面验收 |
| storage-clean | 清理空间确认 | StorageCleanupPage | 代码接线已构建；未做逐屏界面验收 |
| trash | 回收区 | TrashPage | 代码接线已构建；未做逐屏界面验收 |
| trash-detail | 回收项详情 | TrashPage | 代码接线已构建；未做逐屏界面验收 |
| backup | 备份与恢复 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| backup-create | 创建备份 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| backup-progress | 备份进行中 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| backup-done | 备份完成 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| backup-select | 选择恢复文件 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| backup-preview | 备份检查与预览 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| restore-confirm | 恢复二次确认 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| restore-result | 恢复完成 | BackupPage | 代码接线已构建；未做逐屏界面验收 |
| data | 导入与导出 | DataPage | 代码接线已构建；未做逐屏界面验收 |
| data-export | 导出档案 | DataPage | 代码接线已构建；未做逐屏界面验收 |
| data-import-preview | 旧版数据检查 | MigrationPage | 代码接线已构建；未做逐屏界面验收 |
| about | 关于言枢 | AboutPage | 代码接线已构建；未做逐屏界面验收 |
| debug | 调试日志 | DiagnosticsPage | 代码接线已构建；未做逐屏界面验收 |
| crash-log | 崩溃记录 | DiagnosticsPage | 代码接线已构建；未做逐屏界面验收 |
| reset-index | 重建会话索引 | SettingsPage | 代码接线已构建；未做逐屏界面验收 |
