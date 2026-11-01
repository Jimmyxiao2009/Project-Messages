# 言枢 · Modern / Windows 10 Mobile

完整交互原型，共 **140 个页面与状态**，保留原有 `design/prototype/`，未修改 UWP 的 Core、Platform 或 UI。

直接打开 **prototype.html**（单文件离线版），或运行：

```powershell
python -m http.server 8765 --bind 127.0.0.1 --directory design/modern-mobile
```

访问 `http://127.0.0.1:8765/`。

- 左侧按 11 组列出全部页面；可以搜索，也可以用页面 hash 直接定位。
- 「页面总览」同时呈现全部页面缩略图，点击进入交互预览。
- 360×640 为默认基准；工具栏可切换 320×640 和 430×740；手机浏览器自动全屏适配。
- 支持深色 / 浅色及蓝、红、绿、紫、橙单强调色。
- 已接通页面导航、表单填写、开关、搜索、模型选择与收藏、候选切换、快捷填入、附件选择、确认弹层、示例下载、导入与恢复流程。

来源为 `docs/UI-HANDOFF.md`、`docs/设计稿.md`、`docs/CAPABILITIES.md` 和原 15 屏原型。逐项清单见 **PAGE-COVERAGE.md**。

视觉规则：大字号 Pivot、系统字体、单列布局、直角控件、底部 CommandBar；用户消息是唯一气泡；工具与思考走左侧轨道；审批固定在会话顶部。

这是前端设计原型。模型请求、MCP 测试、ZIP 打包、工作区修改、朗读与数据恢复使用演示结果；不连接服务，不读取实际工作区，不保存 API 密钥。选择本地文件只用于本地文件名 / 图片预览。

验证见 **verification.json**：140 页 DOM 内容与路由检查、17 项主要交互通过。当前环境无可用浏览器窗口，未完成实际渲染、真实键盘或真机检查。320 / 430 是已实现的预览尺寸，不代表已在真机验收。

文件：`index.html` 页面壳、`styles.css` 设计令牌与样式、`app.js` 页面清单与交互、`prototype.html` 自动生成的离线单文件。修改源码后使用 `.superdesign/qa/export.mjs` 重新生成单文件与静态画布稿；它依赖本地 QA 的 happy-dom。


全部 140 页也已导入并回读核对到 [Superdesign 画布](https://superdesign.dev/teams/2a786f92-dde2-44dd-b17c-8f6c94277721/projects/45c1ba6a-9e6c-4e31-9708-a7d69d04938e)，这是逐页静态评审稿；完整可点击交互使用本地 prototype.html。画布页面记录见 canvas-pages.json。
