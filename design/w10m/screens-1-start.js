/* 00 规范样张 · 01 启动与根导航 */
(function () {
  'use strict';
  var ic = K.ic;

  /* ---------- 公共数据 ---------- */
  var P = window.P = {
    eng: { n: '工程助理', ch: '工', m: 'deepseek-chat', s: 'deepseek-chat · 18 个会话 · 42 条记忆', tag: '工程' },
    lin: { n: '林', ch: '林', m: 'claude-sonnet-4.5', s: 'claude-sonnet-4.5 · 6 个会话 · 126 条记忆', tag: '角色', img: 1 },
    tr: { n: '翻译官', ch: '译', m: 'gemini-2.5-flash', s: 'gemini-2.5-flash · 9 个会话 · 3 条记忆', tag: '翻译' },
    loc: { n: '本地研究员', ch: '研', m: 'qwen2.5-7b-instruct', s: 'qwen2.5-7b-instruct · 2 个会话 · 无记忆', tag: '工程' }
  };
  window.avOf = function (p, size) { return K.av(p.ch, { size: size, img: p.img }); };

  function conv(o) {
    var l3 = [];
    if (o.state === 'warn') l3.push(K.bd('需要你处理', 'warn'));
    if (o.state === 'run') l3.push(K.bd('运行中 · ' + o.step, 'acc'));
    if (o.state === 'err') l3.push(K.bd('失败', 'err'));
    if (o.state === 'done') l3.push('<span>已完成</span>');
    if (o.pin) l3.push(ic('E718'));
    l3.push('<span>' + o.p + '</span>');
    if (o.tok) l3.push('<span class="mono">' + o.tok + '</span>');
    return K.row({ t: o.t, rt: o.time, s: o.prev, l3: l3.join(''), bar: o.state === 'warn' ? 'warn' : o.state === 'run' ? 'acc' : o.state === 'err' ? 'err' : 'n', go: o.go || 'chat', check: o.check });
  }
  window.conv = conv;

  var CONV = {
    warn: { t: '整理周报并写入 notes', time: '10:41', prev: '等待批准：写入文件 notes/weekly-41.md', state: 'warn', p: '工程助理', tok: '4.2k', go: 'chat-approval' },
    run: { t: '排查列表永远为空', time: '10:38', prev: '正在读取 Core\\Storage\\JsonBridge.cs', state: 'run', step: '第 6 步', p: '工程助理', tok: '11.8k', go: 'chat-running' },
    err: { t: '翻译 1.2 发布说明', time: '10:20', prev: 'HTTP 429：请求过于频繁，稍后重试', state: 'err', p: '翻译官', tok: '2.1k', go: 'chat-error' },
    grp: { t: '三个人的方案评审', time: '10:12', prev: '小满：配置的默认值要写在代码里，而不是…', state: 'done', p: '群聊 · 3 位', tok: '6.4k', go: 'group-chat', pin: true },
    lin: { t: '雨夜的车站', time: '09:40', prev: '林：「你又忘带伞了吧。」（她把伞往你这边偏了偏）', state: 'done', p: '林', tok: '18.0k', go: 'chat-roleplay' },
    week: { t: '周报怎么写', time: '昨天', prev: 'yes — that reads much better. the only remaining nit…', state: 'done', p: '工程助理', tok: '3.3k' },
    long: { t: '一个很长的会话（用来验证滚动与懒加载，标题故意写得很长很长）', time: '10月9日', prev: '第 12 个回答：先确认输入，再确认输出，最后看中间…', state: 'done', p: '工程助理', tok: '2.6k', go: 'chat-history' }
  };
  window.CONV = CONV;

  function profileStrip(sel) {
    var items = [{ n: '全部', av: K.av(ic('E8FD'), { n: true }) }].concat([P.eng, P.lin, P.tr, P.loc].map(function (p) { return { n: p.n, av: avOf(p) }; }));
    return '<div class="strip-av">' + items.map(function (it, i) {
      return '<div class="it tilt' + (i === sel ? ' on' : '') + '" data-go="' + (i === 0 ? 'home' : i === 1 ? 'home-filtered' : 'home') + '">' + it.av + '<span class="ell">' + it.n + '</span></div>';
    }).join('') + '</div>';
  }
  window.profileStrip = profileStrip;

  var HOME_AB = function (open) {
    return K.ab([{ ic: 'E710', t: '新建会话', go: 'new-chat' }, { ic: 'E8B9', t: '导入角色卡', go: 'import-source' }, { ic: 'E721', t: '搜索', go: 'search' }],
      [{ t: '批量管理', go: 'home-select' }, { t: '已归档会话', go: 'archived' }, { t: '新建档案', go: 'profile-new' }], { open: open });
  };
  function homeList() {
    return profileStrip(0) +
      K.sec('需要你处理', { warn: true }) + conv(CONV.warn) +
      K.sec('今天') + conv(CONV.run) + conv(CONV.err) + conv(CONV.grp) + conv(CONV.lin) +
      K.sec('更早') + conv(CONV.week) + conv(CONV.long);
  }
  window.homeRender = function (open) { return K.root(0) + K.body(homeList()) + HOME_AB(open); };

  /* ======================= 00 规范样张 ======================= */
  group('spec', '规范样张', '先看这一组：W10M 控件、列表、弹层与时间线的标准形态。后面所有页面都由这些部件拼成，XAML 侧按这里建样式。');

  screen({
    id: 'spec-type', t: '文字刻度与页头', src: 'W10 type ramp · 设计稿 §5.3',
    note: ['Caption 12 / Body 15 / Base 15 SemiBold / Subtitle 20 / Title 24 SemiLight / Subheader 34 Light', '子页页头 = 12px 上标签 + 24px 页名，不加色块', '页边距 12'],
    render: function () {
      return K.ph('规范样张 · 文字', '文字刻度') + K.body(
        '<div class="pad">' +
        '<div class="t-subhead">Subheader 34</div>' +
        '<div class="t-title">Title 24 SemiLight</div>' +
        '<div class="t-sub">Subtitle 20 · 对话框标题</div>' +
        '<div class="t-base">Base 15 SemiBold · 强调行</div>' +
        '<div class="t-body">Body 15 · 正文与列表主标题，行高 1.45 左右。</div>' +
        '<div class="t-cap">Caption 12 · 副标题、时间、计数</div></div>' +
        K.sec('分节标签 · 12 SEMIBOLD') +
        K.row({ ic: 'E8F2', t: '列表主标题 15', s: '副标题 12 · 写当前值，而不是画箭头' }) +
        K.sec('颜色') +
        '<div class="pad t-body">文字 <span class="t-soft">次要</span> <span class="t-mid">弱化</span> <span class="t-acc">强调文字</span> <span class="t-warn">警告</span> <span class="t-err">错误</span> <span class="t-ok">完成</span></div>' + K.gap(16) +
        K.note('<b>单强调色：</b>一屏只有一个实心强调色焦点（主按钮、用户气泡或选中项），其余用强调色文字或柔底。')
      );
    }
  });

  screen({
    id: 'spec-controls', t: '基础控件', src: 'W10M 系统控件 · 设计稿 §5.5',
    note: ['全部直角（14393 的 Control 没有 CornerRadius）', 'TextBox 聚焦 = 白底黑字 + 强调色边', 'ToggleSwitch 标题在上、开关与「开/关」在下'],
    render: function () {
      return K.ph('规范样张 · 控件', '基础控件') + K.body(
        K.btns([K.btn('次要'), K.btn('主要', { acc: true }), K.btn('禁用', { dis: true })]) +
        K.tb('文本框 · 静止', '', { ph: '占位文字' }) +
        K.tb('文本框 · 聚焦', '正在输入', { focus: true }) +
        K.tb('文本框 · 校验失败', 'temperature=3', { err: '取值范围 0–2' }) +
        K.tb('密码框', '••••••••••', { icon: 'E890', help: '只存进系统凭据保险库' }) +
        K.cb('下拉框', 'OpenAI 兼容') +
        K.tog('开关 · 开', true, { s: '说明文字写在标题下面' }) + K.tog('开关 · 关', false) +
        K.chk('复选框 · 选中', true) + K.chk('复选框 · 未选', false) +
        K.rgrp('', ['单选 · 选中', '单选 · 未选'], 0) +
        K.sl('滑块', 35, '0.70', { min: '0', max: '2' }) +
        '<div class="fld"><div class="fld-hd">进度条 · 确定</div>' + K.pbar(62) + '</div>' +
        '<div class="fld"><div class="fld-hd">进度条 · 不定（五点流动）</div>' + K.dots() + '</div>' +
        '<div class="fld"><div class="fld-hd">进度环</div>' + K.ring() + '</div>'
      );
    }
  });

  screen({
    id: 'spec-lists', t: '列表行与状态', src: '设计稿 §5.5 · §6.7 · UI-HANDOFF §3.2-3',
    note: ['行无 chevron、无分隔线', '4px 左色条是唯一状态信号：警告 / 强调 / 错误 / 中性', '选中 = 柔底 + 4px 强调色条'],
    render: function () {
      return K.ph('规范样张 · 列表', '列表行与状态') + K.body(
        K.sec('设置行') + K.row({ ic: 'E713', t: '单行标题' }) + K.row({ ic: 'E77B', t: '双行标题', s: '副标题写当前值：默认 · DeepSeek' }) +
        K.sec('会话行 · 三行 + 左色条') +
        conv(CONV.warn) + conv(CONV.run) + conv(CONV.err) + conv(CONV.week) +
        K.sec('选中 / 禁用') + K.row({ av: avOf(P.eng), t: '工程助理', s: P.eng.s, sel: true }) + K.row({ ic: 'E943', t: 'run_python', s: '运行时不可用：这台设备没有装 PyUwp 载荷', dis: true }) +
        K.sec('徽标 / 芯片') + '<div class="pad">' + K.bds([K.bd('视觉', 'acc'), K.bd('推理'), K.bd('工具'), K.bd('128K', 'line'), K.bd('敏感', 'warn'), K.bd('失败', 'err')]) + '</div>' + K.gap(8) +
        '<div class="pad">' + K.chips(['{{user}}', '{{char}}', '{{cur_date}}'], { mono: true, add: '添加' }) + '</div>' + K.gap(16) +
        K.note('提示：行内说明。') + K.note('警告：需要你注意。', 'warn') + K.note('错误：没成功，原因写清楚。', 'err')
      );
    }
  });

  screen({
    id: 'spec-appbar', t: '应用栏的三种状态', src: '设计稿 §4.3 · UI-HANDOFF §5',
    note: ['收起：只有图标 + 「•••」，48 高，主命令靠右', '展开：标签出现在图标下，二级命令从上方弹出，背后压暗', '最小化：24 高，只有「•••」', '主命令最多 3 个——UWP 主命令不会自动溢流'],
    render: function () {
      return K.ph('规范样张 · 应用栏', 'CommandBar') + K.body(
        K.sec('收起（Compact）') + K.ab([{ ic: 'E710', t: '新建' }, { ic: 'E8B9', t: '导入' }, { ic: 'E721', t: '搜索' }]) +
        K.sec('最小化（Minimal）') + K.ab([], [], { min: true }) +
        K.sec('表单页') + K.ab([{ ic: 'E74E', t: '保存' }, { ic: 'E711', t: '取消' }]) +
        K.gap(16) + K.tx('最下面这条是展开态：点「•••」可以收起。', true)
      ) + K.ab([{ ic: 'E710', t: '新建会话' }, { ic: 'E8B9', t: '导入角色卡' }, { ic: 'E721', t: '搜索' }], ['批量管理', '已归档会话', '新建档案'], { open: true });
    }
  });

  screen({
    id: 'spec-dialog', t: '对话框与菜单', src: 'W10M ContentDialog / MenuFlyout',
    note: ['ContentDialog 贴顶全宽，按钮等分，主操作在左', 'MenuFlyout 全宽，紧贴触发位置；破坏性操作不染色，靠二次确认兜底'],
    render: function () {
      return K.ph('规范样张 · 弹层', '对话框') + K.body(K.tx('背景页面在遮罩之下。') + K.row({ ic: 'E70F', t: '某个设置项', s: '当前值' })) +
        K.dlg({ title: '删除这个会话？', body: K.tx('会话会移进回收区，30 天内可以恢复。'), btns: [{ t: '删除', acc: true }, '取消'] }) +
        K.fly([{ ic: 'E8C8', t: '复制' }, { ic: 'E70F', t: '编辑' }, { ic: 'E8AB', t: '从这里分支' }, '-', { ic: 'E74D', t: '删除' }], 330, { noDim: true });
    }
  });

  screen({
    id: 'spec-timeline', t: '时间线行类型', src: '设计稿 §5.4 · UI-HANDOFF §3.2',
    note: ['只有用户消息是气泡：靠右、强调色、直角、右下尾巴', '过程走左侧 2px 轨道：思考=圆点，工具=方块，失败转红', '运行中最多露 5 步，更早的并成「显示前面 N 步」（TimelineProjector.VisibleSteps）', '最终输出无气泡；标记行居中灰字；错误行红轨 + 重试'],
    render: function () {
      return K.chat('时间线样张', 'deepseek-chat') + K.body(T.wrap(
        T.user('用户消息：唯一的气泡。', { att: [T.att('需求.docx')] }) +
        T.think('独立思考行（无工具的一轮）：虚线圆圈 + 斜体，默认折叠两行…', { sub: '思考 6 秒 · 点开看全文' }) +
        T.rail([
          { k: 'more', t: '显示前面 3 步', sub: '思考 1 · 工具 2 · 读取 2 个文件' },
          { k: 'think', t: '先确认 JsonBridge 的反射路径', dur: '2s' },
          { k: 'tool', name: 'read_file', t: 'Core/Storage/JsonBridge.cs', dur: '0.3s' },
          { k: 'note', t: '中间解说：模型在轮中间说的话，降级成轨道节点。' },
          { k: 'sys', t: '已裁剪 4 条早期工具结果' },
          { k: 'tool', st: 'fail', name: 'fetch_page', t: 'github.com/…', sub: 'HTTP 404 · 0.8s' },
          { k: 'tool', st: 'run', name: 'list_files', t: 'files/', sub: '运行中…' }
        ]) +
        T.out('<p>最终输出：无气泡、正常亮色正文，Markdown 全量渲染。</p>') +
        T.marker('上下文已压缩 · 节省 31k tokens') +
        T.err('请求失败', 'HTTP 503 · api.deepseek.com · 第 2 次重试仍失败')
      )) + C.cmp({});
    }
  });

  /* ======================= 01 启动与根导航 ======================= */
  group('start', '启动与根导航', '启动 → 迁移自检 → 根 Pivot 三页（对话 / 档案 / 我的）。根 Pivot 底部 CommandBar 主命令固定 3 个。');

  screen({
    id: 'startup', t: '启动与初始化', src: '设计稿 §6.1', sb: '言枢 · 正在装载',
    note: ['延伸启动屏：与系统启动屏同一个标志位置，避免跳动', '一根五点进度 + 一行当前步骤；嵌入引擎在后台初始化，不阻塞进入', '状态栏同步显示进度（StatusBar.ProgressIndicator）'],
    render: function () {
      return '<div class="splash"><div class="logo">言</div></div>' +
        '<div style="flex:none;padding:0 24px 40px">' + K.dots() + '<div class="t-cap" style="margin-top:10px">正在装载工作区 · 4 个档案、39 个会话索引</div>' +
        '<div class="t-cap" style="color:var(--Faint)">下一步：检查旧数据 → 进入首页</div></div>';
    }
  });

  screen({
    id: 'startup-error', t: '初始化失败', src: '设计稿 §6.1 · UI-HANDOFF §5「不要静默吞异常」',
    note: ['绝不白屏：写清楚哪个文件、哪一行、为什么', '三条出路：重试 / 跳过这个档案继续 / 查看调试日志', '坏文件不会被覆盖，先挪到回收区'],
    render: function () {
      return K.ph('言枢', '没能装载工作区') + K.body(
        K.note('<b>engineering\\profile.json 解析失败</b><br>第 12 行附近：缺少右花括号。其他 3 个档案正常。', 'err') +
        K.tx('你可以先跳过这个档案进入应用，坏文件会原样保留在回收区，修好后再恢复。') +
        K.sec('可以做的') +
        K.row({ ic: 'E72C', t: '重试', s: '重新读取全部档案' }) +
        K.row({ ic: 'E8B7', t: '跳过这个档案继续', s: '移入 _trash\\2026-10-12T10-42，其余照常', go: 'home' }) +
        K.row({ ic: 'E9D9', t: '查看调试日志', s: 'diagnostics.log · 不含密钥与正文', go: 'debug' })
      ) + '<div class="foot">' + K.btn('跳过并继续', { acc: true, go: 'home' }) + K.btn('重试') + '</div>';
    }
  });

  screen({
    id: 'crash-notice', t: '上次异常退出', src: '设计稿 §4.5',
    note: ['启动时发现 crash.log 才弹一次', '可复制（贴到 issue）、可清除；内容不含密钥与正文'],
    render: function () {
      return homeRender() + K.dlg({
        title: '上次异常退出',
        body: K.tx('崩溃信息已写入 LocalState\\crash.log（2026-10-12 09:58，NullReferenceException）。不含密钥与正文。'),
        btns: [{ t: '复制并清除', acc: true }, '忽略']
      });
    }
  });

  screen({
    id: 'migration', t: '旧数据迁移', src: '设计稿 §6.1 迁移 · §6.14',
    note: ['只读旧数据，不删除；迁移前自动备份一份', '逐项勾选，每项写明去向', '也可以从「我的 → 导入与导出」再次进入'],
    render: function () {
      return K.ph('首次启动', '发现旧版言枢数据') + K.body(
        K.tx('在 LocalState 里找到 yanshuaiAppData.json（3.8 MB）与 memory_store.json。选择要带过来的内容：') +
        K.chk('模型档案 3 个', true, { s: '→ _shared\\api-profiles.json；密钥需要重新填写' }) +
        K.chk('会话 214 条', true, { s: '→ 新建档案「已导入」，按原时间排序' }) +
        K.chk('图片 38 张', true, { s: '→ 「已导入」档案的 files\\' }) +
        K.chk('长期记忆 512 条', true, { s: '→ 「已导入」档案的记忆池（向量需重建）' }) +
        K.chk('权限记录', false, { s: '旧格式是纯文本，建议重新授权' }) + K.gap(8) +
        K.note('只读取，不改动、不删除旧文件；迁移前会先打包一份备份到 exports\\。')
      ) + '<div class="foot">' + K.btn('开始迁移', { acc: true, go: 'migration-running' }) + K.btn('跳过', { go: 'home' }) + '</div>';
    }
  });

  screen({
    id: 'migration-running', t: '迁移进行中', src: '设计稿 §6.1', sb: '言枢 · 正在迁移',
    note: ['逐项显示完成 / 进行中 / 等待', '可以中途停止：已迁移部分保留，未迁移部分不动'],
    render: function () {
      return K.ph('首次启动', '正在迁移') + K.body(
        '<div class="pad">' + K.pbar(58) + '<div class="t-cap" style="margin:6px 0 14px">第 3 / 4 项 · 图片 22 / 38</div></div>' +
        K.row({ ic: 'E930', t: '模型档案', s: '3 个 · 完成', dense: true }) +
        K.row({ ic: 'E930', t: '会话', s: '214 条 · 完成', dense: true }) +
        K.row({ lead: K.ring(), t: '图片', s: '22 / 38 · 正在压缩到 1600px', dense: true }) +
        K.row({ ic: 'E823', icMid: true, t: '长期记忆', s: '等待', dense: true }) + K.gap(8) +
        K.note('备份已写入 exports\\legacy-2026-10-12.yanshuai-backup.zip', 'ok')
      ) + '<div class="foot">' + K.btn('停止') + '</div>';
    }
  });

  screen({
    id: 'oobe-1', t: '引导 1 / 3 · 连接模型', src: '设计稿 §10.4（引导合并为三步）',
    note: ['预设只填 URL 与提供商，用户只需要贴密钥', '「测试并继续」失败时就地报错，不跳页'],
    render: function () {
      return K.steps(3, 0) + K.ph('开始使用 · 1 / 3', '连接一个模型') + K.body(
        K.rgrp('服务', [{ t: 'DeepSeek', s: 'OpenAI 兼容 · api.deepseek.com' }, { t: 'OpenAI', s: 'api.openai.com' }, { t: 'Anthropic', s: '原生接口' }, { t: 'Gemini', s: '原生接口' }, { t: '自定义（OpenAI 兼容）', s: '中转站、LM Studio、Ollama…' }], 0) +
        K.tb('API 密钥', '••••••••••••••••', { icon: 'E890', help: '只存进系统凭据保险库（PasswordVault），配置文件里没有明文。' })
      ) + '<div class="foot">' + K.btn('测试并继续', { acc: true, go: 'oobe-2' }) + K.btn('以后再说', { go: 'oobe-2' }) + '</div>';
    }
  });

  screen({
    id: 'oobe-2', t: '引导 2 / 3 · 第一个档案', src: '设计稿 §10.4 · §3',
    note: ['档案 = 人设 + 配置 + 数据的文件夹', '选「角色扮演」直接跳角色卡导入向导'],
    render: function () {
      return K.steps(3, 1) + K.ph('开始使用 · 2 / 3', '建第一个档案') + K.body(
        K.rgrp('从哪里开始', [{ t: '通用助手', s: '不开工具，适合日常问答' }, { t: '工程助理', s: '开启搜索、读写档案文件、子代理' }, { t: '角色扮演', s: '导入一张 SillyTavern 角色卡（PNG / JSON）' }], 1) +
        K.tb('名称', '工程助理') +
        K.tb('文件夹名（slug）', 'engineering', { help: '创建后不改；重命名只改显示名。', mono: true })
      ) + '<div class="foot">' + K.btn('下一步', { acc: true, go: 'oobe-3' }) + K.btn('上一步', { go: 'oobe-1' }) + '</div>';
    }
  });

  screen({
    id: 'oobe-3', t: '引导 3 / 3 · 完成', src: '设计稿 §10.4',
    note: ['总结三件事，给一个主操作'],
    render: function () {
      return K.steps(3, 2) + K.ph('开始使用 · 3 / 3', '准备好了') + K.body(
        K.row({ ic: 'E930', t: 'DeepSeek', s: '连接正常 · 48 个模型 · 默认 deepseek-chat' }) +
        K.row({ ic: 'E930', t: '工程助理', s: '工具 7 个已开启 · 敏感操作每次都会问你' }) +
        K.row({ ic: 'E946', icMid: true, t: '工作区', s: 'LocalFolder\\workspace · 可在设置里换到 SD 卡' }) + K.gap(8) +
        K.tx('第一条消息发出之前，新会话只是草稿，不会留下空记录。', true)
      ) + '<div class="foot">' + K.btn('开始第一个会话', { acc: true, go: 'chat-draft' }) + '</div>';
    }
  });

  screen({
    id: 'home', t: '对话（根 Pivot 第 1 页）', src: '设计稿 §6.2 · §6.7 · UI-HANDOFF §1.2',
    note: ['顶部档案横条：点一个档案，下面就地筛成它的会话（不跳页）', '分组：需要你处理（永远最上，警告色组头）/ 今天 / 更早', '每行三行 + 4px 左色条；第三行是状态 + 档案 + token', 'CommandBar：新建会话 / 导入角色卡 / 搜索；其余进「•••」'],
    render: function () { return homeRender(); }
  });

  screen({
    id: 'home-filtered', t: '对话 · 按档案筛选', src: '设计稿 §6.2 交互',
    note: ['选中档案加强调色描边；再点一次取消筛选', '筛选后组头带上档案名与计数，「新建会话」默认用这个档案'],
    render: function () {
      return K.root(0) + K.body(profileStrip(1) +
        K.sec('工程助理 · 18 个会话', { right: K.lnk('档案详情', 'profile') }) +
        conv(CONV.warn) + conv(CONV.run) + conv(CONV.week) + conv(CONV.long) +
        conv({ t: 'MSBuild 找不到 Page 项', time: '10月8日', prev: '新增 XAML 页面必须往 csproj 加 <Page Include>…', state: 'done', p: '工程助理', tok: '1.9k' })
      ) + HOME_AB();
    }
  });

  screen({
    id: 'home-appbar', t: '对话 · 应用栏展开', src: '设计稿 §4.3',
    note: ['点「•••」：主命令露出标签，二级命令从上方弹出', '点遮罩或再点「•••」收起'],
    render: function () { return homeRender(true); }
  });

  screen({
    id: 'home-select', t: '批量管理会话', src: '设计稿 §6.7「长按进入多选」',
    note: ['长按任一会话进入；顶部换成「已选 N 项」', '底部操作：归档 / 移动到档案 / 删除（进回收区）', '返回键 = 退出多选'],
    render: function () {
      function c(o, on) { return '<div class="chk' + (on ? ' on' : '') + '" data-ck style="padding:8px 0 8px 12px;align-items:center"><i></i></div>'; }
      function r(o, on) { return '<div style="display:flex;align-items:stretch">' + c(o, on) + '<div style="flex:1;min-width:0">' + conv(Object.assign({}, o, { go: '' })) + '</div></div>'; }
      return '<div class="sel-hd"><b>已选 3 项</b>' + K.lnk('全选') + '</div>' + K.body(
        K.sec('今天') + r(CONV.run, false) + r(CONV.err, true) + r(CONV.grp, false) + r(CONV.lin, true) +
        K.sec('更早') + r(CONV.week, true) + r(CONV.long, false)
      ) + K.ab([{ ic: 'E8F1', t: '归档' }, { ic: 'E8DE', t: '移动', go: 'move-conversations' }, { ic: 'E74D', t: '删除' }]);
    }
  });

  screen({
    id: 'home-empty', t: '对话 · 一条会话都没有', src: '设计稿 §6.7 空态 · UI-HANDOFF §7',
    note: ['和「搜索无结果」文案不同', '给两条路：新建会话（主）/ 导入角色卡'],
    render: function () {
      return K.root(0) + K.body(profileStrip(0) + K.empty({
        ic: 'E8F2', t: '还没有会话', s: '选一个档案开始聊。第一条消息发出之前不会留下空记录。',
        btns: [K.btn('新建会话', { acc: true, go: 'new-chat' }), K.btn('导入角色卡', { go: 'import-source' })]
      }), 'col') + HOME_AB();
    }
  });

  screen({
    id: 'search', t: '全局搜索', src: '设计稿 §6.2 搜索', sip: true,
    note: ['点搜索后标题区换成搜索框（AutoSuggestBox），实时过滤', '结果分三组：档案 / 会话 / 记忆；命中词加粗', '返回键先收起搜索，再退出'],
    render: function () {
      function m(t) { return '<b style="font-weight:700">' + t + '</b>'; }
      return '<div style="flex:none;padding:10px 12px 6px">' + K.tb('', 'json', { focus: true, bare: true, icon: 'E721' }) + '</div>' + K.body(
        K.sec('档案 · 1') + K.row({ av: avOf(P.eng, 's'), t: '工程助理', s: '人设里提到 ' + m('JSON') + ' 持久化', dense: true, go: 'profile' }) +
        K.sec('会话 · 3') +
        K.row({ t: '排查列表永远为空', s: '…13.x 在 5.4.7 上 ' + m('Json') + 'SerializerSettings 一构造就抛…', rt: '10:38', go: 'chat-running' }) +
        K.row({ t: '三个人的方案评审', s: '我们准备把配置全部改成 ' + m('JSON') + ' 文件…', rt: '10:12', go: 'group-chat' }) +
        K.sec('记忆 · 2') + K.row({ ic: 'E8F1', icMid: true, t: '用户偏好用 Newtonsoft 10.0.3，不升级', s: '事实 · 工程助理', go: 'memory-edit' })
      );
    }
  });

  screen({
    id: 'search-empty', t: '搜索无结果', src: '设计稿 §6.7 · UI-HANDOFF §7',
    note: ['写明搜的是什么、搜了哪些范围', '给下一步：换关键词 / 去记忆页全文搜'],
    render: function () {
      return '<div style="flex:none;padding:10px 12px 6px">' + K.tb('', '量子纠缠', { bare: true, icon: 'E721' }) + '</div>' + K.body(
        '<div class="pad" style="padding-top:28px"><div class="t-sub">没有找到「量子纠缠」</div><div class="t-mid" style="margin-top:6px;font-size:15px">搜过 4 个档案名、39 个会话标题与预览、171 条记忆。</div></div>' + K.gap(16) +
        K.row({ ic: 'E721', t: '在会话正文里搜', s: '会逐个打开会话，稍慢' }) +
        K.row({ ic: 'E8F1', t: '去记忆页搜', s: '支持关键词与语义检索', go: 'memory' })
      );
    }
  });

  screen({
    id: 'profiles', t: '档案（根 Pivot 第 2 页）', src: '设计稿 §6.2 · UI-HANDOFF §1.2',
    note: ['标签筛选是文字条（选中=强调色字），横向滚动', '第一行永远是「新建档案」', '副标题：模型 · 会话数 · 记忆数；头像无图时用首字'],
    render: function () {
      return K.root(1) + K.flt(['全部', '工程', '角色', '翻译', '未分组'], 0) + K.body(
        K.row({ lead: '<span class="av n" style="background:transparent;border:2px dashed var(--ControlBorder);color:var(--Mid)">' + ic('E710') + '</span>', t: '新建档案', s: '空白、模板，或从角色卡导入', go: 'profile-new' }) +
        K.row({ av: avOf(P.eng), t: P.eng.n, s: P.eng.s, go: 'profile' }) +
        K.row({ av: avOf(P.lin), t: P.lin.n, s: P.lin.s, go: 'profile' }) +
        K.row({ av: avOf(P.tr), t: P.tr.n, s: P.tr.s, go: 'profile' }) +
        K.row({ av: avOf(P.loc), t: P.loc.n, s: P.loc.s, go: 'profile' })
      ) + K.ab([{ ic: 'E710', t: '新建档案', go: 'profile-new' }, { ic: 'E8B9', t: '导入角色卡', go: 'import-source' }, { ic: 'E721', t: '搜索', go: 'search' }], [{ t: '用户人设', go: 'user-personas' }, { t: '回收区', go: 'trash' }]);
    }
  });

  screen({
    id: 'profiles-empty', t: '档案 · 空态', src: '设计稿 §6.2 空状态',
    note: ['两个按钮：导入角色卡 / 新建档案'],
    render: function () {
      return K.root(1) + K.body(K.empty({
        ic: 'E77B', t: '还没有档案', s: '档案装着人设、模型配置、记忆与会话，一个档案一个文件夹。',
        btns: [K.btn('新建档案', { acc: true, go: 'profile-new' }), K.btn('导入角色卡', { go: 'import-source' })]
      }), 'col') + K.ab([{ ic: 'E710', t: '新建档案' }, { ic: 'E8B9', t: '导入角色卡' }]);
    }
  });

  screen({
    id: 'me', t: '我的（根 Pivot 第 3 页）', src: '设计稿 §4.1 · UI-HANDOFF §1.3（存储 / 备份 / 快捷消息已接入）',
    note: ['最上面是当前用户人设，点进去切换', '按「模型与能力 / 内容 / 外观与行为 / 数据 / 关于」分节，副标题写当前值', '整页可滚动'],
    render: function () {
      return K.root(2) + K.body(
        K.row({ av: K.av('欧', { n: true }), t: '欧阳', s: '当前用户人设 · 共 2 套', go: 'user-personas' }) +
        K.sec('模型与能力') +
        K.row({ ic: 'E774', t: '模型档案', s: '3 个 · 默认 DeepSeek', go: 'api-profiles' }) +
        K.row({ ic: 'E721', t: '联网搜索', s: 'duckduckgo · 5 条 · 正文 2000 字', go: 'search-settings' }) +
        K.row({ ic: 'E90F', t: '内置工具', s: '20 个 + run_python', go: 'tool-catalog' }) +
        K.row({ ic: 'E71B', t: 'MCP 服务器', s: '2 个 · 1 个连接失败', go: 'mcp-list' }) +
        K.row({ ic: 'E82F', t: '技能', s: '4 个', go: 'skills' }) +
        K.row({ ic: 'E8D7', t: '权限记录', s: '3 条「始终允许」', go: 'permissions' }) +
        K.sec('内容') +
        K.row({ ic: 'E8BD', t: '快捷消息', s: '6 条，显示在输入框上方', go: 'quick-messages' }) +
        K.row({ ic: 'E943', t: '正则转换器', s: '3 条规则 · 2 条启用', go: 'regex-list' }) +
        K.row({ ic: 'E82D', t: '世界书', s: '2 本 · 31 个条目', go: 'lorebooks' }) +
        K.row({ ic: 'E8FD', t: '模式注入', s: '4 条', go: 'injections' }) +
        K.row({ ic: 'E8F1', t: '全局记忆池', s: '18 条 · 2 个档案在用', go: 'memory-global' }) +
        K.sec('外观与行为') +
        K.row({ ic: 'E771', t: '主题与强调色', s: '深色 · 跟随系统强调色', go: 'appearance' }) +
        K.row({ ic: 'E8D2', t: '文字与可读性', s: '标准字号 · 引号着色开', go: 'typography' }) +
        K.row({ ic: 'E8F2', t: '默认会话外观', s: '无背景 · 气泡跟随强调色', go: 'default-appearance' }) +
        K.row({ ic: 'E713', t: '通用', s: '回车发送 · 启动回到上次会话', go: 'general' }) +
        K.sec('数据') +
        K.row({ ic: 'E8B7', t: '存储空间', s: '86.4 MB · 可清理 21.3 MB', go: 'storage' }) +
        K.row({ ic: 'E898', t: '备份与恢复', s: '上次备份：10月10日', go: 'backup' }) +
        K.row({ ic: 'E896', t: '导入与导出', s: '角色卡、会话、档案、旧版数据', go: 'data' }) +
        K.row({ ic: 'E74D', t: '回收区', s: '2 项', go: 'trash' }) +
        K.row({ ic: 'E8B7', t: '工作区位置', s: 'LocalFolder\\workspace', go: 'workspace' }) +
        K.sec('关于') +
        K.row({ ic: 'E9D9', t: '调试日志', s: '不含密钥与正文，可复制', go: 'debug' }) +
        K.row({ ic: 'E946', t: '关于言枢', s: '言枢 · 消息 1.2.0.0', go: 'about' })
      ) + K.ab([{ ic: 'E710', t: '新建会话', go: 'new-chat' }, { ic: 'E8B9', t: '导入角色卡', go: 'import-source' }, { ic: 'E721', t: '搜索', go: 'search' }]);
    }
  });
})();
