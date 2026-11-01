/* 06 模型与服务 · 07 工具与扩展 */
(function () {
  'use strict';
  var ic = K.ic;
  function star(on) { return K.ibtn(on ? 'E735' : 'E734', { on: on, toggle: true }); }
  function model(id, sub, caps, o) {
    o = o || {};
    return K.row({ t: '<span class="mono" style="font-size:14px">' + id + '</span>', s: sub, l3: caps.map(function (c) { return K.bd(c[0], c[1]); }).join(''), sel: o.sel, right: star(o.fav), go: o.go || 'profile-model' });
  }
  function grp(name, meta, open) {
    return '<div class="sec tilt" style="cursor:pointer"><span>' + name + '</span><span style="letter-spacing:0;font-weight:400">' + meta + '</span><span class="sp"></span>' + ic(open ? 'E70E' : 'E70D') + '</div>';
  }

  /* ======================= 06 模型与服务 ======================= */
  group('model', '模型与服务', '模型档案（API 配置）列表 → 编辑 → 连接测试；模型选择器按模型档案分组、可收藏、折叠状态会记住（逻辑在 ModelCatalog，界面此前完全没做）。');

  screen({
    id: 'api-profiles', t: '模型档案列表', src: '设计稿 §6.9 · ApiProfileFile.defaultId',
    note: ['默认项名称前加 ★；副标题：提供商 · 默认模型 · 连通状态', '状态靠左色条：失败 = 错误色；其余中性', '长按：设为默认 / 复制 / 删除'],
    render: function () {
      return K.ph('我的', '模型档案') + K.body(
        K.row({ ic: 'E735', t: 'DeepSeek', s: 'OpenAI 兼容 · deepseek-chat · 正常 · 48 个模型', bar: 'n', go: 'api-edit' }) +
        K.row({ ic: 'E774', icMid: true, t: 'Claude 中转', s: 'Anthropic · claude-sonnet-4.5 · 正常 · 12 个模型', bar: 'n', go: 'api-edit' }) +
        K.row({ ic: 'E774', icMid: true, t: 'Gemini', s: 'Gemini 原生 · gemini-2.5-flash · 429 限流', bar: 'err', go: 'api-test' }) +
        K.row({ ic: 'E774', icMid: true, t: 'LM Studio', s: 'OpenAI 兼容 · 127.0.0.1:1234 · 还没测试', bar: 'n', go: 'api-edit' }) + K.gap(8) +
        K.note('连本机服务需要回环豁免：CheckNetIsolation LoopbackExempt。')
      ) + K.ab([{ ic: 'E710', t: '新建', go: 'api-new' }, { ic: 'E895', t: '全部测试', go: 'api-test' }], [{ t: '长按菜单示例', go: 'api-actions' }]);
    }
  });

  screen({
    id: 'api-actions', t: '模型档案 · 长按菜单', src: '设计稿 §6.9「长按设为默认 / 复制 / 删除」',
    note: ['全宽 MenuFlyout，贴在被长按的行下方'],
    render: function () {
      return K.ph('我的', '模型档案') + K.body(
        K.row({ ic: 'E735', t: 'DeepSeek', s: 'OpenAI 兼容 · deepseek-chat · 正常', bar: 'n' }) +
        K.row({ ic: 'E774', icMid: true, t: 'Claude 中转', s: 'Anthropic · claude-sonnet-4.5 · 正常', sel: true })) +
        K.fly([{ ic: 'E735', t: '设为默认' }, { ic: 'E895', t: '测试连接', go: 'api-test' }, { ic: 'E8C8', t: '复制一份' }, '-', { ic: 'E74D', t: '删除', s: '密钥一并移除' }], 180);
    }
  });

  screen({
    id: 'api-new', t: '新建模型档案 · 选类型', src: '设计稿 §6.9 · OpenAI / Anthropic / Gemini 三套 provider',
    note: ['先选预设，自动填 URL 与提供商', '「自定义」走 OpenAI 兼容'],
    render: function () {
      return K.picker('选择服务', [
        { t: 'DeepSeek', s: 'OpenAI 兼容 · api.deepseek.com', go: 'api-edit' },
        { t: 'OpenAI', s: 'api.openai.com', go: 'api-edit' },
        { t: 'Anthropic', s: '原生接口 · api.anthropic.com', go: 'api-edit' },
        { t: 'Gemini', s: '原生接口 · generativelanguage.googleapis.com', go: 'api-edit' },
        { t: 'OpenRouter', s: 'OpenAI 兼容 · openrouter.ai', go: 'api-edit' },
        { t: '自定义', s: '中转站、LM Studio、Ollama…', go: 'api-edit' }]);
    }
  });

  screen({
    id: 'api-edit', t: '编辑模型档案', src: '设计稿 §6.9 · ApiProfile 字段',
    note: ['密钥只进 PasswordVault，配置里只存引用', '默认模型行进模型选择器；视觉自动探测 + 手动覆盖', '「测试连接」就地显示结果'],
    render: function () {
      return K.ph('模型档案', 'DeepSeek') + K.body(
        K.tb('名称', 'DeepSeek') +
        K.rgrp('提供商', ['OpenAI 兼容', 'Anthropic', 'Gemini 原生'], 0) +
        K.tb('地址', 'https://api.deepseek.com/v1/chat/completions', { mono: true }) +
        K.tb('API 密钥', '••••••••••••••••••••', { icon: 'E890', help: '保存在系统凭据保险库。导出、备份与日志里都没有它。' }) +
        K.row({ ic: 'E99A', t: '默认模型', s: 'deepseek-chat · 48 个可选 · 3 天前拉取', go: 'model-picker' }) +
        K.cb('副模型', 'deepseek-chat', { go: 'model-picker', help: '记忆抽取、翻译、压缩用；留空 = 主模型' }) +
        K.rgrp('视觉', [{ t: '自动探测', s: '按模型元数据：deepseek-chat 不支持' }, '强制开', '强制关'], 0) +
        K.tb('最大上下文 tokens', '0', { help: '0 = 按模型元数据（64K）' }) +
        K.row({ ic: 'E943', t: '自定义请求头 / 请求体', s: '无', go: 'request-overrides' }) +
        K.btns([K.btn('测试连接', { icon: 'E895', go: 'api-test' })], 'left') +
        K.note('上次测试：正常 · 首字 420 ms · 10:02', 'ok')
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'api-profiles' }, { ic: 'E711', t: '取消', go: 'api-profiles' }], ['设为默认', '删除']);
    }
  });

  screen({
    id: 'api-test', t: '连接测试结果', src: '设计稿 §6.9「测试连接」· ModelCatalog 拉取',
    note: ['逐项：连接 → 鉴权 → 拉取模型列表 → 视觉探测', '失败项写 HTTP 状态与建议，只写主机名'],
    render: function () {
      return K.ph('模型档案 · Gemini', '连接测试') + K.body(
        K.row({ ic: 'E930', t: '连接', s: 'generativelanguage.googleapis.com · 186 ms', dense: true }) +
        K.row({ ic: 'E930', t: '鉴权', s: '密钥有效', dense: true }) +
        K.row({ ic: 'E930', t: '模型列表', s: '27 个 · 已合并元数据（窗口 / 视觉 / 推理）', dense: true }) +
        K.row({ ic: 'EA39', t: '<span class="t-err">试发一条消息</span>', s: 'HTTP 429 · RESOURCE_EXHAUSTED · retry-after 58s', dense: true }) + K.gap(8) +
        K.note('密钥与网络都正常，是额度或频率被限。等 1 分钟再试，或换一个模型档案。', 'warn')
      ) + '<div class="foot">' + K.btn('重新测试', { acc: true }) + K.btn('完成', { go: 'api-profiles' }) + '</div>';
    }
  });

  screen({
    id: 'model-picker', t: '模型选择器', src: 'UI-HANDOFF §1.2 / §9「逻辑最全、界面最缺」· ModelCatalogView.Groups',
    note: ['顶部搜索 + 能力筛选（视觉 / 推理 / 工具 / 收藏）', '「收藏」组永远在最上；其余按模型档案分组，组头可折叠，折叠状态持久化', '每行：模型 id（等宽）· 显示名与窗口 · 能力徽标 · 价位；右侧星标收藏', '当前选中 = 柔底 + 4px 强调色条'],
    render: function () {
      return K.ph('工程助理 · 模型', '选择模型') +
        '<div style="flex:none;padding:0 12px 6px">' + K.tb('', '', { bare: true, ph: '搜索模型', icon: 'E721' }) + '</div>' +
        K.flt(['全部', '视觉', '推理', '工具', '收藏'], 0) + K.body(
          grp('收藏', '· 3', true) +
          model('deepseek-chat', 'DeepSeek · DeepSeek V3 · 64K', [['工具'], ['$', 'line']], { sel: true, fav: true }) +
          model('claude-sonnet-4.5', 'Claude 中转 · 200K', [['视觉', 'acc'], ['推理'], ['工具'], ['$$$', 'line']], { fav: true }) +
          model('gemini-2.5-flash', 'Gemini · 1M', [['视觉', 'acc'], ['推理'], ['工具'], ['$', 'line']], { fav: true }) +
          grp('DeepSeek', '· 48 · 3 天前', true) +
          model('deepseek-reasoner', 'DeepSeek R1 · 64K', [['推理'], ['$$', 'line']]) +
          model('deepseek-coder', '128K · 内置表：deepseek-coder*', [['工具'], ['$', 'line']]) +
          grp('Claude 中转', '· 12', false) +
          grp('Gemini', '· 27', false) +
          grp('LM Studio', '· 未拉取', true) +
          K.row({ ic: 'E896', t: '拉取模型列表', s: '127.0.0.1:1234/v1/models', dense: true })
        ) + K.ab([{ ic: 'E710', t: '手动添加', go: 'model-manual' }, { ic: 'E72C', t: '刷新列表' }, { ic: 'E946', t: '详情', go: 'model-details' }]);
    }
  });

  screen({
    id: 'model-picker-search', t: '模型选择器 · 搜索', src: 'ModelCatalogBuilder', sip: true,
    note: ['跨分组过滤，保留组头便于区分来源', '无结果时提示「手动添加」'],
    render: function () {
      return K.ph('工程助理 · 模型', '选择模型') +
        '<div style="flex:none;padding:0 12px 6px">' + K.tb('', 'flash', { bare: true, focus: true, icon: 'E721' }) + '</div>' + K.body(
          grp('Gemini', '· 3 个匹配', true) +
          model('gemini-2.5-flash', '1M', [['视觉', 'acc'], ['推理'], ['工具']], { fav: true }) +
          model('gemini-2.0-flash-lite', '1M', [['视觉', 'acc'], ['工具']]) +
          K.row({ t: '找不到想要的？', s: '手动添加模型 id', go: 'model-manual', dense: true }));
    }
  });

  screen({
    id: 'model-manual', t: '手动添加模型', src: 'ModelMetadataTable.Resolve（打分式匹配）',
    note: ['输入 id 后即时显示按内置表猜到的元数据', '猜不到时写「未知」，可以手动覆盖'],
    render: function () {
      return K.ph('模型档案 · DeepSeek', '手动添加模型') + K.body(
        K.tb('模型 id', 'deepseek-v3.2-exp', { mono: true }) +
        K.sec('按内置表推测') + K.kv([['匹配', '内置表：deepseek-v3*'], ['上下文', '128K'], ['能力', '工具 · 推理'], ['价位', '$']]) +
        K.note('只是推测。服务端返回元数据时以服务端为准；也可以在详情里覆盖。')
      ) + K.ab([{ ic: 'E74E', t: '添加', go: 'model-picker' }, { ic: 'E711', t: '取消', go: 'model-picker' }]);
    }
  });

  screen({
    id: 'model-details', t: '模型元数据与覆盖', src: 'ModelMetadata：ContextWindow / Vision / Reasoning / Tools / PriceTier / Origin',
    note: ['写明来源：服务端 / 内置表 / 未知', '可以逐项覆盖，覆盖后标「已覆盖」'],
    render: function () {
      return K.ph('DeepSeek · 模型详情', 'deepseek-chat') + K.body(
        K.kv([['显示名', 'DeepSeek V3'], ['来源', '服务端 + 内置表：deepseek-chat'], ['价位', '$']]) +
        K.tb('上下文窗口', '65536', { mono: true, help: '服务端未提供，用内置表' }) +
        K.rgrp('视觉', ['按元数据（不支持）', '支持', '不支持'], 0) +
        K.rgrp('推理内容', ['按元数据（不返回）', '返回', '不返回'], 0) +
        K.rgrp('工具调用', ['按元数据（支持）', '支持', '不支持'], 0)
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'model-picker' }, { ic: 'E711', t: '取消', go: 'model-picker' }], ['清除覆盖']);
    }
  });

  screen({
    id: 'search-settings', t: '联网搜索', src: 'GlobalSettings.Search（provider / maxResults / depth / allowFetchPage / timeout）',
    note: ['搜索源单选；SearXNG 需要地址', '结果条数与正文字数用滑块', '抓取网页正文单独开关'],
    render: function () {
      return K.ph('我的', '联网搜索') + K.body(
        K.rgrp('搜索源', [{ t: 'DuckDuckGo', s: '不需要密钥' }, { t: 'Bing', s: '需要密钥' }, { t: 'Tavily', s: '需要密钥' }, { t: 'SearXNG', s: '自建实例' }], 0) +
        K.tb('SearXNG 地址', '', { ph: 'https://searx.example.com', dis: true }) +
        K.sl('每次返回结果', 20, '5 条', { min: '1', max: '20' }) +
        K.sl('每条正文字数', 40, '2000 字', { min: '500', max: '5000' }) +
        K.tog('允许抓取网页正文（fetch_page）', false, { s: '仍然不抓 Google 域名' }) +
        K.sl('超时', 30, '30 秒') +
        K.tb('User-Agent', '', { ph: '默认', mono: true })
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'me' }, { ic: 'E711', t: '取消', go: 'me' }]);
    }
  });

  /* ======================= 07 工具与扩展 ======================= */
  group('tools', '工具与扩展', '内置工具目录、run_python 运行时、MCP 服务器、技能、权限记录、授权目录。档案侧的勾选在「档案 → 工具 · MCP · 技能」。');

  screen({
    id: 'tool-catalog', t: '全部内置工具', src: '设计稿 §9.1 · BuiltinTools（20 个）+ ScriptTools',
    note: ['按 ToolGroup 分组，每个一句人话说明', '敏感工具带「需批准」；set_working_directory 在手机上已移除', 'run_python 显示运行时状态'],
    render: function () {
      function t(n, s, sens, go) { return K.row({ t: '<span class="mono" style="font-size:14px">' + n + '</span>' + (sens ? ' ' + K.bd('需批准', 'warn') : ''), s: s, dense: true, go: go || 'tool-detail' }); }
      return K.ph('我的', '内置工具') + K.body(
        K.sec('网络') + t('web_search', '联网搜索，结果交给模型') + t('fetch_page', '抓取网页正文并转成纯文本') +
        K.sec('文件') + t('list_files', '列出档案文件夹') + t('read_file', '读文本文件，超出上限截断') + t('write_file', '在档案文件夹里写文件', 1) + t('read_image', '读图并发给视觉模型', 1) +
        K.sec('设备') + t('open_app', '用协议或应用标识启动应用') + t('media_control', '暂停 / 继续 / 切歌 / 音量') + t('make_call', '打开拨号，仍需你按拨号', 1) + t('send_sms', '打开短信，仍需你按发送', 1) +
        K.sec('个人') + t('calendar_list', '查看接下来的日程') + t('calendar_create', '新建一条日程', 1) + t('contacts_search', '按姓名或号码搜联系人') +
        K.sec('代理') + t('get_time', '当前日期、时间与时区') + t('memory_search', '主动检索记忆池') + t('spawn_subagent', '开子代理，最多 5 层') + t('mcp_list', '列出可用的 MCP 工具') + t('mcp_call', '调用 MCP 工具') + t('skill_list', '列出技能') + t('skill_use', '启用一个技能') +
        t('run_python', 'PyUwp 3.15 · 可用', 1, 'script-runtime')
      );
    }
  });

  screen({
    id: 'tool-detail', t: '工具详情', src: 'ToolContracts · PermissionService 权限键',
    note: ['参数、是否敏感、权限键格式、哪些档案开了它', '「始终允许」记录直接链到权限页'],
    render: function () {
      return K.ph('内置工具 · 文件', 'write_file') + K.body(
        K.tx('在当前档案的文件夹里写入一个文本文件。路径限定在档案目录内（PathSandbox），不能写到外面。') +
        K.kv([['分组', '文件'], ['审批', '<span class="t-warn">每次需要批准</span>'], ['权限键', 'write_file:&lt;路径&gt;', 1]]) +
        K.sec('参数') + K.kv([['path', '相对路径，必填', 1], ['content', '文本内容，必填', 1], ['mode', 'create / overwrite / append', 1]]) +
        K.sec('在这些档案里开启') + K.row({ av: avOf(P.eng, 's'), t: '工程助理', dense: true, go: 'profile-tools' }) +
        K.row({ ic: 'E8D7', t: '「始终允许」记录', s: '1 条', go: 'permissions' })
      );
    }
  });

  screen({
    id: 'script-runtime', t: '脚本运行时（run_python）', src: 'UI-HANDOFF §4 双重门控 · §9.1 PyUwp · IScriptRuntime',
    note: ['写清运行时是什么、能用什么、不能用什么', '桌面上是桩运行时，不执行真代码', '「自检」跑一行 print，结果就地显示'],
    render: function () {
      return K.ph('内置工具 · 代理', 'run_python') + K.body(
        K.note('<b>PyUwp 3.15 · ARM · 可用</b><br>python-uwp.dll + 622 个标准库模块', 'ok', 'E930') +
        K.kv([['超时', '30 秒'], ['输出上限', '20000 字（超出截断）'], ['工作目录', '档案的 files/', 1]]) +
        K.sec('能用') + K.chips(['json', 're', 'math', 'datetime', 'collections', 'itertools', 'statistics'], { mono: true, pad: true }) +
        K.sec('还不能用') + K.chips(['_socket', '_ssl', '_hashlib', 'zipfile', '_sqlite3'], { mono: true, pad: true }) +
        K.note('两道门：档案里的脚本开关 + 每次执行都审批。两道都过了才会运行。', 'info', 'EA18') +
        K.btns([K.btn('运行自检', { icon: 'E768' })], 'left') +
        '<div class="pre">&gt;&gt;&gt; print(sum(range(10)))\n45\n退出码 0 · 38 ms</div>'
      );
    }
  });

  screen({
    id: 'mcp-list', t: 'MCP 服务器', src: '设计稿 §6.12 · McpServerConfig',
    note: ['副标题：传输 · 工具数 · 连接状态；失败用错误色条', '停用的服务器变灰'],
    render: function () {
      return K.ph('我的', 'MCP 服务器') + K.body(
        K.row({ ic: 'E71B', t: '文档检索', s: 'http · 6 个工具 · 已连接', bar: 'n', go: 'mcp-edit' }) +
        K.row({ ic: 'E71B', t: '公司知识库', s: 'http · HTTP 401 未授权', bar: 'err', go: 'mcp-edit' }) +
        K.row({ ic: 'E71B', t: '本地笔记', s: 'http · 已停用', dis: true, go: 'mcp-edit' }) + K.gap(8) +
        K.note('目前只支持 http 传输；stdio 与 SSE 以后再加。')
      ) + K.ab([{ ic: 'E710', t: '添加', go: 'mcp-new' }, { ic: 'E895', t: '全部测试' }]);
    }
  });

  screen({
    id: 'mcp-edit', t: '编辑 MCP 服务器', src: '设计稿 §6.12',
    note: ['鉴权头是键值对，值按密钥处理（不进日志）', '测试连接后显示工具清单入口'],
    render: function () {
      return K.ph('MCP 服务器', '公司知识库') + K.body(
        K.tb('名称', '公司知识库') +
        K.rgrp('传输', ['http', { t: 'stdio', s: '手机上不支持', dis: true }], 0) +
        K.tb('地址', 'https://kb.example.com/mcp', { mono: true }) +
        '<div class="fld"><div class="fld-hd">鉴权头</div><div style="display:flex;gap:6px"><div style="flex:1">' + K.tb('', 'Authorization', { bare: true, mono: true }) + '</div><div style="flex:1.3">' + K.tb('', '••••••••', { bare: true, mono: true }) + '</div></div></div>' +
        K.tog('启用', true) +
        K.btns([K.btn('测试连接', { icon: 'E895' })], 'left') +
        K.note('HTTP 401 · 未授权。检查鉴权头里的令牌是否过期。', 'err') +
        K.row({ ic: 'E8FD', t: '工具清单', s: '连接成功后可查看', dis: true })
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'mcp-list' }, { ic: 'E711', t: '取消', go: 'mcp-list' }], ['删除']);
    }
  });

  screen({
    id: 'mcp-new', t: '新增 MCP 服务器', src: '设计稿 §6.12',
    note: ['空表单；保存前先测一次'],
    render: function () {
      return K.ph('MCP 服务器', '新增') + K.body(
        K.tb('名称', '', { ph: '例如：文档检索' }) + K.rgrp('传输', ['http', { t: 'stdio', s: '手机上不支持', dis: true }], 0) +
        K.tb('地址', '', { ph: 'https://…/mcp', mono: true }) + K.btns([K.btn('添加鉴权头', { icon: 'E710', sm: true })], 'left') + K.tog('启用', true)
      ) + K.ab([{ ic: 'E74E', t: '保存', dis: true }, { ic: 'E711', t: '取消', go: 'mcp-list' }]);
    }
  });

  screen({
    id: 'mcp-detail', t: 'MCP 工具清单', src: 'McpServerConfig.tools',
    note: ['逐个勾选要暴露给模型的工具', '每个工具显示参数摘要'],
    render: function () {
      return K.ph('MCP · 文档检索', '工具清单') + K.body(
        K.tx('已连接 · 6 个工具 · 10:05 拉取', true) +
        K.chk('<span class="mono">search_docs</span>', true, { s: 'query, top_k' }) +
        K.chk('<span class="mono">get_doc</span>', true, { s: 'id' }) +
        K.chk('<span class="mono">list_spaces</span>', true, { s: '无参数' }) +
        K.chk('<span class="mono">create_doc</span>', false, { s: 'space, title, body · 会写数据' }) +
        K.chk('<span class="mono">update_doc</span>', false, { s: 'id, body · 会写数据' }) +
        K.chk('<span class="mono">delete_doc</span>', false, { s: 'id · 会删数据' })
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'mcp-list' }, { ic: 'E895', t: '重新拉取' }]);
    }
  });

  screen({
    id: 'skills', t: '技能', src: '设计稿 §6.12 · SkillDefinition',
    note: ['从文件导入 / 导出；停用的变灰'],
    render: function () {
      return K.ph('我的', '技能') + K.body(
        K.row({ ic: 'E82F', t: '代码审查清单', s: '审查 diff 时先查空引用、再查并发', go: 'skill-edit' }) +
        K.row({ ic: 'E82F', t: '周报模板', s: '做了什么 / 结果 / 下周', go: 'skill-edit' }) +
        K.row({ ic: 'E82F', t: '翻译术语表', s: '专有名词保持英文', go: 'skill-edit' }) +
        K.row({ ic: 'E82F', t: '写 commit 信息', s: '已停用', dis: true, go: 'skill-edit' })
      ) + K.ab([{ ic: 'E710', t: '新建', go: 'skill-new' }, { ic: 'E8E5', t: '从文件导入' }], ['导出全部']);
    }
  });

  screen({
    id: 'skill-edit', t: '编辑技能', src: 'SkillDefinition：name / description / content / mode / enabled',
    note: ['说明会出现在 skill_list 的结果里，写给模型看', '内容模板支持占位符'],
    render: function () {
      return K.ph('技能', '代码审查清单') + K.body(
        K.tb('名称', '代码审查清单') +
        K.tb('说明', '审查 diff 时先查空引用、再查并发与资源释放。', { help: '模型靠这句话决定要不要用它' }) +
        K.tb('内容模板', '审查以下改动：\n1. 空引用与边界\n2. 并发与锁\n3. 资源释放\n4. 是否违反 C# 7.3 / 14393 约束\n只列有把握的问题。', { tall: true, mono: true }) +
        K.cb('注入方式', '作为本次回答的额外要求（prefix）') +
        K.tog('启用', true)
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'skills' }, { ic: 'E711', t: '取消', go: 'skills' }], ['导出为文件', '删除']);
    }
  });

  screen({
    id: 'skill-new', t: '新建技能', src: 'SkillDefinition',
    note: ['空表单'],
    render: function () {
      return K.ph('技能', '新建') + K.body(K.tb('名称', '', { ph: '必填' }) + K.tb('说明', '', { ph: '一句话，写给模型看' }) + K.tb('内容模板', '', { ph: '技能被启用时追加的要求', tall: true }) + K.tog('启用', true)) +
        K.ab([{ ic: 'E74E', t: '保存', dis: true }, { ic: 'E711', t: '取消', go: 'skills' }]);
    }
  });

  screen({
    id: 'permissions', t: '权限记录', src: '设计稿 §9.2「批准记录可查看与清空」· PermissionFile.granted',
    note: ['只列「始终允许」；每条 = 工具:关键参数', '完全信任不会出现在这里（不持久化）'],
    render: function () {
      return K.ph('我的', '权限记录') + K.body(
        K.tx('这些操作不再每次询问。撤销后下次会重新问你。', true) +
        K.row({ ic: 'E90F', t: '<span class="mono" style="font-size:14px">write_file:notes/weekly-41.md</span>', s: '工程助理 · 10月12日', go: 'permission-detail' }) +
        K.row({ ic: 'E90F', t: '<span class="mono" style="font-size:14px">calendar_create</span>', s: '工程助理 · 10月9日', go: 'permission-detail' }) +
        K.row({ ic: 'E90F', t: '<span class="mono" style="font-size:14px">send_sms:+86 138 0000 0000</span>', s: '工程助理 · 10月2日', go: 'permission-detail' }) + K.gap(8) +
        K.note('「完全信任」只在单个会话里临时生效，离开会话即复位，所以不会出现在这里。', 'info', 'EA18')
      ) + K.ab([{ ic: 'E74D', t: '全部清空' }]);
    }
  });

  screen({
    id: 'permission-detail', t: '授权详情与撤销', src: 'PermissionService',
    note: ['写明授权范围与来源会话'],
    render: function () {
      return K.ph('权限记录', '写入文件') + K.body(
        K.kv([['权限键', 'write_file:notes/weekly-41.md', 1], ['范围', '只这一个路径'], ['授权于', '10月12日 10:44 · 「整理周报并写入 notes」'], ['档案', '工程助理']]) +
        K.btns([K.btn('撤销这条授权', { icon: 'E74D' })], 'left'));
    }
  });

  screen({
    id: 'authorized-folders', t: '授权目录', src: '设计稿 §6.12「添加授权目录（FutureAccessList）」',
    note: ['set_working_directory 在手机上移除，外部目录统一在这里授权', '每个目录写明只读 / 读写与可用档案'],
    render: function () {
      return K.ph('我的 · 工作区', '授权目录') + K.body(
        K.tx('档案文件夹本身就是工作目录。要让工具读写档案外的文件夹，先在这里授权。', true) +
        K.row({ ic: 'E8B7', t: 'SD 卡\\Documents\\notes', s: '读写 · 工程助理', go: 'folder-permission' }) +
        K.row({ ic: 'E8B7', t: '文档\\言枢导出', s: '只读 · 全部档案', go: 'folder-permission' })
      ) + K.ab([{ ic: 'E710', t: '添加目录', go: 'folder-permission' }]);
    }
  });

  screen({
    id: 'folder-permission', t: '目录授权设置', src: 'FutureAccessList',
    note: ['选只读 / 读写，选哪些档案可用；移除授权会从系统列表里删掉'],
    render: function () {
      return K.ph('授权目录', 'notes') + K.body(
        K.kv([['位置', 'SD 卡\\Documents\\notes', 1]]) +
        K.rgrp('权限', [{ t: '只读', s: 'list_files / read_file' }, { t: '读写', s: '另外允许 write_file（仍需每次批准）' }], 1) +
        K.sec('可用档案') + K.chk('工程助理', true) + K.chk('林', false) + K.chk('翻译官', false) + K.chk('本地研究员', false) +
        K.btns([K.btn('移除授权', { icon: 'E74D' })], 'left')
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'authorized-folders' }, { ic: 'E711', t: '取消', go: 'authorized-folders' }]);
    }
  });
})();
