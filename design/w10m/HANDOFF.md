# W10M 设计稿交接

更新日期：2026-10-13

## 现在的状态

`design/w10m/` 是按 Windows 10 Mobile（Modern 设计语言）重画的全页面 HTML 设计稿，共 179 屏、13 组，覆盖 `docs/UI-HANDOFF.md` 里的全部页面和关键状态。它只用来演示和评审，不是 UWP 实现。

- 渲染检查通过（见下方验证结果）。
- 320 / 360 / 430 三个宽度的溢出审计全部为 0。
- 没有动 `docs/`、`design/prototype/`、`design/modern-mobile/`，也没有改 `Themes/Design.xaml`。

## 怎么打开

- 双击 `design/w10m/index.html`。
- 如果浏览器拦截 `file://`，在 `design/w10m` 目录下运行 `python -m http.server 8000`，然后打开 `http://localhost:8000`。
- 画廊里点任一手机，进入交互预览。支持点击、Esc / Backspace 返回、方向键切换。
- URL 参数：
  - `theme=light`：浅色主题
  - `w=320` 或 `w=430`：屏宽
  - `accent=E81123`：强调色
  - `nav=hw`：硬件按键导航栏
  - `only=<组 id>`：只看一组
  - `ids=a,b,c`：只看指定屏
  - `bare=1`：只显示网格
  - `nocap=1`：去掉说明文字
  - `audit=1`：运行溢出审计

## 文件

| 文件 | 内容 |
| --- | --- |
| `w10m.css` | 令牌（深色，以及 `.light` 浅色）、W10M 控件样式、时间线样式 |
| `kit.js` | 组件助手 `K.*`、时间线 `T.*`、会话条 `C.*`，另有 `K.ACCENTS`、`K.QUICK` |
| `app.js` | 画廊、查看器、工具栏、URL 参数、溢出审计 |
| `index.html` | 外壳：W10M 规范表、令牌对照表、覆盖表、画廊 |
| `screens-1-start.js` 至 `screens-6-data.js` | 页面定义，合计 179 屏 |
| `coverage.js` | 需求到页面的映射表（`COVER`，36 行） |

各组屏数：规范样张 6，启动与根导航 18，会话与执行 24，审批 · 追问 · 群聊 10，档案与人设 19，角色卡导入 10，模型与服务 10，工具与扩展 14，记忆与提示词 14，会话管理与文件 23，设置与内容规则 12，数据与备份 15，关于与诊断 4。

## 最近一轮的改动

1. 模型参数页：`temperature`、`top_p` 的「用模型默认」从单独一行复选框，改为滑块下方的复选框。勾选后滑块变灰。实现方式是 `K.sl` 新增 `def` 选项。
2. 一次性选项改用 CheckBox：导入选项、备份内容。这些选项要按按钮后才生效，所以不用 ToggleSwitch。设计稿规范表里已加一句说明。
3. 外观页：「跟随系统强调色」开着时，色块网格变灰、不可点。关掉后可以选。在查看器里拨开关可以看到联动。
4. 备份页：文件名完整换行，预计大小移到分组标题里。
5. 新增 `chat-resumed`「离开后切回」屏，覆盖 UI-HANDOFF §4 和 §7 里的挂起恢复。
6. 8 色色块在深色下加了内描边，黑色块不再几乎看不见。
7. 工具步骤里的长路径，可以在 `/` 和 `\` 处换行（`pathBreaks`）。之前 320 宽下路径会被拆成单个字。

## 验证结果

- `node --check` 通过：`kit.js`、`app.js`、`coverage.js`、`screens-2-chat.js`、`screens-3-profile.js`、`screens-6-data.js`。
- 渲染检查：13 组、179 屏。重复 id、渲染错误、`undefined` 泄漏、死链、`COVER` 缺失、未入表的屏，数量均为 0。
- 溢出审计（`?audit=1`）：320、360、430 三个宽度，179 屏溢出均为 0。
- 截图抽查：浅色主题、绿色和红色强调色、320 宽会话页、430 宽档案和模型页、备份页、参数页、外观页。

## 还没做或需要注意

- 没有把 b1 至 b11 全部截图批次重新过一遍。改动后只抽查了受影响的屏。
- UI-HANDOFF §7 里的软键盘、旋转、挂起恢复，设计稿只能给静态示意（`chat-keyboard`、`chat-landscape`、`chat-resumed`），实际表现要真机验证。
- 文字对比度只看了截图，没有做系统检查。
- Design.xaml 的建议改动（ControlFill、FieldBrush、CornerRadius 改 0 等）只写在 `index.html` 规范表里，还没有应用到 `Themes/Design.xaml`。
- `design/w10m/_check` 是一个空目录，创建于 0:31。检查脚本不引用它，来源不明。我没有删除，请确认后再处理。
- 截图和检查脚本放在系统临时目录（`C:\Users\Ouyang Quan\AppData\Local\Temp\w10m-*`），不在仓库里。

## 修改页面的方法

- 新增页面：在对应的 `screens-N-*.js` 里调用 `screen({ id, t, src, note, render })`，放进所在的 `group`。然后在 `coverage.js` 的对应行里加上这个 id，否则检查会报「screens not in COVER」。
- 开关控制另一个控件的禁用状态：开关上写 `ctl: 'x'`，被控制的控件上写 `ctlt: 'x'`。`K.swg` 支持这个写法，`K.sl` 则用 `def` 挂载复选框。联动只在同一个 `.phone` 内生效，不用 id，因为画廊和查看器会同时渲染同一屏。
- 颜色尽量用 CSS 变量（如 `var(--Accent)`），不写硬编码色值。变量名与 `Design.xaml` 的键对应。

## 临时验证脚本（不在仓库里）

- 渲染检查：`node "<TEMP>\w10m-check.cjs" "<仓库>\design\w10m"`
- 无头截图：`w10m-shot.ps1 -Name <名字> -Query '<URL 参数>' -W <宽> -H <高>`。它用的是旧版 `--headless` 和 `Start-Process -Wait`。
- 列出各组的屏 id：`w10m-ids.cjs`
- 路径用 `(Get-Item $env:TEMP).FullName` 获取。本机的真实临时目录是 `C:\Users\Ouyang Quan\AppData\Local\Temp`。

## 建议的下一步

1. 对照 UI-HANDOFF §7，在真机上验证软键盘、旋转和挂起恢复。
2. 决定 `design/w10m/_check` 是否删除。
3. 确认 Design.xaml 的建议改动，再开始 UWP 实现。
4. 可选：重新过一遍 b1 至 b11 的全量截图。
