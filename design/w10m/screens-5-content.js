/* 08 记忆与提示词 · 09 会话管理与文件 */
(function () {
  'use strict';
  var ic = K.ic;

  function mem(text, cat, imp, meta, o) {
    o = o || {};
    return K.row({
      t: text, wrapT: true, go: o.go || 'memory-edit', dis: o.dis,
      l3: K.bd(cat, o.global ? 'acc' : '') + '<span class="imp"><i style="width:' + Math.round(imp * 100) + '%"></i></span><span>' + meta + '</span>'
    });
  }
  function memList() {
    return mem('用户偏好用 Newtonsoft 10.0.3，不升级；13.x 在 5.4.7 上会抛 FieldAccessException。', '事实', 0.9, '10月12日 · 用过 6 次') +
      mem('回答先给结论，再给理由；不喜欢客套。', '偏好', 0.8, '10月3日 · 用过 21 次') +
      mem('正在做 W10M 的聊天客户端「言枢」，目标设备 Lumia 950。', '事实', 0.75, '10月1日 · 用过 14 次') +
      mem('上周把 JSON 映射层从 JsonConvert 改成了自研 JsonBridge。', '经历', 0.65, '10月8日 · 用过 3 次') +
      mem('「帮我看看 JsonBridge 到底哪里不对」', '对话', 0.5, '10月12日 · 来自「排查列表永远为空」');
  }

  /* ======================= 08 记忆与提示词 ======================= */
  group('memory', '记忆与提示词', '记忆池（档案私有 + 可选全局）、世界书、模式注入。所有剪枝都先给预览；检索方式（向量 / 关键词）在页面上写明。');

  screen({
    id: 'memory', t: '记忆列表', src: '设计稿 §6.10 · UI-HANDOFF §1.2 记忆管理页',
    note: ['顶部：条数 / 检索方式 / 上次更新；档案池与全局池切换', '每条：内容、类别、重要性条、时间、用过几次', 'CommandBar：新增 / 整理 / 搜索；二级：重建索引、导出、清空'],
    render: function () {
      return K.ph('工程助理', '记忆') + K.flt(['档案池 42', '全局池 18'], 0) + K.body(
        '<div class="ctxnum" style="padding-top:0"><span>向量检索 · 上次更新 10:44</span><span>' + K.lnk('按重要性 ' + ic('E70D')) + '</span></div>' +
        K.flt(['全部', '事实', '偏好', '经历', '对话'], 0) + memList()
      ) + K.ab([{ ic: 'E710', t: '新增', go: 'memory-new' }, { ic: 'E8CB', t: '整理', go: 'memory-clean' }, { ic: 'E721', t: '搜索', go: 'search' }],
        [{ t: '索引状态 / 重建', go: 'memory-index' }, { t: '记忆设置', go: 'profile-memory' }, { t: '导出' }, { t: '清空', go: 'memory-clear' }]);
    }
  });

  screen({
    id: 'memory-empty', t: '记忆 · 空态', src: '设计稿 §6.10',
    note: ['说明记忆从哪来：每 10 轮自动抽取，或手动添加'],
    render: function () {
      return K.ph('翻译官', '记忆') + K.body(K.empty({ ic: 'E8F1', t: '还没有记忆', s: '每聊 10 轮会自动记下事实与偏好，也可以现在手动加一条。', btns: [K.btn('手动添加', { acc: true, go: 'memory-new' }), K.btn('记忆设置', { go: 'profile-memory' })] }), 'col') +
        K.ab([{ ic: 'E710', t: '新增', go: 'memory-new' }]);
    }
  });

  screen({
    id: 'memory-edit', t: '编辑记忆', src: 'MemoryItem：text / category / importance / scope / sourceConversationId',
    note: ['类别四选一；重要性滑块', '「提升为全局」= 范围改成全局池', '来源会话可点'],
    render: function () {
      return K.ph('工程助理 · 记忆', '编辑记忆') + K.body(
        K.tb('内容', '用户偏好用 Newtonsoft 10.0.3，不升级；13.x 在 5.4.7 上会抛 FieldAccessException。', { multi: true }) +
        K.rgrp('类别', ['事实', '偏好', '经历', '对话'], 0) +
        K.sl('重要性', 90, '0.90') +
        K.rgrp('范围', [{ t: '这个档案' }, { t: '全局', s: '所有开启「使用全局池」的档案都能用' }], 0) +
        K.kv([['来源', K.lnk('排查列表永远为空', 'chat')], ['创建', '10月12日 10:44'], ['用过', '6 次']])
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'memory' }, { ic: 'E711', t: '取消', go: 'memory' }], ['删除']);
    }
  });

  screen({
    id: 'memory-new', t: '新增记忆', src: '设计稿 §6.10「右上 + 手动新增」',
    note: ['不等模型抽取，直接写一条'],
    render: function () {
      return K.ph('工程助理 · 记忆', '新增记忆') + K.body(
        K.tb('内容', '', { ph: '一句话写一件事', multi: true }) + K.rgrp('类别', ['事实', '偏好', '经历'], 0) + K.sl('重要性', 70, '0.70') +
        K.rgrp('范围', ['这个档案', '全局'], 0)) + K.ab([{ ic: 'E74E', t: '保存', dis: true }, { ic: 'E711', t: '取消', go: 'memory' }]);
    }
  });

  screen({
    id: 'memory-clean', t: '整理记忆 · 预览', src: '设计稿 §8.5「任何剪枝先给预览」',
    note: ['分两组：低价值（重要性 ≤ 0.3 且 30 天未用）/ 重复合并', '每条可以取消勾选；底部按钮写实际数量'],
    render: function () {
      return K.ph('工程助理 · 记忆', '整理预览') + K.body(
        K.tx('整理后 42 → 31 条。取消勾选的会保留。', true) +
        K.sec('低价值 · 7 条') +
        K.chk('「好的」', true, { s: '对话 · 0.20 · 41 天没用过' }) +
        K.chk('用户问过天气', true, { s: '对话 · 0.30 · 33 天没用过' }) +
        K.chk('喜欢深色界面', false, { s: '偏好 · 0.30 · 35 天没用过' }) +
        K.sec('重复 · 合并 4 组') +
        K.chk('「先给结论」×3 → 合并为一条，重要性取最高 0.8', true) +
        K.chk('「目标设备 Lumia 950」×2 → 合并', true)
      ) + '<div class="foot">' + K.btn('删除 6 · 合并 4', { acc: true, go: 'memory' }) + K.btn('取消', { go: 'memory' }) + '</div>';
    }
  });

  screen({
    id: 'memory-index', t: '记忆索引状态', src: '设计稿 §8.6 · MemoryFile.embedder / dimensions · UI-HANDOFF §9',
    note: ['写明当前是向量还是关键词（BM25）检索', '嵌入模型换了：提示旧向量失配并重建；重建只动 vectors 段'],
    render: function () {
      return K.ph('工程助理 · 记忆', '索引状态') + K.body(
        K.note('<b>嵌入模型已更换</b>：旧向量是 text-embedding-3-small（1536 维），现在是 bge-m3（1024 维）。重建前暂用关键词检索。', 'warn') +
        K.kv([['当前检索', '关键词（BM25）'], ['向量', '0 / 42 条可用'], ['嵌入模型', 'bge-m3 · 1024 维'], ['上次重建', '10月3日']]) +
        K.sec('正在重建') + '<div class="pad">' + K.pbar(45) + '<div class="t-cap" style="margin-top:6px">19 / 42 · 文字不动，只重算向量</div></div>' + K.gap(16) +
        K.note('嵌入请求失败时会自动退回关键词检索，并在记忆设置页写明。')
      ) + K.ab([{ ic: 'E71A', t: '停止' }]);
    }
  });

  screen({
    id: 'memory-global', t: '全局记忆池', src: '设计稿 §8.1 第三层 · GlobalSettings.globalMemoryEnabled',
    note: ['只放用户身份类事实', '写明哪些档案在用'],
    render: function () {
      return K.ph('我的', '全局记忆池') + K.body(
        '<div class="ctxnum" style="padding-top:0"><span>18 条 · 工程助理、翻译官在用</span><span>' + K.lnk('设置', 'profile-memory') + '</span></div>' +
        mem('用户叫欧阳，后端工程师。', '事实', 0.9, '全局 · 用过 40 次', { global: true }) +
        mem('时区 Asia/Shanghai，工作日 9:30–19:00。', '事实', 0.8, '全局 · 用过 12 次', { global: true }) +
        mem('不喜欢被叫「老师」。', '偏好', 0.7, '全局 · 用过 5 次', { global: true })
      ) + K.ab([{ ic: 'E710', t: '新增', go: 'memory-new' }, { ic: 'E8CB', t: '整理', go: 'memory-clean' }]);
    }
  });

  screen({
    id: 'memory-clear', t: '清空记忆', src: '设计稿 §6.10「清空（保留导出）」',
    note: ['默认先导出一份再清空'],
    render: function () {
      return K.ph('工程助理', '记忆') + K.body(memList()) +
        K.dlg({ title: '清空 42 条记忆？', body: K.tx('清空前会导出到 exports\\memory-2026-10-12.json。') + K.chk('先导出', true, { dis: true }) + K.gap(8), btns: [{ t: '清空', acc: true, go: 'memory-empty' }, '取消'] });
    }
  });

  screen({
    id: 'lorebooks', t: '世界书列表', src: '设计稿 §6.11 · Lorebook',
    note: ['共享库；副标题写条目数、常驻数、来源', '哪些档案启用在档案侧勾选'],
    render: function () {
      return K.ph('我的', '世界书') + K.body(
        K.row({ ic: 'E82D', t: '工程术语', s: '13 个条目 · 2 个常驻 · 工程助理在用', go: 'lorebook-detail' }) +
        K.row({ ic: 'E82D', t: '林 · 内嵌', s: '18 个条目 · 来自角色卡 · 林在用', go: 'lorebook-detail' }) +
        K.row({ ic: 'E82D', t: '旧设定（停用）', s: '4 个条目', dis: true, go: 'lorebook-detail' })
      ) + K.ab([{ ic: 'E710', t: '新建', go: 'lorebook-new' }]);
    }
  });

  screen({
    id: 'lorebook-new', t: '新建世界书', src: '设计稿 §6.11',
    note: ['只要一个名字，条目建好后再加'],
    render: function () {
      return K.ph('我的', '世界书') + K.body(K.row({ ic: 'E82D', t: '工程术语', s: '13 个条目' })) +
        K.dlg({ title: '新建世界书', body: K.tb('名称', '项目代号', { focus: true }), btns: [{ t: '创建', acc: true, go: 'lorebook-detail' }, '取消'] });
    }
  });

  screen({
    id: 'lorebook-detail', t: '世界书详情', src: 'LorebookEntry 列表',
    note: ['每个条目：名称、关键词、注入位置、常驻 / 正则标记', '停用条目变灰'],
    render: function () {
      function e(n, kw, pos, bd, o) { return K.row({ t: n, s: kw, l3: K.bd(pos, 'line') + (bd || ''), go: 'lore-entry-edit', dis: o }); }
      return K.ph('世界书', '工程术语') + K.body(
        K.tog('启用这本世界书', true) +
        K.sec('13 个条目') +
        e('W10M', 'W10M · Windows 10 Mobile · 15254', '系统提示后', K.bd('常驻')) +
        e('.NET Native', '.NET Native · rd.xml · ARM Release', '系统提示后', K.bd('常驻')) +
        e('JsonBridge', 'JsonBridge · Newtonsoft · 10.0.3', '对话底') +
        e('PyUwp', '/py(uwp)?/i', '指定深度 4', K.bd('正则')) +
        e('Agents Anywhere', 'Agents Anywhere · mobile-v3', '系统提示后', '', true)
      ) + K.ab([{ ic: 'E710', t: '新增条目', go: 'lore-entry-edit' }], ['重命名', '导出', '删除']);
    }
  });

  screen({
    id: 'lore-entry-edit', t: '编辑世界书条目', src: '设计稿 §6.11 · LorebookEntry 全字段',
    note: ['关键词是芯片，支持中英标点分隔；正则 / 大小写开关', '注入位置五选一；选「指定深度」才出现深度', '保存时校验：关键词与内容非空、深度为正整数（这里演示一条报错）', '新增条目是同一页，字段为空'],
    render: function () {
      return K.ph('工程术语 · 条目', 'PyUwp') + K.body(
        K.tb('名称', 'PyUwp') +
        '<div class="fld"><div class="fld-hd">关键词<small>回车或逗号分隔</small></div>' + K.chips(['/py(uwp)?/i'], { mono: true, x: true, add: '添加' }) + '</div>' +
        K.chk('按正则匹配', true) + K.chk('区分大小写', false) +
        K.tb('内容', 'PyUwp 是 CPython 3.15 的 ARM32 W10M 移植，没有 _socket / _ssl / zipfile。', { multi: true }) +
        K.rgrp('注入位置', ['系统提示前', '系统提示后', '对话顶', '对话底', '指定深度'], 4) +
        K.tb('深度', '0', { err: '深度要是正整数（从末尾往前数第几条）' }) +
        K.rgrp('角色', ['system', 'user', 'assistant'], 0) +
        K.tb('优先级', '100', { help: '同一位置按优先级降序' }) +
        K.sl('扫描深度', 20, '最近 4 条') +
        K.tog('常驻（不看关键词，总是注入）', false) + K.tog('启用', true)
      ) + K.ab([{ ic: 'E74E', t: '保存' }, { ic: 'E711', t: '取消', go: 'lorebook-detail' }], ['删除条目']);
    }
  });

  screen({
    id: 'injections', t: '模式注入列表', src: '设计稿 §6.11 · ModeInjection',
    note: ['副标题：位置 · 优先级 · 启用的档案'],
    render: function () {
      return K.ph('我的', '模式注入') + K.body(
        K.row({ ic: 'E8FD', t: '先给结论', s: '系统提示后 · 100 · 工程助理', go: 'injection-edit' }) +
        K.row({ ic: 'E8FD', t: '中文回答', s: '对话底 · 90 · 工程助理、翻译官', go: 'injection-edit' }) +
        K.row({ ic: 'E8FD', t: '简洁模式', s: '指定深度 4 · 80 · 无档案启用', go: 'injection-edit' }) +
        K.row({ ic: 'E8FD', t: '安全提醒', s: '系统提示前 · 120 · 已停用', dis: true, go: 'injection-edit' })
      ) + K.ab([{ ic: 'E710', t: '新建', go: 'injection-edit' }]);
    }
  });

  screen({
    id: 'injection-edit', t: '编辑模式注入', src: 'ModeInjection：content / position / role / priority / depth / enabled',
    note: ['与世界书条目共用位置与角色控件', '提示：绝不插在带 tool_calls 的消息与其结果之间（由装配器保证）'],
    render: function () {
      return K.ph('模式注入', '中文回答') + K.body(
        K.tb('名称', '中文回答') + K.tb('内容', '无论用户用什么语言提问，都用简体中文回答；代码与专有名词保持原样。', { multi: true }) +
        K.rgrp('位置', ['系统提示前', '系统提示后', '对话顶', '对话底', '指定深度'], 3) +
        K.rgrp('角色', ['system', 'user', 'assistant'], 0) +
        K.tb('优先级', '90') + K.tog('启用', true) +
        K.note('同一位置、同一角色的多条注入会合并成一条消息。')
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'injections' }, { ic: 'E711', t: '取消', go: 'injections' }], ['删除']);
    }
  });

  /* ======================= 09 会话管理与文件 ======================= */
  group('manage', '会话管理与文件', '会话设置是列表式二级页，一行一个动作，改动立即生效（W10M 设置页不需要「保存」）。附件与档案文件、调试用的用量页都从这里进入。');

  screen({
    id: 'chat-settings', t: '会话设置', src: '设计稿 §6.8 · UI-HANDOFF §1.3「会话设置里的四个动作」',
    note: ['重命名行内编辑；归属档案改了记忆随之迁移', '「完全信任」只在这里、只对本次会话，离开即复位', '朗读 / 翻译 / 用量 / 附件 / 上下文都是独立页'],
    render: function () {
      return K.ph('会话', '会话设置') + K.body(
        K.tb('标题', '排查列表永远为空') +
        K.row({ av: avOf(P.eng, 's'), t: '归属档案', s: '工程助理 · 改了以后记忆随之迁移', go: 'move-conversations' }) +
        K.row({ ic: 'E99A', t: '这个会话的模型', s: '跟随档案（deepseek-chat）', go: 'model-picker' }) +
        K.tog('置顶', false) +
        K.tog('完全信任（仅本次会话）', false, { s: '跳过敏感操作的审批。离开会话自动关闭，不会保存。' }) +
        K.sec('内容') +
        K.row({ ic: 'E8CB', t: '上下文', s: '已用 34% · 未压缩过', go: 'context' }) +
        K.row({ ic: 'E723', t: '附件与文件', s: '2 个附件', go: 'attachments' }) +
        K.row({ ic: 'E716', t: '群聊成员', s: '单人会话 · 加成员变群聊', go: 'group-members' }) +
        K.row({ ic: 'E70F', t: '系统提示覆盖', s: '档案未允许会话覆盖', dis: true }) +
        K.row({ ic: 'E771', t: '外观', s: '跟随档案', go: 'chat-appearance' }) +
        K.sec('朗读 · 翻译 · 用量') +
        K.row({ ic: 'E767', t: '朗读', s: 'Huihui · 1.0 倍速', go: 'speech-settings' }) +
        K.row({ ic: 'E8C1', t: '翻译', s: '目标语言 English', go: 'translation' }) +
        K.row({ ic: 'E9D9', t: '会话用量', s: '14 轮 · 41.2k tokens', go: 'usage' }) +
        K.sec('管理') +
        K.row({ ic: 'E72D', t: '导出', s: 'JSON / JSONL / Markdown', go: 'conversation-export' }) +
        K.row({ ic: 'E8F1', t: '归档', s: '从列表收起，可在「已归档」找回' }) +
        K.row({ ic: 'E74D', t: '删除', s: '移进回收区', go: 'chat-delete' })
      );
    }
  });

  screen({
    id: 'chat-delete', t: '删除会话', src: '设计稿 §6.8「删除（二次确认，移入 _trash）」',
    note: ['写明去处与可恢复期'],
    render: function () {
      return K.ph('会话', '会话设置') + K.body(K.tb('标题', '排查列表永远为空')) +
        K.dlg({ title: '删除「排查列表永远为空」？', body: K.tx('14 轮对话与 2 个附件会移进回收区，30 天内可以恢复。'), btns: [{ t: '删除', acc: true, go: 'trash' }, '取消'] });
    }
  });

  screen({
    id: 'archived', t: '已归档会话', src: '设计稿 §6.8 归档',
    note: ['与对话页同一套行；长按可取消归档'],
    render: function () {
      return K.ph('对话', '已归档') + K.body(
        conv({ t: '旧版迁移检查', time: '9月28日', prev: '迁移只读旧数据，不删除。', state: 'done', p: '已导入', tok: '1.2k' }) +
        conv({ t: '试试 Gemini 原生接口', time: '9月20日', prev: 'safetySettings 要放在 generationConfig 外面。', state: 'done', p: '工程助理', tok: '4.0k' })
      ) + K.ab([{ ic: 'E762', t: '选择', go: 'home-select' }]);
    }
  });

  screen({
    id: 'move-conversations', t: '移动到档案', src: '设计稿 §6.7 批量操作 · §6.8 归属档案',
    note: ['全屏选择页；当前所属档案标「当前」'],
    render: function () {
      return K.picker('移动 3 个会话到', [
        { t: P.eng.n, s: '当前', av: avOf(P.eng), on: true },
        { t: P.lin.n, s: P.lin.m, av: avOf(P.lin), go: 'home' },
        { t: P.tr.n, s: P.tr.m, av: avOf(P.tr), go: 'home' },
        { t: P.loc.n, s: P.loc.m, av: avOf(P.loc), go: 'home' }]);
    }
  });

  screen({
    id: 'chat-appearance', t: '会话外观', src: '设计稿 §6.8「外观（仅此会话覆盖档案）」',
    note: ['默认跟随档案；关掉后才出现覆盖项'],
    render: function () {
      return K.ph('会话 · 外观', '外观') + K.body(
        K.tog('跟随档案', false, { s: '关掉后只改这个会话' }) +
        K.rgrp('背景', ['无', '纯色', '图片'], 1) +
        '<div class="fld-hd" style="padding:0 12px">背景色</div>' + K.swg(['#000000', '#101820', '#1B1A2E', '#1E2A1E', '#2A1E1E', '#262626', '#0C2A3A', '#2D2340'], 2, { s8: true }) +
        K.sl('背景暗度', 0, '0%', { dis: true }) +
        '<div class="fld-hd" style="padding:0 12px">用户气泡</div>' + K.swg(['none', '#0078D7', '#E81123', '#10893E', '#8764B8', '#CA5010', '#00B7C3', '#5D5A58'], 0, { s8: true })
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'chat-settings' }, { ic: 'E711', t: '取消', go: 'chat-settings' }], ['恢复默认']);
    }
  });

  screen({
    id: 'context', t: '上下文与压缩', src: '设计稿 §6.8 上下文 · ContextCompressor · Limits.Compact*',
    note: ['大数字 + 4px 用量条；写明是估算还是服务端数字', '阈值 80%、保留最近 4 条、少于 6 条不压缩', '显示已压缩的摘要'],
    render: function () {
      return K.ph('会话 · 上下文', '上下文') + K.body(
        '<div class="pad"><span class="t-subhead">34%</span><span class="t-mid" style="margin-left:8px">21.8k / 64k tokens · 服务端计数</span></div>' + K.gap(8) +
        '<div class="ctxbar">' + K.pbar(34) + '</div><div class="ctxnum"><span>0</span><span>80% 自动压缩</span><span>64k</span></div>' +
        K.kv([['保留', '最近 4 条不动'], ['最少', '少于 6 条不压缩'], ['上次压缩', '还没有']]) +
        K.sec('摘要') + K.tx('还没有压缩过。压缩会把较早的消息交给副模型写成 800–1500 字的结构化摘要。', true)
      ) + '<div class="foot">' + K.btn('立即压缩', { acc: true, go: 'context-preview' }) + '</div>';
    }
  });

  screen({
    id: 'context-preview', t: '上下文压缩预览', src: 'ContextCompressor',
    note: ['先生成摘要给你看，确认后才替换', '写明范围与预计节省'],
    render: function () {
      return K.ph('会话 · 上下文', '压缩预览') + K.body(
        K.kv([['范围', '第 1–38 条 → 一段摘要'], ['保留', '第 39–42 条原样'], ['预计', '55.0k → 9.4k tokens']]) +
        K.sec('摘要草稿 · deepseek-chat 生成 · 1.2k 字') +
        '<div class="pre">【目标】排查 ARM Release 下列表为空。\n【已确认】x86 正常；JsonBridge 反射在 .NET Native 下返回 0 个属性。\n【结论】rd.xml 未覆盖 ConversationIndexEntry 等类型。\n【待办】补齐 rd.xml；真机验证 diagnostics.log。</div>' +
        K.note('原消息不删除，仍在会话文件里；只是不再整段发给模型。')
      ) + '<div class="foot">' + K.btn('应用压缩', { acc: true, go: 'chat-history' }) + K.btn('取消', { go: 'context' }) + '</div>';
    }
  });

  screen({
    id: 'speech-settings', t: '朗读设置', src: 'TtsService：Voice / Rate / Volume · SpeechText',
    note: ['语音用系统语音列表；语速、音量滑块', '代码块与链接自动跳过（Markdown 清洗）'],
    render: function () {
      return K.ph('会话 · 朗读', '朗读') + K.body(
        K.cb('语音', 'Microsoft Huihui · 中文（中国）') +
        K.sl('语速', 50, '1.0 倍') + K.sl('音量', 100, '100%') +
        K.note('朗读前会去掉 Markdown 标记，代码块与链接不读；按句切段，随时可以停。') +
        K.btns([K.btn('试听', { icon: 'E768' })], 'left')
      );
    }
  });

  screen({
    id: 'translation', t: '消息翻译', src: 'TranslationService.CommonLanguages · 按语言缓存',
    note: ['目标语言单选（常用 10 种）', '代码块不翻译；译文按语言缓存', '翻译用副模型'],
    render: function () {
      return K.ph('会话 · 翻译', '翻译') + K.body(
        K.rgrp('翻译成', ['简体中文（zh-CN）', 'English（en）', '日本語（ja）', '한국어（ko）', 'Français（fr）', 'Deutsch（de）', 'Español（es）', 'Русский（ru）', 'Português（pt）', 'Italiano（it）'], 1) +
        K.cb('使用模型', '副模型 · deepseek-chat', { go: 'model-picker' }) +
        K.row({ ic: 'E74D', t: '清除译文缓存', s: '这个会话已缓存 12 条' })
      );
    }
  });

  screen({
    id: 'usage', t: '会话用量', src: 'UI-HANDOFF §1.2「独立的会话用量页」· TokenTechLog / TechLogSummary',
    note: ['汇总用大数字网格；每轮一行', '只记元数据：不记提示词正文，地址只留主机名', '可复制、可导出 JSONL 贴到 issue'],
    render: function () {
      function turn(n, time, s, bds) { return K.row({ t: '第 ' + n + ' 轮', rt: time, s: s, l3: bds, dense: true }); }
      return K.ph('会话 · 用量', '用量') + K.body(
        K.metric([['14', '轮'], ['41.2k', '总 tokens'], ['33.9k / 7.3k', '输入 / 输出'], ['62%', '缓存命中'], ['21 · 37', '模型调用 · 工具调用'], ['31.0k', '压缩节省']]) +
        K.tx('失败 1 轮 · 估算 2 轮（服务端没返回用量）', true) +
        K.sec('每一轮') +
        turn(14, '10:38', 'deepseek-chat · api.deepseek.com · 3.1k / 620 · 首字 420 ms · 6.2 s', K.bd('流式') + K.bd('工具 5')) +
        turn(13, '10:31', 'deepseek-chat · api.deepseek.com · 2.8k / 410 · 4.0 s', K.bd('流式')) +
        turn(12, '10:30', 'deepseek-chat · api.deepseek.com · HTTP 503', K.bd('失败', 'err')) +
        turn(11, '10:12', 'deepseek-chat · 估算 2.2k / 380', K.bd('估算', 'line') + K.bd('已压缩', 'acc'))
      ) + K.ab([{ ic: 'E8C8', t: '复制' }, { ic: 'E72D', t: '导出 JSONL' }]);
    }
  });

  screen({
    id: 'conversation-export', t: '导出会话', src: '设计稿 §6.14 导出 · §6.8',
    note: ['格式三选一；附件选择会改成 zip', '默认存档案 exports\\，也可另存到文档库'],
    render: function () {
      return K.ph('会话 · 导出', '导出') + K.body(
        K.rgrp('格式', [{ t: 'Markdown', s: '适合阅读与分享' }, { t: 'JSON', s: '完整结构，可再导入' }, { t: 'JSONL', s: '一行一条消息' }], 0) +
        K.chk('包含思考过程', false) + K.chk('包含工具步骤', true) + K.chk('包含附件', false, { s: '勾选后打包成 zip' }) + K.gap(8) +
        K.rgrp('保存到', [{ t: '档案的 exports\\', s: 'engineering\\exports\\排查列表永远为空.md' }, { t: '选择位置…', s: '文档库、SD 卡' }], 0)
      ) + '<div class="foot">' + K.btn('导出', { acc: true }) + K.btn('取消', { go: 'chat-settings' }) + '</div>';
    }
  });

  screen({
    id: 'attachments', t: '会话附件', src: '设计稿 §6.13 会话附件 · Limits.MaxAttachments / MaxAttachmentBytes',
    note: ['顶部写总量：个数 / 大小上限', '每个附件写进上下文的方式：全文 / 截断 / 视觉 / 只发文件名'],
    render: function () {
      return K.ph('会话 · 附件', '附件与文件') + K.body(
        '<div class="ctxnum" style="padding-top:0"><span>4 / 10 个</span><span>6.1 MB / 25 MB</span></div>' +
        K.row({ lead: T.thumb('', 48), t: 'IMG_2041.jpg', s: '380 KB（原 4.2 MB）· 只发文件名：模型不支持视觉', go: 'image-viewer' }) +
        K.row({ ic: 'E8A5', t: '需求.docx', s: '解析为 3.2k 字 · 已进上下文', go: 'file-preview' }) +
        K.row({ ic: 'E9D9', t: 'diagnostics.log', s: '120 KB · 截断到前 8000 字' }) +
        K.row({ ic: 'E8A5', t: 'glossary.xlsx', s: '2 张表 · 86 行 · 转成 Markdown 表格', go: 'file-preview' })
      ) + K.ab([{ ic: 'E710', t: '添加', go: 'attachment-add' }, { ic: 'E713', t: '处理设置', go: 'attachment-settings' }]);
    }
  });

  screen({
    id: 'attachment-add', t: '添加附件', src: '设计稿 §6.13 · DocumentExtractor 支持格式',
    note: ['四个来源；下方写支持的格式与上限'],
    render: function () {
      return K.picker('添加附件', [
        { t: '相册', s: '图片会压缩到 1600px', go: 'attachment-preview' },
        { t: '拍照', go: 'attachment-preview' },
        { t: '文件', s: 'txt md csv json · docx xlsx pptx · pdf', go: 'attachment-preview' },
        { t: '档案文件', s: '从 engineering\\files\\ 选', go: 'files' }]) +
        '<div style="flex:none;padding:0 12px 12px" class="t-cap">单个 ≤ 25 MB，一次最多 10 个；文本文件 ≤ 4 MB。</div>';
    }
  });

  screen({
    id: 'attachment-preview', t: '附件发送前预览', src: 'ImageCompressor · DocumentExtractor · UwpPdfRenderer',
    note: ['图片：原图与压缩后尺寸 / 大小', '文档：解析结果摘要', '模型不支持视觉时明确写「只发文件名」'],
    render: function () {
      return K.ph('添加附件', '发送前确认') + K.body(
        '<div class="pad">' + T.thumb('', 336).replace('width:336px', 'width:100%').replace('height:336px', 'height:180px') + '</div>' + K.gap(8) +
        K.kv([['原图', '4032 × 3024 · 4.2 MB'], ['发送', '1600 × 1200 · JPEG 85 · 380 KB']]) +
        K.note('deepseek-chat 不支持视觉：图片只会以文件名发送。换成支持视觉的模型后可以直接看图。', 'warn') +
        K.sec('另外 1 个文件') + K.row({ ic: 'E8A5', t: '报价.pdf', s: '6 页 · 渲染第 1–3 页为图片，抽取文字 2.1k 字', dense: true })
      ) + '<div class="foot">' + K.btn('加入', { acc: true, go: 'chat-compose-attach' }) + K.btn('取消', { go: 'chat' }) + '</div>';
    }
  });

  screen({
    id: 'attachment-settings', t: '附件处理设置', src: 'ImageCompressor 选项 · Limits',
    note: ['图片长边与质量；文本截断；PDF 页数'],
    render: function () {
      return K.ph('会话 · 附件', '处理设置') + K.body(
        K.sl('图片长边', 50, '1600 px', { min: '800', max: '2400' }) + K.sl('JPEG 质量', 85, '85') +
        K.tog('保留原图到档案 files\\', false, { s: '只影响本地，不影响发送' }) +
        K.sl('文本附件截断', 40, '8000 字') + K.sl('PDF 渲染页数', 30, '前 3 页') +
        K.note('上限固定：单个 25 MB、一次 10 个、文本 4 MB。')
      );
    }
  });

  screen({
    id: 'files', t: '档案文件', src: '设计稿 §6.13 档案文件',
    note: ['面包屑 + 列表；行内大小与修改时间', '文件夹在前；点文件按类型进预览 / 编辑 / 看图'],
    render: function () {
      return K.ph('工程助理', '文件') + K.crumb(['engineering', 'files']) + K.body(
        K.row({ ic: 'E8B7', t: 'notes', s: '4 个文件', go: 'file-folder' }) +
        K.row({ ic: 'E8B7', t: 'specs', s: '2 个文件', go: 'file-folder' }) +
        K.row({ ic: 'E8A5', t: '设计稿.md', s: '75 KB · 10月11日', go: 'file-editor' }) +
        K.row({ ic: 'E8A5', t: '需求.docx', s: '48 KB · 10月10日', go: 'file-preview' }) +
        K.row({ ic: 'E943', t: 'usage.jsonl', s: '12 KB · 今天 10:38', go: 'file-editor' }) +
        K.row({ lead: T.thumb('b', 24), t: 'screenshot.png', s: '210 KB · 10月9日', go: 'image-viewer' })
      ) + K.ab([{ ic: 'E710', t: '新建文本', go: 'file-new' }, { ic: 'E898', t: '上传' }, { ic: 'E762', t: '选择' }], ['新建文件夹', '授权目录']);
    }
  });

  screen({
    id: 'files-empty', t: '文件 · 空态', src: '设计稿 §6.13',
    note: ['说明文件夹的用途（知识库与附件）'],
    render: function () {
      return K.ph('翻译官', '文件') + K.crumb(['translator', 'files']) + K.body(K.empty({ ic: 'E8B7', t: '这个档案还没有文件', s: '放在这里的文件可以被工具读取，也可以作为知识库注入提示词。', btns: [K.btn('上传', { acc: true }), K.btn('新建文本', { go: 'file-new' })] }), 'col') +
        K.ab([{ ic: 'E710', t: '新建文本', go: 'file-new' }, { ic: 'E898', t: '上传' }]);
    }
  });

  screen({
    id: 'file-folder', t: '文件夹内容', src: '设计稿 §6.13 面包屑',
    note: ['面包屑可点回上级'],
    render: function () {
      return K.ph('工程助理', 'notes') + K.crumb(['engineering', 'files', 'notes']) + K.body(
        K.row({ ic: 'E8A5', t: 'weekly-41.md', s: '2.3 KB · 今天 10:44 · 由 write_file 创建', go: 'file-editor' }) +
        K.row({ ic: 'E8A5', t: 'weekly-40.md', s: '2.0 KB · 10月5日', go: 'file-editor' }) +
        K.row({ ic: 'E8A5', t: 'todo.md', s: '640 B · 10月3日', go: 'file-editor' }) +
        K.row({ ic: 'E8A5', t: '一个非常长的文件名_without_spaces_用来验证不撑破布局_final_v2.md', s: '1 KB', go: 'file-editor' })
      ) + K.ab([{ ic: 'E710', t: '新建文本', go: 'file-new' }, { ic: 'E898', t: '上传' }, { ic: 'E762', t: '选择' }]);
    }
  });

  screen({
    id: 'file-preview', t: '文件解析预览', src: 'OoxmlReader / DocumentExtractor（docx / xlsx / pptx 端上解析）',
    note: ['显示解析出来的文本，表格转 Markdown', '操作：作为附件发送 / 加入知识库注入'],
    render: function () {
      return K.ph('files', '需求.docx') + K.body(
        K.kv([['类型', 'Word 文档 · 端上解析'], ['内容', '3.2k 字 · 2 张表 · 1 张图片（已忽略）']]) +
        T.wrap(T.out('<h4>言枢 v1.3 需求</h4><p>1. 模型选择器按服务分组，支持收藏。</p>' + T.table(['页面', '优先级'], [['模型选择器', 'P0'], ['群聊成员', 'P1']])))
      ) + K.ab([{ ic: 'E723', t: '作为附件', go: 'chat-compose-attach' }, { ic: 'E82D', t: '加入知识库' }], ['用其他应用打开']);
    }
  });

  screen({
    id: 'file-editor', t: '文本文件编辑', src: '设计稿 §6.13 · §6.5 软键盘对策 2', sip: true,
    note: ['UTF-8；编辑态「保存」在标题行，不被键盘挡', '保存带 sha256 冲突保护（被别处改过会提示）'],
    render: function () {
      return K.ph('notes · 已修改', 'weekly-41.md', K.hb('保存', { acc: true, go: 'file-conflict' })) + K.body(
        K.tb('', '# 第 41 周周报\n## 做了什么\n- 修复 .NET Native 下 JSON 映射为空\n- 补齐 rd.xml', { focus: true, tall: true, mono: true }));
    }
  });

  screen({
    id: 'file-new', t: '新建文本文件', src: '设计稿 §6.13',
    note: ['文件名 + 内容；扩展名决定预览方式'],
    render: function () {
      return K.ph('files', '新建文本文件') + K.body(K.tb('文件名', 'notes/todo-2.md', { mono: true }) + K.tb('内容', '', { ph: '可以先留空', tall: true, mono: true })) +
        K.ab([{ ic: 'E74E', t: '保存', go: 'files' }, { ic: 'E711', t: '取消', go: 'files' }]);
    }
  });

  screen({
    id: 'file-conflict', t: '文件保存冲突', src: '设计稿 §6.13「ifMatch: sha256 冲突保护」',
    note: ['14393 的 ContentDialog 只有两个按钮：覆盖 / 另存为副本；返回键 = 继续编辑'],
    render: function () {
      return K.ph('notes · 已修改', 'weekly-41.md') + K.body(K.tb('', '# 第 41 周周报\n## 做了什么', { tall: true, mono: true })) +
        K.dlg({ title: '文件在别处被改过', body: K.tx('你打开之后，weekly-41.md 在 10:44 被 write_file 修改了。要覆盖它，还是把你的版本另存一份？'), btns: [{ t: '另存为副本', acc: true, go: 'file-folder' }, { t: '覆盖', go: 'file-folder' }] });
    }
  });

  screen({
    id: 'image-viewer', t: '图片查看', src: '设计稿 §6.13「图片 → 全屏查看」',
    note: ['黑底全屏，双指缩放；点一下显示 / 隐藏应用栏'],
    render: function () {
      return '<div class="body col" style="background:#000;justify-content:center">' + '<span class="thumb" style="width:100%;height:270px"></span>' +
        '<div class="t-cap" style="padding:10px 12px;color:#999">IMG_2041.jpg · 1600 × 1200 · 380 KB</div></div>' +
        K.ab([{ ic: 'E72D', t: '分享' }, { ic: 'E77B', t: '设为头像', go: 'avatar' }, { ic: 'E74D', t: '删除' }]);
    }
  });
})();
