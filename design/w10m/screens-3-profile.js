/* 04 档案与人设 · 05 角色卡导入 */
(function () {
  'use strict';
  var ic = K.ic;
  var SAVE = function (back) { return K.ab([{ ic: 'E74E', t: '保存', go: back || 'profile' }, { ic: 'E711', t: '取消', go: back || 'profile' }]); };
  window.SAVE = SAVE;

  function profileRender() {
    return K.body(
      '<div class="hero">' + avOf(P.eng, 'l') + '<div class="c"><b>工程助理</b><small>engineering · 创建于 10月1日</small>' + K.bds([K.bd('工程', 'line'), K.bd('工具', 'line')]) + '</div></div>' +
      K.btns([K.btn('新建会话', { acc: true, icon: 'E710', go: 'new-chat' }), K.btn('导出', { icon: 'E72D', go: 'data-export' })]) +
      K.sec('设置') +
      K.row({ ic: 'E77B', t: '身份', s: '名称、头像、标签、用户人设', go: 'profile-identity' }) +
      K.row({ ic: 'E70F', t: '人设', s: '系统提示词 1.2k 字 · 未设首句', go: 'profile-persona' }) +
      K.row({ ic: 'E99A', t: '模型与生成', s: 'DeepSeek · deepseek-chat · 参数用模型默认', go: 'profile-model' }) +
      K.row({ ic: 'E90F', t: '工具 · MCP · 技能', s: '9 个工具 · 1 个 MCP · 脚本关', go: 'profile-tools' }) +
      K.row({ ic: 'E8F1', t: '记忆', s: '42 条 · 私有池 · 向量检索', go: 'profile-memory' }) +
      K.row({ ic: 'E82D', t: '世界书 · 注入', s: '1 本世界书 · 2 条模式注入', go: 'profile-prompts' }) +
      K.row({ ic: 'E771', t: '外观', s: '无背景 · 气泡跟随强调色', go: 'profile-appearance' }) +
      K.row({ ic: 'E8A5', t: '查看最终提示词', s: '按装配顺序预览 · 约 3.4k tokens', go: 'prompt-assembly' }) +
      K.sec('数据') +
      K.row({ ic: 'E8F2', t: '会话', s: '18 个 · 2 个置顶', go: 'conversations' }) +
      K.row({ ic: 'E8B7', t: '文件', s: '12 个 · 3.1 MB', go: 'files' }) +
      K.row({ ic: 'E8F1', t: '管理记忆', s: '列表、编辑、整理、重建索引', go: 'memory' })
    ) + K.ab([{ ic: 'E710', t: '新建会话', go: 'new-chat' }, { ic: 'E72D', t: '导出', go: 'data-export' }], [{ t: '复制为新档案' }, { t: '删除档案', go: 'profile-delete' }]);
  }

  /* ======================= 04 档案与人设 ======================= */
  group('profile', '档案与人设', '档案详情从弹窗改成独立页，分节编辑：身份 / 人设 / 模型 / 工具 / 记忆 / 世界书与注入 / 外观。表单页底部是 CommandBar「保存 / 取消」；键盘弹出时「保存」提到标题行。');

  screen({
    id: 'profile', t: '档案详情', src: 'UI-HANDOFF §1.2「档案详情页（现在是弹窗）」· 设计稿 §4.1',
    note: ['头部：72 头像 + 名称 + slug + 标签', '分节入口，副标题写当前值', '「查看最终提示词」用来排查人设拼装'],
    render: function () { return K.ph('档案', '工程助理') + profileRender(); }
  });

  screen({
    id: 'profile-new', t: '新建档案', src: '设计稿 §6.3',
    note: ['先选起点，再填名称；其余字段建好后在详情页分节改', 'slug 自动生成、可改，冲突时追加 -2'],
    render: function () {
      return K.ph('档案', '新建档案') + K.body(
        K.rgrp('从哪里开始', [{ t: '空白档案' }, { t: '通用助手', s: '不开工具' }, { t: '工程助理', s: '开启搜索、文件、子代理' }, { t: '从角色卡导入', s: 'PNG / JSON，转到导入向导' }], 2) +
        K.tb('名称', '代码审查员') + K.tb('文件夹名（slug）', 'code-reviewer', { mono: true, help: '创建后不改。重名时自动追加 -2。' })
      ) + SAVE('profile');
    }
  });

  screen({
    id: 'profile-identity', t: '编辑身份', src: '设计稿 §6.3 第 1 节 · §5.2 强调色策略',
    note: ['名称 / 头像 / 标签 / 用户人设', '强调色：跟随系统，或跟随角色卡（从卡图取主色）'],
    render: function () {
      return K.ph('工程助理 · 身份', '身份') + K.body(
        K.row({ av: avOf(P.eng, 'l'), t: '头像', s: '点击更换：拍照 / 相册 / 从卡图 / 首字', go: 'avatar' }) +
        K.tb('名称', '工程助理') + K.tb('文件夹名', 'engineering', { mono: true, dis: true, help: '创建后不能改，避免路径失效。' }) +
        '<div class="fld"><div class="fld-hd">标签</div>' + K.chips(['工程', '工具'], { x: true, add: '添加' }) + '</div>' +
        K.cb('用户人设', '欧阳（默认）', { go: 'user-personas', help: '这个档案里 {{user}} 指谁' }) +
        K.rgrp('强调色', [{ t: '跟随系统' }, { t: '跟随角色卡', s: '从 card.png 提取主色，只在这个档案里生效' }], 0)
      ) + SAVE();
    }
  });

  screen({
    id: 'avatar', t: '选择头像', src: '设计稿 §6.3 身份 · ImageCompressor',
    note: ['圆形裁剪预览；来源四选一', '自动降采样到 ≤1024px 存为 avatar.png'],
    render: function () {
      return K.ph('工程助理 · 身份', '头像') + K.body(
        '<div style="display:flex;justify-content:center;padding:12px 0 18px"><div style="width:200px;height:200px;position:relative;background:linear-gradient(160deg,#16313b,#2f6f73 55%,#c7d9b7)"><div style="position:absolute;inset:14px;border-radius:50%;box-shadow:0 0 0 200px rgba(0,0,0,.55);border:2px solid #fff"></div></div></div>' +
        K.row({ ic: 'E722', t: '拍照' }) + K.row({ ic: 'E8B9', t: '从相册选' }) + K.row({ ic: 'E8A5', t: '从卡图', s: 'card.png（若这个档案来自角色卡）', dis: true }) + K.row({ ic: 'E8D2', t: '用首字', s: '「工」+ 强调色柔底' }) +
        K.tx('会降采样到 1024px 以内保存为 avatar.png。', true)
      ) + K.ab([{ ic: 'E8FB', t: '完成', go: 'profile-identity' }, { ic: 'E711', t: '取消', go: 'profile-identity' }]);
    }
  });

  screen({
    id: 'profile-persona', t: '编辑人设', src: '设计稿 §6.3 第 2 节 · §7.1 · Placeholders.All',
    note: ['系统提示词下方是「插入变量」芯片行，点一下插到光标处', '系统提示词留空时按模板用描述 / 性格 / 场景拼装', '字段与 SillyTavern v2/v3 一一对应'],
    render: function () {
      return K.ph('工程助理 · 人设', '人设') + K.body(
        K.tb('系统提示词', '你是 {{char}}，{{user}} 的工程助理。先给结论，再给理由；改代码前先说明影响范围。', { multi: true, small: '留空则按下方字段拼装' }) +
        '<div class="fld" style="margin-top:-6px">' + K.chips(['{{user}}', '{{char}}', '{{cur_date}}', '{{cur_time}}', '{{model_name}}', '{{device_info}}', '{{battery_level}}'], { mono: true }) + '</div>' +
        K.tb('描述', '熟悉 UWP、.NET Native 与 Windows 10 Mobile 的限制。', { multi: true }) +
        K.tb('性格', '', { ph: '例如：直接、谨慎、不客套' }) +
        K.tb('场景', '', { ph: '对话发生的背景' }) +
        K.tb('首句', '', { ph: '新会话的第一条助手消息' }) +
        K.tb('示例对话', '', { ph: '<START>\n{{user}}: …\n{{char}}: …', multi: true, mono: true }) +
        K.tb('后置指令', '回答控制在 300 字以内，除非我要求展开。', { multi: true, help: '放在对话历史之后（post_history_instructions）' }) +
        K.row({ ic: 'E8BD', t: '预设消息', s: '2 条 · 固定放在对话开头' }) +
        K.row({ ic: 'E8A5', t: '消息模板', s: '{{message}}（原样发送）' })
      ) + SAVE();
    }
  });

  screen({
    id: 'profile-persona-keyboard', t: '编辑人设 · 键盘弹出', src: '设计稿 §6.5 软键盘对策 · UI-HANDOFF §5', sip: true,
    note: ['键盘弹出时底部 CommandBar 被挡：「保存」提到标题行', '变量芯片紧贴输入框下方，仍然可点'],
    render: function () {
      return K.ph('工程助理 · 人设', '系统提示词', K.hb('保存', { acc: true, go: 'profile' })) + K.body(
        K.tb('', '你是 {{char}}，{{user}} 的工程助理。先给结论，再给理由；改代码前先说明影响范围。今天是 {{cur_date}}', { focus: true, multi: true }) +
        '<div class="fld" style="margin-top:-6px">' + K.chips(['{{user}}', '{{char}}', '{{cur_date}}', '{{cur_time}}', '{{model_name}}'], { mono: true }) + '</div>');
    }
  });

  screen({
    id: 'profile-unsaved', t: '未保存改动', src: '设计稿 §6.3「有未保存改动时按返回键先确认」',
    note: ['只在真的改过时弹；写明改了几处', '保存 / 不保存；按返回键 = 留在页面'],
    render: function () {
      return K.ph('工程助理 · 人设', '人设') + K.body(K.tb('系统提示词', '你是 {{char}}，{{user}} 的工程助理。', { multi: true })) + SAVE() +
        K.dlg({ title: '保存对人设的改动？', body: K.tx('改了 2 处：系统提示词、后置指令。'), btns: [{ t: '保存', acc: true, go: 'profile' }, { t: '不保存', go: 'profile' }] });
    }
  });

  screen({
    id: 'profile-model', t: '模型与生成参数', src: '设计稿 §6.3 第 3 节 · ProfileApiOptions / GenerationOptions',
    note: ['滑块旁「用模型默认」勾选时滑块变灰，值为 null', '模型行进模型选择器；推理等级用单选，不用下拉', '自定义请求字段单独一页'],
    render: function () {
      return K.ph('工程助理 · 模型', '模型与生成') + K.body(
        K.cb('模型档案', 'DeepSeek（默认）', { go: 'api-profiles' }) +
        K.row({ ic: 'E99A', t: 'deepseek-chat', s: '64K · 工具 · $ · 点击更换', go: 'model-picker' }) +
        K.sec('参数') +
        K.sl('temperature', 35, '0.70', { min: '0', max: '2', def: false }) +
        K.sl('top_p', 100, '默认', { dis: true, def: true }) +
        K.tb('最大输出 tokens', '', { ph: '模型默认' }) +
        K.rgrp('推理强度', ['自动', '低', '中', '高'], 0) +
        K.tb('上下文消息条数', '0', { help: '0 = 不限，按 token 窗口自动截断' }) +
        K.tb('最大上下文 tokens', '0', { help: '0 = 按模型元数据（64K）' }) +
        K.rgrp('视觉', ['跟随模型档案', '开', '关'], 0) +
        K.cb('副模型', '与主模型相同', { go: 'model-picker', help: '用于记忆抽取、翻译、压缩' }) +
        K.tog('流式输出', true, { s: '开启工具时自动走工具循环，按步更新' }) +
        K.row({ ic: 'E943', t: '自定义请求字段', s: '1 个请求头 · 0 个请求体字段', go: 'request-overrides' })
      ) + SAVE();
    }
  });

  screen({
    id: 'request-overrides', t: '自定义请求字段', src: '设计稿 §3.4 customHeaders / customBody',
    note: ['键值对列表，每行可删', '密钥不写在这里，写在模型档案的密钥字段'],
    render: function () {
      function kvRow(k, v) { return '<div style="display:flex;gap:6px;padding:0 12px 8px;align-items:center">' + '<div style="flex:1">' + K.tb('', k, { bare: true, mono: true }) + '</div><div style="flex:1.3">' + K.tb('', v, { bare: true, mono: true }) + '</div>' + K.ibtn('E711') + '</div>'; }
      return K.ph('工程助理 · 模型', '自定义请求字段') + K.body(
        K.sec('请求头') + kvRow('HTTP-Referer', 'yanshuai.app') + K.btns([K.btn('添加请求头', { icon: 'E710', sm: true })], 'left') +
        K.sec('请求体') + kvRow('', '') + K.btns([K.btn('添加字段', { icon: 'E710', sm: true })], 'left') +
        K.note('值按 JSON 解析：<span class="mono">true</span>、<span class="mono">0.5</span>、<span class="mono">{"a":1}</span> 都可以。') +
        K.note('不要把密钥写在这里：这里的内容会进配置文件。', 'warn')
      ) + SAVE('profile-model');
    }
  });

  screen({
    id: 'profile-tools', t: '工具 · MCP · 技能', src: '设计稿 §6.3 第 4 节 · §9.1 · BuiltinTools ToolGroup',
    note: ['分组：网络 / 文件 / 设备 / 个人 / 代理；敏感工具带「需批准」', '脚本开关是第一道门，旁边写运行时是否可用', '完全信任不在这里：只能在会话设置里临时开，离开会话即复位'],
    render: function () {
      function t(name, s, on, sens) { return K.chk('<span class="mono">' + name + '</span>' + (sens ? ' ' + K.bd('需批准', 'warn') : ''), on, { s: s }); }
      return K.ph('工程助理 · 工具', '工具 · MCP · 技能') + K.body(
        K.tog('使用工具', true, { s: '关闭后走真流式，不调用任何工具' }) +
        K.sec('网络') + t('web_search', '联网搜索', true) + t('fetch_page', '抓取网页正文', true) +
        K.sec('文件') + t('list_files', '列出档案文件夹', true) + t('read_file', '读文本文件（8000 字截断）', true) + t('write_file', '写入档案文件夹', true, 1) + t('read_image', '读图并发给视觉模型', false, 1) +
        K.sec('设备') + t('open_app', '通过协议启动应用', false) + t('media_control', '暂停 / 切歌 / 音量', false) + t('make_call', '打开拨号', false, 1) + t('send_sms', '打开短信', false, 1) +
        K.sec('个人') + t('calendar_list', '查看日程', false) + t('calendar_create', '新建日程', false, 1) + t('contacts_search', '搜联系人', false) +
        K.sec('代理') + t('get_time', '当前时间与时区', true) + t('memory_search', '主动检索记忆', true) + t('spawn_subagent', '子代理（最多 5 层）', true) +
        K.row({ ic: 'E71B', t: 'MCP 服务器', s: '已选 1 / 2 · mcp_list / mcp_call', go: 'mcp-list' }) +
        K.row({ ic: 'E82F', t: '技能', s: '已选 2 / 4 · skill_list / skill_use', go: 'skills' }) +
        K.tog('允许运行 Python 脚本（run_python）', false, { s: '运行时：PyUwp 3.15 可用（无网络 / 无 zipfile）。每次执行仍需批准。' }) +
        K.sl('最多工具轮次', 30, '15', { min: '1', max: '50' }) +
        K.note('「完全信任」只能在会话设置里临时打开，离开会话自动复位，不会保存。', 'info', 'EA18')
      ) + SAVE();
    }
  });

  screen({
    id: 'profile-memory', t: '记忆设置', src: '设计稿 §6.3 第 5 节 · §8 · MemoryOptions · UI-HANDOFF §9 向量检索可见性',
    note: ['写明当前是向量还是关键词检索（嵌入失败会降级到 BM25）', 'topK / 阈值真正生效', '入口：管理记忆、索引状态'],
    render: function () {
      return K.ph('工程助理 · 记忆', '记忆') + K.body(
        K.note('当前：<b>向量检索</b> · text-embedding-3-small · 1536 维 · 42 / 42 条已建索引', 'ok', 'E930') +
        K.tog('启用记忆', true) +
        K.tog('同时使用全局记忆池', false, { s: '全局池存放用户身份类事实，所有开启此项的档案共享' }) +
        K.tog('参考最近会话', false, { s: '把最近几次会话的摘要放进提示词' }) +
        K.tog('时间提醒', false, { s: '告诉模型现在几点、距上次聊天多久' }) +
        K.tog('自动抽取记忆', true) +
        K.sl('每隔几轮抽取一次', 20, '10 轮', { min: '2', max: '50' }) +
        K.sl('记忆上限', 10, '100 条', { min: '20', max: '1000', help: '满了按重要性 × 新近度淘汰到 80%，先给预览' }) +
        K.sec('检索') + K.tog('检索增强（RAG）', true) +
        K.sl('每次取几条（topK）', 25, '5') + K.sl('最低相关度', 30, '0.30') +
        K.row({ ic: 'E8F1', t: '管理记忆', s: '42 条', go: 'memory' }) + K.row({ ic: 'E895', t: '索引状态', s: '向量 · 正常', go: 'memory-index' })
      ) + SAVE();
    }
  });

  screen({
    id: 'profile-prompts', t: '世界书 · 注入（档案侧勾选）', src: '设计稿 §6.11 档案侧 · PromptOptions',
    note: ['世界书与注入定义在共享库里，档案只存「启用了哪些」', '可允许会话覆盖'],
    render: function () {
      return K.ph('工程助理 · 提示词', '世界书 · 注入') + K.body(
        K.sec('世界书', { right: K.lnk('管理', 'lorebooks') }) +
        K.chk('工程术语', true, { s: '13 个条目 · 2 个常驻' }) + K.chk('雨夜车站设定', false, { s: '18 个条目 · 来自角色卡「林」' }) +
        K.sec('模式注入', { right: K.lnk('管理', 'injections') }) +
        K.chk('先给结论', true, { s: '系统提示后 · 优先级 100' }) + K.chk('中文回答', true, { s: '对话底 · 优先级 90' }) + K.chk('简洁模式', false, { s: '指定深度 4' }) + K.chk('安全提醒', false, { s: '系统提示前' }) +
        K.tog('允许会话覆盖', false, { s: '开启后，会话设置里可以单独改系统提示词' })
      ) + SAVE();
    }
  });

  screen({
    id: 'profile-appearance', t: '档案外观', src: '设计稿 §3.4 外观六字段 · §6.3 第 6 节',
    note: ['顶部实时预览', '色板换行网格（不再 8 个一排贴边）；第一格 = 跟随强调色', '引号 / 括号着色是招牌细节'],
    render: function () {
      var sw = ['none', '#0078D7', '#E81123', '#10893E', '#8764B8', '#CA5010', '#00B7C3', '#5D5A58'];
      return K.ph('工程助理 · 外观', '外观') + K.body(
        '<div class="preview-bub" style="position:relative;overflow:hidden;padding:0"><div class="bgimg"></div><div class="bgdim" style="opacity:.6"></div><div class="onbg" style="padding:10px">' +
        T.user('预览一下') + T.out('<p><span class="br">（点头）</span><span class="q">「看起来不错。」</span>正文颜色不变。</p>') + '</div></div>' +
        K.rgrp('背景', ['无', '纯色', '图片'], 2) +
        K.row({ lead: T.thumb('c', 40), t: 'card.png', s: '角色卡立绘 · 点击更换', dense: true }) +
        K.sl('背景暗度', 60, '60%') +
        '<div class="fld-hd" style="padding:0 12px">用户气泡</div>' + K.swg(sw, 0, { s8: true }) +
        '<div class="fld-hd" style="padding:0 12px">引号内文字「…」</div>' + K.swg(['none', '#DCA03C', '#FFB900', '#FF8C00', '#E3008C', '#00B7C3', '#8E8CD8', '#BDBDBD'], 1, { s8: true }) +
        '<div class="fld-hd" style="padding:0 12px">括号内文字（…）</div>' + K.swg(['none', '#969696', '#7A7574', '#68768A', '#8764B8', '#2D7D9A', '#BDBDBD', '#666666'], 1, { s8: true })
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'profile' }, { ic: 'E711', t: '取消', go: 'profile' }], ['恢复默认']);
    }
  });

  screen({
    id: 'prompt-assembly', t: '查看最终提示词', src: '设计稿 §7 提示词装配管线 · PromptAssembler',
    note: ['只读：按 15 个装配位置列出来源与 token', '未启用的位置变灰，不隐藏', '点一行展开原文'],
    render: function () {
      function r(n, t, s, tok, dis) { return K.row({ lead: '<span class="t-mid mono" style="width:24px;text-align:center;font-size:13px">' + n + '</span>', t: t, s: s, rt: tok, dense: true, dis: dis }); }
      return K.ph('工程助理 · 只读预览', '最终提示词') + K.body(
        '<div class="ctxnum" style="padding-top:2px"><span>合计约 3.4k tokens · 模型窗口 64K</span><span>' + K.lnk('复制全文') + '</span></div>' +
        r(1, '模式注入 · 系统提示前', '未启用', '—', 1) +
        r(2, '档案人设', '系统提示词 + 后置指令', '412') +
        '<div class="pre" style="margin-top:-4px">你是 工程助理，欧阳 的工程助理。先给结论，再给理由；改代码前先说明影响范围。</div>' +
        r(3, '用户人设', '欧阳', '64') +
        r(4, '知识库文件', 'files/ 下 2 个注入文件', '1.1k') +
        r(5, '世界书命中', '工程术语 · 命中 3 条 · 常驻 2', '386') +
        r(6, '长期记忆', '向量检索 · 5 条', '298') +
        r(7, '最近会话摘要', '未开启', '—', 1) +
        r(8, '压缩摘要', '本会话无', '—', 1) +
        r(9, '工具规则', '9 个工具', '820') +
        r(10, '时间与环境', '未开启', '—', 1) +
        r(11, '模式注入 · 系统提示后', '先给结论', '48') +
        r(12, '对话顶注入', '—', '—', 1) +
        r(13, '预设消息', '2 条', '210') +
        r(14, '对话底注入', '中文回答', '22') +
        r(15, '指定深度注入', '—', '—', 1)
      );
    }
  });

  screen({
    id: 'profile-delete', t: '删除档案', src: '设计稿 §3.3「删除 = 移入 _trash」',
    note: ['写明影响范围；进回收区，不直接删除'],
    render: function () {
      return K.ph('档案', '本地研究员') + K.body(K.row({ ic: 'E8F2', t: '会话', s: '2 个' }) + K.row({ ic: 'E8B7', t: '文件', s: '6 个' })) +
        K.dlg({ title: '删除档案「本地研究员」？', body: K.tx('整个文件夹（2 个会话、6 个文件、记忆池）会移进回收区，30 天内可以恢复。'), btns: [{ t: '删除', acc: true, go: 'trash' }, '取消'] });
    }
  });

  screen({
    id: 'conversations', t: '档案的会话', src: '设计稿 §6.7 · §4.1「该档案的会话与文件」',
    note: ['置顶 / 今天 / 更早分组；与对话页同一套行'],
    render: function () {
      return K.ph('工程助理', '会话') + K.body(
        K.sec('置顶') + conv({ t: 'MSBuild 与部署笔记', time: '10月3日', prev: '新增 XAML 页面必须往 csproj 加 <Page Include>', state: 'done', p: '工程助理', tok: '5.0k', pin: true }) +
        K.sec('今天') + conv(CONV.warn) + conv(CONV.run) +
        K.sec('更早') + conv(CONV.week) + conv(CONV.long)
      ) + K.ab([{ ic: 'E710', t: '新建会话', go: 'new-chat' }, { ic: 'E762', t: '选择', go: 'home-select' }], [{ t: '已归档会话', go: 'archived' }]);
    }
  });

  screen({
    id: 'user-personas', t: '用户人设', src: '设计稿 §4.1 · UserPersonaFile.activeId',
    note: ['多套，选中一套作为默认；档案可以单独指定'],
    render: function () {
      return K.ph('我的', '用户人设') + K.body(
        K.row({ av: K.av('欧', { n: true }), t: '欧阳', s: '默认 · 后端工程师，说话直接', sel: true, go: 'user-persona-edit' }) +
        K.row({ av: K.av('旅', { n: true }), t: '旅人', s: '角色扮演用 · 林 在用', go: 'user-persona-edit' })
      ) + K.ab([{ ic: 'E710', t: '新建', go: 'user-persona-new' }]);
    }
  });

  screen({
    id: 'user-persona-edit', t: '编辑用户人设', src: 'UserPersona：name / description / avatar',
    note: ['{{user}} 展开为这里的名字'],
    render: function () {
      return K.ph('用户人设', '欧阳') + K.body(
        K.row({ av: K.av('欧', { n: true, size: 'l' }), t: '头像', s: '点击更换', go: 'avatar' }) +
        K.tb('名字', '欧阳', { help: '提示词里的 {{user}} 会换成这个名字' }) +
        K.tb('描述', '后端工程师，熟悉 C#，说话直接，不喜欢客套。', { multi: true }) +
        K.tog('设为默认', true)
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'user-personas' }, { ic: 'E711', t: '取消', go: 'user-personas' }], ['删除']);
    }
  });

  screen({
    id: 'user-persona-new', t: '新建用户人设', src: 'UserPersona',
    note: ['空表单；名字必填'],
    render: function () {
      return K.ph('用户人设', '新建') + K.body(
        K.row({ av: K.av('?', { n: true, size: 'l' }), t: '头像', s: '可选' }) +
        K.tb('名字', '', { ph: '必填', focus: false }) + K.tb('描述', '', { ph: '你是谁、希望被怎么称呼', multi: true }) + K.tog('设为默认', false)
      ) + K.ab([{ ic: 'E74E', t: '保存', dis: true }, { ic: 'E711', t: '取消', go: 'user-personas' }]);
    }
  });

  /* ======================= 05 角色卡导入 ======================= */
  group('import', '角色卡导入', '三步向导：选来源 → 预览与补齐 → 落盘选项。顶部 4px 分段进度。每一种解析失败都有明确文案，不静默成功。');

  screen({
    id: 'import-source', t: '导入 1 / 3 · 选来源', src: '设计稿 §6.4 第 1 步 · CardImporter',
    note: ['PNG（tEXt / zTXt）与 JSON（v2 / v3）', '从链接是下一期；内置卡库更晚，先变灰'],
    render: function () {
      return K.steps(3, 0) + K.ph('导入角色卡 · 1 / 3', '选来源') + K.body(
        K.row({ ic: 'E8B9', t: 'PNG 角色卡', s: '内嵌 chara（tEXt / zTXt）的卡图', go: 'import-parsing' }) +
        K.row({ ic: 'E943', t: 'JSON 文件', s: 'SillyTavern spec v2 / v3', go: 'import-parsing' }) +
        K.row({ ic: 'E71B', t: '从链接', s: '粘贴角色卡页面地址', go: 'import-url' }) +
        K.row({ ic: 'E8F1', t: '内置卡库', s: '以后提供', dis: true }) + K.gap(8) +
        K.note('PNG 不超过 5 MB。导入只新建档案，不会改动已有档案。')
      );
    }
  });

  screen({
    id: 'import-url', t: '导入 · 从链接', src: '设计稿 §6.4（P1）', sip: true,
    note: ['粘贴后自动识别站点；只下载卡图本身'],
    render: function () {
      return K.steps(3, 0) + K.ph('导入角色卡 · 1 / 3', '从链接', K.hb('解析', { acc: true, go: 'import-parsing' })) + K.body(
        K.tb('链接', 'https://example.com/characters/rainy-station', { focus: true, help: '支持直接指向 .png / .json 的地址，以及常见角色卡站点的详情页。' }));
    }
  });

  screen({
    id: 'import-parsing', t: '导入 · 解析中', src: 'CardImporter', sb: '言枢 · 正在解析',
    note: ['逐步显示：读块 → 解压 → 映射字段 → 世界书', '解析是纯字节处理，通常不到 1 秒'],
    render: function () {
      return K.steps(3, 0) + K.ph('导入角色卡', '正在解析') + K.body(
        '<div class="pad">' + K.dots() + '<div class="t-cap" style="margin-top:8px">rainy-station.png · 1.8 MB</div></div>' + K.gap(8) +
        K.row({ ic: 'E930', t: '读取 PNG 块', s: '找到 chara（zTXt）', dense: true }) +
        K.row({ ic: 'E930', t: '解压', s: '41 KB → 128 KB', dense: true }) +
        K.row({ lead: K.ring(), t: '映射字段', s: 'spec v3', dense: true }) +
        K.row({ ic: 'E823', icMid: true, t: '内嵌世界书', s: '等待', dense: true })
      ) + '<div class="foot">' + K.btn('取消', { go: 'import-source' }) + '</div>';
    }
  });

  screen({
    id: 'import-preview', t: '导入 2 / 3 · 预览与补齐', src: '设计稿 §6.4 第 2 步 · CardImportResult.Warnings / FilledFieldCount',
    note: ['卡图 + 名称 + 标签；字段完整度用 4px 进度条', '警告（缺示例对话）不挡路；缺名字时用文件名并让你改', '「用 AI 补全」下一期，先变灰'],
    render: function () {
      return K.steps(3, 1) + K.ph('导入角色卡 · 2 / 3', '预览') + K.body(
        '<div class="card-img"><div class="cap"><b>林</b><small>spec v3 · 作者 rainy · v1.2</small></div></div>' +
        '<div class="pad">' + K.bds([K.bd('角色', 'line'), K.bd('日常', 'line'), K.bd('雨', 'line')]) + '</div>' + K.gap(8) +
        '<div class="fld"><div class="fld-hd">字段完整度<span class="sp"></span><small>9 / 12</small></div>' + K.pbar(75) + '</div>' +
        K.kv([['首句', '（候车室的灯管闪了两下）「你又忘带伞了吧。」'], ['描述', '在末班车站等车的年轻女子，说话轻，观察细…'], ['性格', '温和、嘴硬'], ['场景', '雨夜的郊外车站'], ['示例对话', '<span class="t-warn">缺失</span>'], ['世界书', '18 个条目']]) +
        K.note('没有示例对话，不影响使用。', 'warn') +
        K.btns([K.btn('用 AI 补全缺失字段', { dis: true, icon: 'E82F' })], 'left')
      ) + '<div class="foot">' + K.btn('下一步', { acc: true, go: 'import-options' }) + K.btn('上一步', { go: 'import-source' }) + '</div>';
    }
  });

  screen({
    id: 'import-options', t: '导入 3 / 3 · 落盘选项', src: '设计稿 §6.4 第 3 步',
    note: ['档案名、slug 可改', '卡图作背景、导入内嵌世界书、创建首会话（含首句）等选项按「导入」后才生效，所以用 CheckBox 而不是 ToggleSwitch', '原图 card.png 原样保留，便于回导'],
    render: function () {
      return K.steps(3, 2) + K.ph('导入角色卡 · 3 / 3', '导入选项') + K.body(
        K.tb('档案名', '林') + K.tb('文件夹名', 'lin', { mono: true, help: 'workspace\\lin\\ · 原图存为 card.png，头像降采样为 avatar.png' }) +
        K.chk('卡图设为聊天背景', true, { s: '暗度 60%，可在外观里改' }) +
        K.chk('导入内嵌世界书', true, { s: '新建世界书「林 · 内嵌」18 个条目，并在这个档案里启用' }) +
        K.chk('创建第一个会话', true, { s: '首句作为第一条消息' }) +
        K.chk('强调色跟随卡图', false, { s: '从卡图提取主色 #A07A7F' })
      ) + '<div class="foot">' + K.btn('导入', { acc: true, go: 'import-done' }) + K.btn('上一步', { go: 'import-preview' }) + '</div>';
    }
  });

  screen({
    id: 'import-done', t: '导入完成', src: '设计稿 §6.4',
    note: ['写明做了哪几件事；主操作「开始对话」'],
    render: function () {
      return K.ph('导入角色卡', '已创建档案「林」') + K.body(
        '<div class="hero">' + avOf(P.lin, 'l') + '<div class="c"><b>林</b><small>lin · claude-sonnet-4.5（默认模型档案）</small></div></div>' +
        K.row({ ic: 'E930', t: '世界书', s: '18 个条目，已启用', dense: true }) +
        K.row({ ic: 'E930', t: '背景', s: '卡图 · 暗度 60%', dense: true }) +
        K.row({ ic: 'E930', t: '第一个会话', s: '「雨夜的车站」，首句已写入', dense: true })
      ) + '<div class="foot">' + K.btn('开始对话', { acc: true, go: 'chat-roleplay' }) + K.btn('查看档案', { go: 'profile' }) + '</div>';
    }
  });

  screen({
    id: 'import-error', t: '导入失败 · 文件过大', src: '设计稿 §6.4 状态与异常 · Limits.MaxCardPngBytes',
    note: ['写清上限与实际大小', '给替代办法'],
    render: function () {
      return K.ph('导入角色卡', '没能导入') + K.body(
        K.note('<b>rainy-station-4k.png 有 7.2 MB</b>，超过 5 MB 上限（防止超大图注入）。', 'err') +
        K.tx('可以先用图片工具缩小到 2000px 以内再导入，或者改用这张卡的 JSON 版本。')
      ) + '<div class="foot">' + K.btn('换一个文件', { acc: true, go: 'import-source' }) + K.btn('关闭', { go: 'profiles' }) + '</div>';
    }
  });

  screen({
    id: 'import-not-card', t: '导入 · 这不是角色卡', src: '设计稿 §6.4「PNG 无 chara / zTXt 块」',
    note: ['不当成失败：问要不要当普通图片做头像'],
    render: function () {
      return K.steps(3, 0) + K.ph('导入角色卡 · 1 / 3', '选来源') + K.body(K.row({ ic: 'E8B9', t: 'PNG 角色卡', s: '' })) +
        K.dlg({ title: '这张图片不是角色卡', body: K.tx('photo.png 里没有 chara 数据块。要把它当作普通图片，用来做新档案的头像吗？'), btns: [{ t: '做头像', acc: true, go: 'profile-new' }, { t: '换一个', go: 'import-source' }] });
    }
  });

  screen({
    id: 'import-error-ztxt', t: '导入失败 · 解压失败', src: '设计稿 §6.4「zTXt 解压失败 → 提示具体块名」',
    note: ['写出块名与偏移，方便反馈给卡作者'],
    render: function () {
      return K.ph('导入角色卡', '没能读出角色数据') + K.body(
        K.note('<b>zTXt 块「chara」解压失败</b><br>偏移 0x1F40 · 数据流在 12 KB 处提前结束。卡图可能被压缩工具改过。', 'err') +
        K.tx('建议向卡作者要一份原图，或改用 JSON 版本。') + K.row({ ic: 'E9D9', t: '查看调试日志', s: '记录了块列表，不含卡内容', go: 'debug' })
      ) + '<div class="foot">' + K.btn('换一个文件', { acc: true, go: 'import-source' }) + K.btn('关闭') + '</div>';
    }
  });

  screen({
    id: 'import-error-empty', t: '导入失败 · 没有字段', src: '设计稿 §6.4「解析出的字段为 0 条 → 明确提示」',
    note: ['不能静默成功地建出一个空档案'],
    render: function () {
      return K.ph('导入角色卡', '没有可用的字段') + K.body(
        K.note('<b>card.json 解析成功，但一个已知字段都没有</b>（name / description / first_mes 等全缺）。可能不是 SillyTavern 格式。', 'err') +
        K.tx('没有创建档案。') + K.kv([['顶层键', 'title, prompt, avatarUrl', 1], ['格式猜测', '其他应用导出']])
      ) + '<div class="foot">' + K.btn('换一个文件', { acc: true, go: 'import-source' }) + K.btn('关闭') + '</div>';
    }
  });
})();
