/* 10 设置 · 快捷消息 · 正则 · 11 数据与备份 · 12 关于与诊断 */
(function () {
  'use strict';
  var ic = K.ic;

  /* ======================= 10 设置与内容规则 ======================= */
  group('settings', '设置与内容规则', '通用、主题与强调色、文字可读性、工作区位置；快捷消息与正则转换器的管理页。设置项改了立即生效。');

  screen({
    id: 'general', t: '通用设置', src: '设计稿 §4.1 通用设置 · GlobalSettings',
    note: ['回车行为、启动行为、提示音、历史分页大小'],
    render: function () {
      return K.ph('我的', '通用') + K.body(
        K.rgrp('回车键', [{ t: '发送', s: '换行用 Shift + 回车' }, { t: '换行', s: '点发送按钮才发送' }], 0) +
        K.rgrp('启动时', ['回到上次的会话', '打开对话列表'], 0) +
        K.tog('回复完成时提示音', false, { s: '应用在后台时才响' }) +
        K.sl('每次加载历史消息', 20, '20 条', { min: '10', max: '100' }) +
        K.row({ ic: 'E8B7', t: '工作区位置', s: 'LocalFolder\\workspace', go: 'workspace' }) +
        K.row({ ic: 'E8D7', t: '授权目录', s: '2 个', go: 'authorized-folders' })
      );
    }
  });

  screen({
    id: 'appearance', t: '主题与强调色', src: '设计稿 §5.2 · UI-HANDOFF §9 浅色主题 · GlobalSettings.theme / followSystemAccent',
    note: ['主题：深色 / 浅色 / 跟随系统', '强调色默认跟随系统；开着时色块网格变灰不可点，关掉后用 W10M 同款色块网格自选（查看器里可直接拨开关验证）', '画廊顶部的「主题 / 系统强调色」可以直接验证每一屏'],
    render: function () {
      return K.ph('我的', '主题与强调色') + K.body(
        K.rgrp('主题', ['深色', '浅色', '跟随系统'], 0) +
        K.tog('跟随系统强调色', true, { s: '关掉后从下面选一个；档案还可以单独「跟随角色卡」', ctl: 'acc' }) +
        K.swg(K.ACCENTS, 16, { dis: true, ctlt: 'acc' }) +
        '<div class="pad">' + K.btns([K.btn('预览中的主按钮', { acc: true })], 'left').replace('class="btns left"', 'class="btns left" style="padding:0"') + '</div>'
      );
    }
  });

  screen({
    id: 'default-appearance', t: '默认会话外观', src: '设计稿 §3.4 外观 · 旧 DefaultAppearancePage',
    note: ['新档案默认用这里；档案、会话可以逐级覆盖'],
    render: function () {
      return K.ph('我的', '默认会话外观') + K.body(
        K.rgrp('背景', ['无', '纯色', '图片'], 0) +
        '<div class="fld-hd" style="padding:0 12px">用户气泡</div>' + K.swg(['none', '#0078D7', '#E81123', '#10893E', '#8764B8', '#CA5010', '#00B7C3', '#5D5A58'], 0, { s8: true }) +
        '<div class="fld-hd" style="padding:0 12px">引号内文字「…」</div>' + K.swg(['none', '#DCA03C', '#FFB900', '#FF8C00', '#E3008C', '#00B7C3', '#8E8CD8', '#BDBDBD'], 1, { s8: true }) +
        '<div class="fld-hd" style="padding:0 12px">括号内文字（…）</div>' + K.swg(['none', '#969696', '#7A7574', '#68768A', '#8764B8', '#2D7D9A', '#BDBDBD', '#666666'], 1, { s8: true }) +
        K.note('优先级：会话覆盖 → 档案外观 → 这里的默认。')
      );
    }
  });

  screen({
    id: 'typography', t: '文字与可读性', src: '设计稿 §5.3 · UI-HANDOFF §7 对比度',
    note: ['字号四档；下方实时预览', '引号 / 括号着色开关', '不提供自定义字体（包体积）'],
    render: function () {
      return K.ph('我的', '文字与可读性') + K.body(
        K.sl('字号', 33, '标准', { min: '小', max: '特大' }) +
        '<div class="preview-bub">' + T.user('预览：这是一条用户消息。') + T.out('<p>这是模型输出，<span class="q">「引号里的话」</span>和<span class="br">（括号里的补充）</span>单独着色。</p>') + '</div>' +
        K.tog('引号与括号着色', true) +
        K.tog('代码块自动换行', false, { s: '关闭时横向滚动' }) +
        K.note('只用系统字体（Segoe UI / Microsoft YaHei UI / Consolas）。自定义字体以后作为可选项挂载。')
      );
    }
  });

  screen({
    id: 'workspace', t: '工作区位置', src: '设计稿 §3.3 · §13.2-2',
    note: ['写清每个位置的代价：卸载会不会丢数据、授权流程', '移动是复制 + 校验 + 切换，失败不动原数据'],
    render: function () {
      return K.ph('我的', '工作区位置') + K.body(
        K.kv([['当前', 'LocalFolder\\workspace', 1], ['大小', '86.4 MB · 4 个档案']]) +
        K.rgrp('放在', [{ t: '应用内（默认）', s: '最快；卸载应用会一起删除' }, { t: 'SD 卡', s: '卸载后仍保留；第一次需要授权' }, { t: '文档库', s: '可以用文件管理器直接看' }], 0) +
        K.note('移动会先复制并校验，成功后才切换；失败时原数据不动。', 'info') +
        K.btns([K.btn('移动工作区…')], 'left') +
        K.sec('维护') + K.row({ ic: 'E895', t: '重建会话索引', s: '列表显示不对时用，不改正文', go: 'reset-index' })
      );
    }
  });

  screen({
    id: 'quick-messages', t: '快捷消息', src: 'UI-HANDOFF §1.3 快捷消息管理页 · QuickMessages.Visible / Sort',
    note: ['顶部是快捷栏实时预览', '拖动柄排序；隐藏的条目变灰但保留', '点快捷消息只填进输入框，不直接发'],
    render: function () {
      return K.ph('我的', '快捷消息') + '<div style="flex:none">' + C.quick(K.QUICK).replace('class="qbar"', 'class="qbar" style="padding-bottom:8px"') + '</div>' + K.body(
        K.sec('6 条 · 拖动排序') +
        K.row({ hdl: true, t: '继续', s: '继续', go: 'quick-edit' }) +
        K.row({ hdl: true, t: '展开说说', s: '展开详细说说。', go: 'quick-edit' }) +
        K.row({ hdl: true, t: '更简短', s: '把上面的回答压缩到一半长度，保留关键信息。', go: 'quick-edit' }) +
        K.row({ hdl: true, t: '翻译成英文', s: '把上面这段翻译成英语，代码与专有名词保持原样。', go: 'quick-edit' }) +
        K.row({ hdl: true, t: '改写成代码', s: '把上面的需求写成可运行的代码。', go: 'quick-edit' }) +
        K.row({ hdl: true, t: '问候 {char}', s: '已隐藏 · 早上好，{char}，今天是 {cur_date}。', dis: true, go: 'quick-edit' })
      ) + K.ab([{ ic: 'E710', t: '新增', go: 'quick-new' }], ['恢复默认']);
    }
  });

  screen({
    id: 'quick-edit', t: '编辑快捷消息', src: 'QuickMessages.Expand · Placeholders',
    note: ['内容支持占位符，下方实时显示展开结果', '认不出的占位符原样保留（看得见）'],
    render: function () {
      return K.ph('快捷消息', '问候 {char}') + K.body(
        K.tb('标题', '问候 {char}', { help: '显示在快捷栏上的字' }) +
        K.tb('内容', '早上好，{char}，今天是 {cur_date}。{weather}', { multi: true }) +
        '<div class="fld" style="margin-top:-6px">' + K.chips(['{char}', '{user}', '{cur_date}', '{cur_time}', '{cur_datetime}'], { mono: true }) + '</div>' +
        K.sec('展开后') + '<div class="preview-bub" style="font-size:15px">早上好，林，今天是 2026-10-12。<span class="t-warn">{weather}</span></div>' +
        K.note('<span class="mono">{weather}</span> 不是已知占位符，会原样保留。', 'warn') +
        K.tog('显示在快捷栏', false)
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'quick-messages' }, { ic: 'E711', t: '取消', go: 'quick-messages' }], ['删除']);
    }
  });

  screen({
    id: 'quick-new', t: '新增快捷消息', src: 'QuickMessages.NextSort',
    note: ['新条目排在最后'],
    render: function () {
      return K.ph('快捷消息', '新增') + K.body(K.tb('标题', '', { ph: '快捷栏上显示的字' }) + K.tb('内容', '', { ph: '点了以后填进输入框的内容', multi: true }) +
        '<div class="fld" style="margin-top:-6px">' + K.chips(['{char}', '{user}', '{cur_date}', '{cur_time}'], { mono: true }) + '</div>' + K.tog('显示在快捷栏', true)) +
        K.ab([{ ic: 'E74E', t: '保存', dis: true }, { ic: 'E711', t: '取消', go: 'quick-messages' }]);
    }
  });

  screen({
    id: 'regex-list', t: '正则转换器', src: 'UI-HANDOFF §1.2「正则转换器的编辑页（没有入口）」· RegexTransform',
    note: ['按顺序执行，拖动柄排序', '徽标：作用范围（用户 / 模型 / 两者）· 仅显示', '写错的规则会被跳过，不影响对话'],
    render: function () {
      function r(n, find, bds, o) { return K.row({ hdl: true, t: n, s: '<span class="mono">' + find + '</span>', l3: bds, go: 'regex-edit', dis: o }); }
      return K.ph('我的', '正则转换器') + K.body(
        K.tx('发出去之前、显示之前按顺序替换文字。', true) +
        r('去掉动作描写', '\\*[^*]+\\*', K.bd('模型') + K.bd('仅显示', 'acc')) +
        r('统一称呼', '(老师|大佬)', K.bd('两者')) +
        r('隐藏签名', '^—— 由 .+ 生成$', K.bd('模型') + K.bd('已停用', 'line'), true)
      ) + K.ab([{ ic: 'E710', t: '新增', go: 'regex-new' }, { ic: 'E768', t: '测试', go: 'regex-test' }]);
    }
  });

  screen({
    id: 'regex-edit', t: '编辑正则规则', src: 'RegexTransform：find / replace / scope / visualOnly / enabled',
    note: ['查找 / 替换用等宽；替换支持 $1 $2', '「只改显示」：发给模型的内容不变', '有超时保护，写出灾难性回溯也不会卡死'],
    render: function () {
      return K.ph('正则转换器', '去掉动作描写') + K.body(
        K.tb('名称', '去掉动作描写') +
        K.tb('查找', '\\*([^*]+)\\*', { mono: true, help: '.NET 正则语法' }) +
        K.tb('替换成', '', { mono: true, ph: '留空 = 删除；可用 $1', help: '' }) +
        K.rgrp('作用于', ['用户消息', '模型输出', '两者'], 1) +
        K.tog('只改显示', true, { s: '发给模型的内容保持原样，只影响你看到的' }) +
        K.tog('启用', true)
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'regex-list' }, { ic: 'E768', t: '测试', go: 'regex-test' }], ['删除']);
    }
  });

  screen({
    id: 'regex-new', t: '新增正则规则 · 校验失败', src: 'RegexTransformer.Validate',
    note: ['保存前校验；错误写在字段下面，保存按钮变灰'],
    render: function () {
      return K.ph('正则转换器', '新增规则') + K.body(
        K.tb('名称', '统一引号') +
        K.tb('查找', '("|“)(.+?', { mono: true, err: '语法错误：第 9 个字符处缺少右括号 )' }) +
        K.tb('替换成', '「$2」', { mono: true }) +
        K.rgrp('作用于', ['用户消息', '模型输出', '两者'], 2) +
        K.tog('只改显示', false) + K.tog('启用', true)
      ) + K.ab([{ ic: 'E74E', t: '保存', dis: true }, { ic: 'E711', t: '取消', go: 'regex-list' }]);
    }
  });

  screen({
    id: 'regex-test', t: '正则测试', src: 'RegexTransformer.ApplyVisual / ApplyOutgoing',
    note: ['同一段输入，分别显示「你看到的」和「发给模型的」', '列出每条规则命中次数'],
    render: function () {
      return K.ph('正则转换器', '测试') + K.body(
        K.rgrp('当作', ['用户消息', '模型输出'], 1) +
        K.tb('输入', '*她笑了笑* 好啊，老师。', { multi: true }) +
        K.sec('你看到的') + '<div class="preview-bub">好啊，你。</div>' +
        K.sec('发给模型的') + '<div class="preview-bub">*她笑了笑* 好啊，你。</div>' +
        K.sec('命中') + K.kv([['去掉动作描写', '1 次 · 仅显示'], ['统一称呼', '1 次'], ['隐藏签名', '已停用']])
      );
    }
  });

  /* ======================= 11 数据与备份 ======================= */
  group('data', '数据与备份', '存储空间按可清理维度分类（StorageStats）；备份是 zip、二进制安全、恢复走事务式（先全部校验再提交，并先做安全备份）。');

  screen({
    id: 'storage', t: '存储空间', src: 'UI-HANDOFF §1.3 存储空间页 · StorageStats 分类',
    note: ['大数字 + 分段条（W10M「存储感知」同款）', '每类写大小与能不能清理；可清理的带徽标', '按档案再看一遍'],
    render: function () {
      function b(n, size, cl, hint) { return K.row({ t: n, rt: size, rtMid: true, s: hint, l3: cl ? K.bd('可清理', 'acc') : '', dense: true, go: cl ? 'storage-clean' : '' }); }
      return K.ph('我的', '存储空间') + K.body(
        '<div class="pad"><span class="t-subhead">86.4</span><span class="t-mid" style="margin-left:6px">MB · 可清理 21.3 MB</span></div>' + K.gap(8) +
        K.seg([['会话', 32, '#0078D7'], ['附件', 30, '#00B7C3'], ['记忆', 9, '#8764B8'], ['备份', 16, '#CA5010'], ['日志', 3, '#7A7574'], ['回收区', 6, '#E81123'], ['其他', 4, '#5D5A58']]) + K.gap(8) +
        b('会话记录', '27.9 MB', 0, '39 个会话') + b('附件与图片', '25.6 MB', 0, '可在附件页逐个删除') + b('记忆', '7.8 MB', 0, '含向量 · 可在记忆页清理') +
        b('备份与导出', '14.2 MB', 1, '3 个备份 · 保留最新一个即可') + b('运行日志', '2.1 MB', 1, 'diagnostics.log + 用量日志') + b('回收区', '5.0 MB', 1, '2 项') + b('其他', '3.8 MB', 0, '') +
        K.sec('按档案') + K.row({ av: avOf(P.lin, 's'), t: '林', rt: '38.1 MB', rtMid: true, dense: true }) + K.row({ av: avOf(P.eng, 's'), t: '工程助理', rt: '31.0 MB', rtMid: true, dense: true })
      ) + K.ab([{ ic: 'E74D', t: '清理', go: 'storage-clean' }, { ic: 'E72C', t: '重新统计' }]);
    }
  });

  screen({
    id: 'storage-clean', t: '清理空间确认', src: 'StorageBucket.Cleanable / CleanupHint',
    note: ['只列可清理的类别，可逐项取消', '按钮写实际释放量'],
    render: function () {
      return K.ph('存储空间', '清理') + K.body(
        K.chk('备份与导出 · 14.2 MB', true, { s: '保留最新一个备份（10月10日）' }) +
        K.chk('运行日志 · 2.1 MB', true, { s: '会先轮换，保留今天的' }) +
        K.chk('回收区 · 5.0 MB', false, { s: '清空后无法恢复' }) + K.gap(8) +
        K.note('会话、附件与记忆不在这里清理，请到各自的页面逐个处理。')
      ) + '<div class="foot">' + K.btn('清理 16.3 MB', { acc: true, go: 'storage' }) + K.btn('取消', { go: 'storage' }) + '</div>';
    }
  });

  screen({
    id: 'trash', t: '回收区', src: '设计稿 §3.3 _trash',
    note: ['写剩余天数；点进去恢复或永久删除'],
    render: function () {
      return K.ph('我的', '回收区') + K.body(
        K.row({ ic: 'E77B', t: '本地研究员', s: '档案 · 2 个会话、6 个文件 · 还剩 29 天', go: 'trash-detail' }) +
        K.row({ ic: 'E8F2', t: '试试 Gemini 原生接口', s: '会话 · 工程助理 · 还剩 12 天', go: 'trash-detail' })
      ) + K.ab([{ ic: 'E74D', t: '清空回收区' }]);
    }
  });

  screen({
    id: 'trash-detail', t: '回收项详情', src: '设计稿 §3.3',
    note: ['恢复到原位置；原位置被占用时自动改名'],
    render: function () {
      return K.ph('回收区', '本地研究员') + K.body(
        K.kv([['类型', '档案'], ['原位置', 'workspace\\researcher\\', 1], ['删除于', '10月11日 22:10'], ['内容', '2 个会话 · 6 个文件 · 记忆池'], ['大小', '4.6 MB']])
      ) + '<div class="foot">' + K.btn('恢复', { acc: true, go: 'profiles' }) + K.btn('永久删除') + '</div>';
    }
  });

  screen({
    id: 'backup', t: '备份与恢复', src: 'UI-HANDOFF §1.3 备份与恢复页 · BackupService',
    note: ['两个主入口：创建 / 恢复', '写明密钥不进备份', '列出最近的备份'],
    render: function () {
      return K.ph('我的', '备份与恢复') + K.body(
        K.row({ ic: 'E898', t: '创建备份', s: '打包成 zip 保存到设备', go: 'backup-create' }) +
        K.row({ ic: 'E896', t: '从备份恢复', s: '选一个 .yanshuai-backup.zip', go: 'backup-select' }) +
        K.note('API 密钥不会进备份。恢复后到「模型档案」重新填一次。', 'info', 'E72E') +
        K.sec('最近的备份') +
        K.row({ ic: 'E8B7', t: 'yanshuai-2026-10-10.yanshuai-backup.zip', s: '12.8 MB · 4 个档案 · 37 个会话', go: 'backup-preview' }) +
        K.row({ ic: 'E8B7', t: 'yanshuai-2026-10-01.yanshuai-backup.zip', s: '9.1 MB · 3 个档案', go: 'backup-preview' })
      );
    }
  });

  screen({
    id: 'backup-create', t: '创建备份', src: 'BackupService · BackupManifest',
    note: ['范围：整个工作区 / 选择档案', '附件可选；日志默认不带。按钮提交前的选项用 CheckBox', '文件名带日期，扩展名 .yanshuai-backup.zip'],
    render: function () {
      return K.ph('备份与恢复', '创建备份') + K.body(
        K.rgrp('范围', ['整个工作区', '选择档案'], 0) +
        K.chk('工程助理', true, { dis: true }) + K.chk('林', true, { dis: true }) + K.chk('翻译官', true, { dis: true }) + K.chk('本地研究员', true, { dis: true }) +
        K.sec('内容 · 预计约 61 MB') + K.chk('包含附件与图片', true, { s: '约 25.6 MB' }) + K.chk('包含运行日志', false) +
        K.kv([['文件名', 'yanshuai-2026-10-12.yanshuai-backup.zip', 1]])
      ) + '<div class="foot">' + K.btn('选择位置并创建', { acc: true, go: 'backup-progress' }) + K.btn('取消', { go: 'backup' }) + '</div>';
    }
  });

  screen({
    id: 'backup-progress', t: '备份进行中', src: 'BackupService', sb: '言枢 · 正在备份',
    note: ['确定进度条 + 当前文件；状态栏同步', '可以停止：已写的半个 zip 会删掉'],
    render: function () {
      return K.ph('备份与恢复', '正在备份') + K.body(
        '<div class="pad">' + K.pbar(64) + '<div class="t-cap" style="margin-top:6px">412 / 640 个文件 · 39.1 / 61 MB</div>' +
        '<div class="t-cap ell" style="margin-top:2px">lin\\files\\bg-rain-02.png</div></div>' + K.gap(16) +
        K.tx('可以切出去做别的，备份会在后台继续。', true)
      ) + '<div class="foot">' + K.btn('停止', { go: 'backup' }) + '</div>';
    }
  });

  screen({
    id: 'backup-done', t: '备份完成', src: 'BackupResult：Path / ByteSize / FileCount / Warnings',
    note: ['写明位置、大小、文件数；有警告单列'],
    render: function () {
      return K.ph('备份与恢复', '备份完成') + K.body(
        K.note('已保存到 文档\\言枢备份\\yanshuai-2026-10-12.yanshuai-backup.zip', 'ok') +
        K.kv([['大小', '58.7 MB'], ['文件', '640 个'], ['档案', '4 个 · 39 个会话'], ['密钥', '不包含']]) +
        K.note('1 个文件被跳过：lin\\files\\tmp.lock（正在被占用）', 'warn')
      ) + '<div class="foot">' + K.btn('完成', { acc: true, go: 'backup' }) + K.btn('分享') + '</div>';
    }
  });

  screen({
    id: 'backup-select', t: '选择恢复文件', src: 'UwpFileStore.PickBackupFileAsync',
    note: ['先列出档案 exports\\ 与文档库里找到的备份', '其他位置走系统文件选择器'],
    render: function () {
      return K.picker('选择备份文件', [
        { t: 'yanshuai-2026-10-10', s: '12.8 MB · 文档\\言枢备份', go: 'backup-preview' },
        { t: 'yanshuai-2026-10-01', s: '9.1 MB · 文档\\言枢备份', go: 'backup-preview' },
        { t: 'legacy-2026-10-12', s: '3.9 MB · 迁移前自动备份', go: 'backup-preview' },
        { t: '从其他位置选择…', s: 'SD 卡、OneDrive', go: 'backup-preview' }]);
    }
  });

  screen({
    id: 'backup-preview', t: '备份检查与预览', src: 'BackupManifest：Version / CreatedAtUtc / AppVersion / DeviceInfo / ProfileCount / ConversationCount / ContainsSecrets',
    note: ['先读清单再决定；版本较新时提示', '和当前工作区比较：会覆盖什么'],
    render: function () {
      return K.ph('从备份恢复', 'yanshuai-2026-10-10') + K.body(
        K.kv([['创建于', '10月10日 21:03'], ['应用版本', '1.2.0.0'], ['设备', 'Lumia 950 · 10.0.15254'], ['内容', '4 个档案 · 37 个会话 · 512 个文件'], ['密钥', '不包含']]) +
        K.sec('和现在相比') +
        K.row({ ic: 'E70F', t: '会被覆盖', s: '工程助理、林、翻译官（备份里的版本较旧）', dense: true }) +
        K.row({ ic: 'E710', t: '会恢复回来', s: '本地研究员（现在在回收区）', dense: true }) +
        K.row({ ic: 'E74D', t: '会丢失', s: '10月10日之后的 2 个新会话', dense: true }) +
        K.note('恢复前会自动给当前工作区做一份安全备份。', 'info')
      ) + '<div class="foot">' + K.btn('恢复', { acc: true, go: 'restore-confirm' }) + K.btn('取消', { go: 'backup' }) + '</div>';
    }
  });

  screen({
    id: 'restore-confirm', t: '恢复二次确认', src: '设计稿 §6.14「覆盖导入必须二次确认」',
    note: ['写清会丢什么、会先做安全备份'],
    render: function () {
      return K.ph('从备份恢复', 'yanshuai-2026-10-10') + K.body(K.kv([['内容', '4 个档案 · 37 个会话']])) +
        K.dlg({ title: '用这个备份覆盖当前工作区？', body: K.tx('10月10日之后的 2 个新会话会丢失。恢复前会先把现在的工作区备份到 exports\\safety-2026-10-12.zip。'), btns: [{ t: '恢复', acc: true, go: 'restore-result' }, '取消'] });
    }
  });

  screen({
    id: 'restore-result', t: '恢复完成', src: 'RestoreResult：RestoredFiles / SafetyBackupPath / Warnings',
    note: ['写明安全备份位置；需要重新填密钥'],
    render: function () {
      return K.ph('从备份恢复', '恢复完成') + K.body(
        K.note('已恢复 512 个文件。', 'ok') +
        K.kv([['安全备份', 'exports\\safety-2026-10-12.zip', 1], ['密钥', '3 个模型档案需要重新填写']]) +
        K.row({ ic: 'E774', t: '去填密钥', s: '模型档案', go: 'api-profiles' })
      ) + '<div class="foot">' + K.btn('重新载入', { acc: true, go: 'startup' }) + '</div>';
    }
  });

  screen({
    id: 'data', t: '导入与导出', src: '设计稿 §6.14 · §10.4「给 ImportExportPage 一个真实入口」',
    note: ['导出三档：单会话 / 单档案 / 全工作区', '导入四类：角色卡 / 旧版言枢 / zip 备份 / 会话 JSON'],
    render: function () {
      return K.ph('我的', '导入与导出') + K.body(
        K.sec('导出') +
        K.row({ ic: 'E8F2', t: '导出一个会话', s: '在会话设置里也能进', go: 'conversation-export' }) +
        K.row({ ic: 'E77B', t: '导出一个档案', s: '整个文件夹打包 zip', go: 'data-export' }) +
        K.row({ ic: 'E8B7', t: '导出全部', s: '等同于创建备份', go: 'backup-create' }) +
        K.sec('导入') +
        K.row({ ic: 'E8B9', t: '角色卡', s: 'PNG / JSON', go: 'import-source' }) +
        K.row({ ic: 'E81C', t: '旧版言枢数据', s: 'yanshuaiAppData.json / memory_store.json', go: 'data-import-preview' }) +
        K.row({ ic: 'E896', t: 'zip 备份', s: '.yanshuai-backup.zip', go: 'backup-select' })
      );
    }
  });

  screen({
    id: 'data-export', t: '导出档案', src: '设计稿 §6.14',
    note: ['选档案、选内容；角色卡档案可以回导成 PNG / JSON'],
    render: function () {
      return K.ph('导入与导出', '导出档案') + K.body(
        K.cb('档案', '林') +
        K.chk('人设与设置（profile.json）', true, { dis: true }) + K.chk('会话（6 个）', true) + K.chk('记忆池（126 条）', true) + K.chk('文件（14 个）', false) +
        K.rgrp('角色卡格式', [{ t: '一起导出 card.png', s: '内嵌 spec v3 JSON，可直接导回 SillyTavern' }, '只导 card.json', '不导出角色卡'], 0)
      ) + '<div class="foot">' + K.btn('导出', { acc: true }) + K.btn('取消', { go: 'data' }) + '</div>';
    }
  });

  screen({
    id: 'data-import-preview', t: '旧版数据检查', src: '设计稿 §6.1 迁移 · §6.14 导入',
    note: ['从「我的」再次进入迁移；已经迁移过的项标出来，避免重复'],
    render: function () {
      return K.ph('导入与导出', '旧版言枢数据') + K.body(
        K.kv([['文件', 'yanshuaiAppData.json · 3.8 MB'], ['上次迁移', '10月12日 · 已导入 214 个会话']]) +
        K.chk('模型档案 3 个', false, { s: '已存在同名档案，跳过', dis: true }) +
        K.chk('会话 214 条', false, { s: '已导入过；再导会产生重复', dis: false }) +
        K.chk('长期记忆 512 条', true, { s: '上次没导，现在导入' }) + K.gap(8) +
        K.note('只读取旧文件，不改动、不删除。')
      ) + '<div class="foot">' + K.btn('导入 1 项', { acc: true, go: 'migration-running' }) + K.btn('取消', { go: 'data' }) + '</div>';
    }
  });

  /* ======================= 12 关于与诊断 ======================= */
  group('about', '关于与诊断', '版本信息与独立的调试日志页（此前是弹窗）。日志与崩溃记录都不含密钥与正文，可以放心复制。');

  screen({
    id: 'about', t: '关于言枢', src: '设计稿 §6.15',
    note: ['版本、包族名、设备与系统、运行时', '复制诊断信息一键贴到 issue'],
    render: function () {
      return K.ph('我的', '关于') + K.body(
        '<div class="hero"><div class="logo" style="width:72px;height:72px;font-size:36px">言</div><div class="c"><b>言枢 · 消息</b><small>1.2.0.0 · ARM · .NET Native</small></div></div>' +
        K.kv([['包族名', 'com.yanshuai.messages_amaw0954g0pkg', 1], ['设备', 'Lumia 950 · 10.0.15254.603'], ['脚本运行时', 'PyUwp 3.15'], ['数据', '全部在本机，不经过任何中转']]) +
        K.row({ ic: 'E8C8', t: '复制诊断信息', s: '版本 + 设备 + 最近 50 行日志，不含密钥' }) +
        K.row({ ic: 'E9D9', t: '调试日志', go: 'debug' }) +
        K.row({ ic: 'E8A5', t: '开源许可', s: 'Newtonsoft.Json 10.0.3 · CPython' })
      );
    }
  });

  screen({
    id: 'debug', t: '调试日志', src: 'UI-HANDOFF §1.2「独立的调试日志页」· DiagnosticLog',
    note: ['按类别筛选；等宽小字，警告 / 错误着色', '记录：UTC 时间、阶段、HTTP 方法 / 路径 / 状态 / 耗时、异常类型与 HRESULT', '不记录：密钥、正文、URL query、异常 message', '维护动作放二级命令'],
    render: function () {
      return K.ph('我的', '调试日志') + K.flt(['全部', 'HTTP', '存储', '错误'], 0) + K.body(
        '<div class="logl">' +
        '<span class="t">02:38:11Z</span> chat   POST /v1/chat/completions 200 6.2s\n' +
        '<span class="t">02:38:05Z</span> tool   read_file ok 0.2s\n' +
        '<span class="t">02:30:40Z</span> <span class="e">chat   POST /v1/chat/completions 503 1.1s retry=2</span>\n' +
        '<span class="t">02:20:12Z</span> <span class="w">chat   POST …:generateContent 429 0.4s</span>\n' +
        '<span class="t">02:14:55Z</span> store  index.json saved 39 entries 12ms\n' +
        '<span class="t">02:14:55Z</span> memory extract +1 (fact) 1.8s\n' +
        '<span class="t">01:58:02Z</span> <span class="e">app    crash NullReferenceException 0x80004003</span>\n' +
        '<span class="t">01:58:01Z</span> store  JsonBridge parse ok 4 profiles\n' +
        '<span class="t">01:57:59Z</span> app    launch 1.2.0.0 ARM 15254' +
        '</div>'
      ) + K.ab([{ ic: 'E8C8', t: '复制' }, { ic: 'E72D', t: '导出' }, { ic: 'E74D', t: '清除' }], [{ t: '查看 crash.log', go: 'crash-log' }, { t: '清空权限记录', go: 'permissions' }, { t: '重建会话索引', go: 'reset-index' }]);
    }
  });

  screen({
    id: 'crash-log', t: '崩溃记录', src: '设计稿 §4.5 crash.log',
    note: ['只有异常类型、HRESULT 与调用栈，没有正文'],
    render: function () {
      return K.ph('调试日志', 'crash.log') + K.body(
        '<div class="logl"><span class="t">2026-10-12 09:58:02</span>\n<span class="e">System.NullReferenceException 0x80004003</span>\n  at Ui.MainPage.RefreshQuickBarAsync()\n  at Ui.MainPage.OnNavigatedTo(…)\nversion 1.2.0.0 ARM · 15254</div>'
      ) + K.ab([{ ic: 'E8C8', t: '复制' }, { ic: 'E74D', t: '清除', go: 'debug' }]);
    }
  });

  screen({
    id: 'reset-index', t: '重建会话索引', src: '设计稿 §6.15 维护动作',
    note: ['写明不改正文；耗时取决于会话数'],
    render: function () {
      return K.ph('我的', '调试日志') + K.body('<div class="logl">…</div>') +
        K.dlg({ title: '重建会话索引？', body: K.tx('会重新扫描 39 个会话文件生成列表索引，不改动会话正文。大约需要几秒。'), btns: [{ t: '重建', acc: true, go: 'home' }, '取消'] });
    }
  });
})();
