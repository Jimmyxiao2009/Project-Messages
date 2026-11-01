/* 02 会话与执行 · 03 审批 · 追问 · 群聊 */
(function () {
  'use strict';
  var ic = K.ic;

  var Q = K.QUICK;
  var USER1 = T.user('列表永远是空的，diagnostics.log 里一直报「解析失败」。帮我看看 JsonBridge 到底哪里不对。', { att: [T.att('diagnostics.log', 'E9D9')] });
  var DONE_RAIL = T.rail([
    { k: 'more', t: '显示前面 6 步', sub: '思考 2 · 工具 4 · 读取 3 个文件' },
    { k: 'tool', name: 'read_file', t: 'Core/Storage/JsonBridge.cs', dur: '0.2s' }
  ]);
  var DONE_OUT = T.out(
    '<h4>原因</h4><p>不是 JsonBridge 的映射写错了，而是它在 .NET Native 下<b>拿不到属性</b>：rd.xml 只声明了 <code>Profile</code>，没有覆盖到 <code>ConversationIndexEntry</code>，反射返回空集合，于是整张索引被当成空表。</p>' +
    '<ul class="tasks"><li class="task on"><i></i><span>确认 x86 Debug 正常、ARM Release 为空</span></li><li class="task on"><i></i><span>定位到 <code>GetRuntimeProperties()</code> 返回 0 个</span></li><li class="task"><i></i><span>给 rd.xml 补齐全部模型类型</span></li></ul>' +
    T.code('xml · Default.rd.xml', '&lt;<span class="kw">Type</span> Name=<span class="s">"Yanshuai.Messages.Core.ConversationIndexEntry"</span>\n      Dynamic=<span class="s">"Required All"</span> /&gt;') +
    T.table(['构建', '索引条目', '结果'], [['x86 Debug', '39', '正常'], ['ARM Release', '0', '列表为空'], ['ARM + 补 rd.xml', '39', '正常']]) +
    '<p>补完之后跑一次 <code>SeedData --verify</code>，再上真机看 <code>diagnostics.log</code> 里还有没有「解析失败」。</p>'
  );

  function chatBottom(o) {
    o = o || {};
    return (o.branch ? C.branch(o.branch[0], o.branch[1]) : '') + (o.extra || '') + C.quick(Q) + C.cmp(o.cmp || {});
  }

  /* ======================= 02 会话与执行 ======================= */
  group('chat', '会话与执行', '会话页从上到下：顶栏 → 待处理条 → 时间线（虚拟化 ListView）→ 分支条 / 运行条 / 朗读条 → 快捷栏 → 输入行。会话页没有 CommandBar，所有动作在顶栏「⋯」里。');

  screen({
    id: 'new-chat', t: '新建会话（草稿设置）', src: '设计稿 §4.4 · §6.5「先选设置、后建会话」',
    note: ['先选档案 / 模型 / 可选标题，得到草稿；第一条消息发出才落盘', '模型可以只对这个会话覆盖', '加成员即变群聊'],
    render: function () {
      return K.ph('新建会话', '先选好再开始') + K.body(
        K.sec('档案') + K.row({ av: avOf(P.eng), t: P.eng.n, s: '点这里换档案', go: 'profile-select' }) +
        K.sec('模型') + K.row({ ic: 'E99A', t: 'deepseek-chat', s: 'DeepSeek · 64K · 工具 · 档案默认', go: 'model-picker' }) +
        K.sec('可选') + K.tb('标题', '', { ph: '留空，发第一条消息后自动生成' }) +
        K.row({ ic: 'E716', t: '添加成员，变成群聊', s: '再选 1 个以上档案', go: 'group-add' }) + K.gap(8) +
        K.note('草稿不保存：没发消息就返回会直接丢弃，不留空会话。')
      ) + '<div class="foot">' + K.btn('开始', { acc: true, go: 'chat-draft' }) + K.btn('取消', { go: 'home' }) + '</div>';
    }
  });

  screen({
    id: 'profile-select', t: '选择档案', src: '设计稿 §4.4 · W10M ListPickerFlyout',
    note: ['全屏选择页：20px 大字条目，选中项强调色字', '点一下即选中并返回'],
    render: function () {
      return K.picker('选择档案', [
        { t: P.eng.n, s: P.eng.s, av: avOf(P.eng), on: true, go: 'new-chat' },
        { t: P.lin.n, s: P.lin.s, av: avOf(P.lin), go: 'new-chat' },
        { t: P.tr.n, s: P.tr.s, av: avOf(P.tr), go: 'new-chat' },
        { t: P.loc.n, s: P.loc.s, av: avOf(P.loc), go: 'new-chat' }]);
    }
  });

  screen({
    id: 'chat-draft', t: '会话 · 草稿空态', src: '设计稿 §4.4 · §6.5 状态',
    note: ['草稿态禁用附件、追加、打断、会话设置', '有首句的档案（角色卡）首句直接作为第一条输出显示', '快捷栏照常可用'],
    render: function () {
      return K.chat('新会话', '草稿 · 工程助理 · deepseek-chat', { more: '' }) +
        C.notice('草稿', '发出第一条消息后才会保存；现在返回会直接丢弃', '', { kind: 'info', go: '' }) +
        K.body('<div class="pad" style="padding-top:28px">' + avOf(P.eng, 'l') + '<div class="t-title" style="margin-top:14px">工程助理</div>' +
          '<div class="t-mid" style="font-size:15px;margin-top:4px;line-height:1.45">会用工具：联网搜索、读写这个档案的文件、子代理。敏感操作每次都会先问你。</div></div>' + K.gap(16) +
          K.row({ ic: 'E721', t: '查一下 UWP 5.4.7 能用哪个版本的 Newtonsoft', dense: true }) +
          K.row({ ic: 'E8A5', t: '读一下 files/ 里的设计稿，列出缺的页面', dense: true }), 'col') +
        C.quick(Q) + C.cmp({ noAttach: true, ph: '发消息开始这个会话…' });
    }
  });

  screen({
    id: 'chat', t: '会话 · 完成', src: '设计稿 §5.4 · §6.5 · UI-HANDOFF §3.2',
    note: ['已完成的一轮：轨道收起成「显示前面 N 步」+ 最后一步，最终输出是视觉主角', '输出 Markdown：标题 / 任务列表 / 代码块（复制、保存）/ 表格横向滚动', '记忆抽取给一条轻提示：「已记住 1 条」', '分支条在快捷栏上方：候选 2 / 3'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat · 11.8k') + K.body(T.wrap(
        USER1 + DONE_RAIL + DONE_OUT + T.marker(ic('E8F1') + '已记住 1 条新记忆 · ' + K.lnk('查看', 'memory'))
      )) + chatBottom({ branch: [2, 3], cmp: {} });
    }
  });

  screen({
    id: 'chat-markdown', t: '会话 · Markdown 与公式', src: 'UI-HANDOFF §9「Markdown / 公式渲染」· 设计稿 §10.2 MarkdownBlock + LatexRenderer',
    note: ['标题、有序 / 嵌套列表、引用、链接、行内代码', '公式走 LatexRenderer 的二维排版（真分数线），失败降级为斜体原文', '表格斑马纹 + 横向滚动；代码块四色高亮 + 复制 / 保存', '流式期间先用纯文本，定稿后再切富文本'],
    render: function () {
      return K.chat('上下文压缩怎么估算', '工程助理 · deepseek-chat') + K.body(T.wrap(
        T.user('压缩阈值是怎么算的？顺便给个公式。') +
        T.out('<h4>估算方法</h4><ol><li>CJK 字符按 1.5 token 计<ul><li>标点也算 CJK</li></ul></li><li>其他字符按 0.25 token 计</li></ol>' +
          '<p>当估算值超过窗口的 80% 时触发压缩：</p>' +
          '<div class="math"><span>r</span><span>=</span><span class="fr"><span>1.5·n<sub>cjk</sub> + 0.25·n<sub>other</sub></span><span>W</span></span><span>&gt;</span><span>0.8</span></div>' +
          '<blockquote>保留最近 4 条不动，少于 6 条不压缩。</blockquote>' +
          T.table(['窗口 W', '阈值', '约合中文字'], [['64K', '51.2K', '3.4 万'], ['128K', '102.4K', '6.8 万'], ['1M', '800K', '53 万']]) +
          T.code('csharp · TokenEstimator.cs', '<span class="kw">public static int</span> Estimate(<span class="kw">string</span> text)\n{\n    <span class="kw">double</span> n = <span class="n">0</span>; <span class="cm">// CJK 1.5，其他 0.25</span>\n    <span class="kw">foreach</span> (<span class="kw">var</span> ch <span class="kw">in</span> text) n += IsCjk(ch) ? <span class="n">1.5</span> : <span class="n">0.25</span>;\n    <span class="kw">return</span> (<span class="kw">int</span>)Math.Ceiling(n);\n}') +
          '<p>详见 <a>docs/设计稿.md §8</a>，常量在 <code>Limits.CompactThreshold</code>。</p>')
      )) + C.quick(Q) + C.cmp({});
    }
  });

  screen({
    id: 'chat-menu', t: '会话 · 「⋯」菜单', src: '设计稿 §4.3（会话页主命令 0 个）· UI-HANDOFF §1.3「会话设置里的四个动作」',
    note: ['全宽 MenuFlyout，紧贴顶栏下方', '高频在上：会话设置 / 附件 / 压缩 / 朗读 / 翻译 / 用量', '删除在最下，二次确认后进回收区'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat · 11.8k') + K.body(T.wrap(USER1 + DONE_RAIL + DONE_OUT)) + chatBottom({ cmp: {} }) +
        K.fly([
          { ic: 'E713', t: '会话设置', go: 'chat-settings' },
          { ic: 'E723', t: '附件与文件', s: '2', go: 'attachments' },
          { ic: 'E8C1', t: '翻译最后一条', go: 'chat-translated' },
          { ic: 'E767', t: '朗读最后一条', go: 'chat-speaking' },
          { ic: 'E8CB', t: '压缩上下文', s: '已用 34%', go: 'context' },
          { ic: 'E9D9', t: '会话用量', go: 'usage' },
          '-',
          { ic: 'E718', t: '置顶' }, { ic: 'E72D', t: '导出', go: 'conversation-export' },
          { ic: 'E74D', t: '删除', go: 'chat-delete' }
        ], 48);
    }
  });

  screen({
    id: 'chat-running', t: '会话 · 运行中', src: '设计稿 §5.4.2 · §6.5 · UI-HANDOFF §3.2-2/5', sb: '言枢 · 正在生成',
    note: ['顶栏副标题换成呼吸点 + 「运行中」；状态栏显示后台进度', '运行中最多露最近 5 步，更早的并成「显示前面 3 步」并给分类计数', '运行条在输入框上方：进度环 + 当前步骤 + 打断（永远点得到）', '发送按钮原位换成「追加」，占位符变「追加到当前任务」', '离开页面不取消生成'],
    render: function () {
      return K.chat('排查列表永远为空', '运行中 · 第 8 步', { state: 'running' }) + K.body(T.wrap(
        USER1 +
        T.rail([
          { k: 'more', t: '显示前面 3 步', sub: '思考 1 · 工具 2 · 读取 2 个文件' },
          { k: 'think', t: '列表为空但没有异常，说明解析「成功」返回了空集合…', dur: '3s' },
          { k: 'tool', name: 'read_file', t: 'Core/Storage/JsonBridge.cs', dur: '0.2s' },
          { k: 'note', t: '映射层走 GetRuntimeProperties()，.NET Native 下需要 rd.xml。' },
          { k: 'tool', name: 'list_files', t: 'Properties/', dur: '0.1s' },
          { k: 'tool', st: 'run', name: 'read_file', t: 'Properties/Default.rd.xml', sub: '运行中 · 1.4s' }
        ])
      )) + C.run('第 8 步 · 正在执行 read_file') + C.quick(Q) + C.cmp({ mode: 'steer', ph: '追加到当前任务…' });
    }
  });

  screen({
    id: 'chat-approval', t: '会话 · 待审批', src: '设计稿 §5.4.3 · UI-HANDOFF §3.2-4', sb: '言枢 · 等待你批准',
    note: ['审批不进时间线：固定在顶部的 NoticeBanner（警告色）', '轨道上对应节点标成「等待批准」', '运行条文字变「等待你批准」，打断仍可用', '点固定条进全屏审批页，处理完自动回到原位'],
    render: function () {
      return K.chat('整理周报并写入 notes', '等待你批准', { state: 'running' }) +
        C.notice('需要你批准 · 写入文件', 'notes/weekly-41.md · 新建 · 2.3 KB', '处理') +
        K.body(T.wrap(
          T.user('把这周的提交记录整理成周报，写进 notes/weekly-41.md。') +
          T.rail([
            { k: 'more', t: '显示前面 2 步', sub: '思考 1 · 工具 1' },
            { k: 'tool', name: 'list_files', t: 'notes/', dur: '0.1s' },
            { k: 'note', t: '周报草稿已经写好，共 5 节，下面写入文件。' },
            { k: 'appr', name: 'write_file', t: 'notes/weekly-41.md', sub: '等待你批准' }
          ])
        )) + C.strip(ic('E7BA', 'lead'), '等待你批准 · 写入文件', [C.sb('打断', { stop: true, icon: 'E71A' })]) + C.quick(Q) + C.cmp({ mode: 'steer', ph: '追加到当前任务…' });
    }
  });

  screen({
    id: 'chat-error', t: '会话 · 请求失败', src: '设计稿 §5.4 错误行',
    note: ['本轮有失败：轨道展开、转红', '错误行写清 HTTP 状态与主机名，给「重试」（复用原 clientMessageId）与「查看详情」', '已产出的内容保留'],
    render: function () {
      return K.chat('翻译 1.2 发布说明', '翻译官 · gemini-2.5-flash') + K.body(T.wrap(
        T.user('把 CHANGELOG 的 1.2 节翻成英文，术语表见附件。', { att: [T.att('CHANGELOG.md'), T.att('glossary.xlsx', 'E8A5')] }) +
        T.rail([
          { k: 'tool', name: 'read_file', t: 'files/glossary.xlsx', sub: '解析为 2 张表 · 86 行', dur: '0.6s' },
          { k: 'tool', st: 'fail', name: '模型请求', t: 'generativelanguage.googleapis.com', sub: 'HTTP 429 · 第 3 次重试仍失败' }
        ], { fail: true }) +
        T.err('请求失败', 'HTTP 429 · 请求过于频繁 · 建议 60 秒后重试', { detail: 'RESOURCE_EXHAUSTED\nretry-after: 58s\nturn=2 model=gemini-2.5-flash' })
      )) + C.quick(Q) + C.cmp({});
    }
  });

  screen({
    id: 'chat-offline', t: '会话 · 离线', src: '设计稿 §6.5 状态',
    note: ['顶栏副标题显示离线', '发送禁用并写明原因；输入内容作为草稿保留'],
    render: function () {
      return K.chat('周报怎么写', '离线 · 没有网络连接', { state: 'offline' }) + K.body(T.wrap(
        T.user('帮我把这段话改得更像周报一点。') +
        T.out('<p>可以按「做了什么 / 结果 / 下周」三段写，每段不超过三行。下面是改好的版本：</p><blockquote>本周完成 JSON 持久化的修复，ARM 包列表恢复正常；下周上真机验证 PyUwp。</blockquote>')
      )) + C.quick(Q) + C.cmp({ mode: 'off', val: '再短一点', hint: '没有网络。联网后再发送，输入内容会保留。', hintWarn: true });
    }
  });

  screen({
    id: 'chat-context-limit', t: '会话 · 上下文将满', src: '设计稿 §6.5「额度 / 上下文超限」',
    note: ['输入框上方一行警告条 + 「压缩」快捷入口', '阈值 80%（Limits.CompactThreshold）'],
    render: function () {
      return K.chat('一个很长的会话（用来验证滚动）', '工程助理 · deepseek-chat · 55.0k') + K.body(T.wrap(
        T.out('<p>第 12 个回答：先确认输入，再确认输出，最后看中间有没有可以省掉的一步。</p>', { mid: false }) +
        T.user('那第 13 步呢？') +
        T.out('<p>第 13 步是把前面 12 步的结论合成一张检查表，贴在 PR 描述里。</p>')
      )) + C.strip(ic('E7BA', 'lead'), '上下文已用 86%（55k / 64k）', [C.sb('压缩', { go: 'context-preview' })], { kind: 'warn' }) + C.quick(Q) + C.cmp({});
    }
  });

  screen({
    id: 'chat-roleplay', t: '会话 · 角色对话', src: '设计稿 §3.4 外观（背景 + 引号 / 括号着色）',
    note: ['角色卡立绘作为背景，按 backgroundOpacity 压暗', '引号「…」与括号（…）内的文字单独着色', '用户气泡仍然是唯一气泡'],
    render: function () {
      return K.chat('雨夜的车站', '林 · claude-sonnet-4.5') +
        '<div class="body" style="position:relative"><div class="bgimg"></div><div class="bgdim" style="opacity:.62"></div><div class="onbg">' + T.wrap(
          T.out('<p><span class="br">（候车室的灯管闪了两下，她抬头看见你，把湿了的发梢别到耳后）</span></p><p><span class="q">「你又忘带伞了吧。」</span></p>') +
          T.user('（推门进来，抖了抖外套）还在等末班车？') +
          T.out('<p><span class="q">「嗯，晚点了二十分钟。」</span><span class="br">（她往长椅一侧挪了挪，把伞立在你们中间）</span><span class="q">「坐吧，反正一时半会儿也走不了。」</span></p><p>窗外的雨声更大了。</p>')
        ) + '</div></div>' + C.quick(['继续', '（沉默）', '描写环境']) + C.cmp({ ph: '以你的身份说话…' });
    }
  });

  screen({
    id: 'chat-branches', t: '会话 · 候选分支', src: 'UI-HANDOFF §1.3 分支切换条 · Conversation.VariantCount / SelectVariant',
    note: ['分支条：「候选 N / M」+ 左右切换 + 重新生成；到头的箭头变灰', '切换只换最后一轮的输出，不重建整页', '重新生成 = 往候选池追加一个（AddVariant）'],
    render: function () {
      return K.chat('周报怎么写', '工程助理 · deepseek-chat') + K.body(T.wrap(
        T.user('给周报起个标题，要短。') +
        T.out('<p>三个方向：</p><ol><li>《第 41 周：JSON 修好了》</li><li>《列表回来了》</li><li>《rd.xml 补齐周》</li></ol>') +
        T.marker('候选 3 / 3 · 刚刚生成')
      )) + chatBottom({ branch: [3, 3], cmp: {} });
    }
  });

  screen({
    id: 'chat-speaking', t: '会话 · 朗读', src: 'UI-HANDOFF §1.3 朗读条 · SpeechText 切段',
    note: ['朗读条：当前段 / 总段数 + 停止', '正在读的段落淡强调底', '离开会话页自动停止（StopSpeechAsync）'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat') + K.body(T.wrap(
        T.out('<p>不是 JsonBridge 的映射写错了，而是它在 .NET Native 下拿不到属性。</p><p><span class="hl">rd.xml 只声明了 Profile，没有覆盖到 ConversationIndexEntry，反射返回空集合，于是整张索引被当成空表。</span></p><p>补完之后跑一次 SeedData --verify。</p>')
      )) + C.speak('正在朗读 · 第 2 / 5 段 · 代码块已跳过') + C.quick(Q) + C.cmp({});
    }
  });

  screen({
    id: 'chat-translated', t: '会话 · 译文叠加', src: 'UI-HANDOFF §1.3 消息翻译 · TranslationService',
    note: ['译文叠加在原文段落下方，左侧柔强调色细线', '代码块不翻译（保护）', '按语言缓存，再次切换不重新请求'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat') + K.body(T.wrap(
        T.out('<p>不是映射写错了，而是 .NET Native 下拿不到属性。</p><div class="tr"><small>English · 已缓存</small>The mapping is fine — under .NET Native the reflection simply returns no properties.</div>' +
          '<p>补齐 rd.xml 之后再跑一次校验。</p><div class="tr"><small>English</small>Complete rd.xml, then run the verification again.</div>' +
          T.code('powershell', 'dotnet run --project tools\\SeedData -- "$ws" --verify') +
          '<div class="sig"><span>' + K.lnk('只看原文') + '</span><span>' + K.lnk('换语言', 'translation') + '</span></div>')
      )) + C.quick(Q) + C.cmp({});
    }
  });

  screen({
    id: 'chat-keyboard', t: '会话 · 软键盘弹出', src: '设计稿 §6.5 软键盘 · UI-HANDOFF §5 / §7', sip: true,
    note: ['整页上移：顶栏、最后一条输出、快捷栏、输入框都还在', '输入框聚焦 = 白底黑字；最高 140px 后内部滚动', '快捷消息点一下只是填进输入框'],
    render: function () {
      return K.chat('三个人的方案评审', '群聊 · 3 位 · 轮流发言', { more: 'chat-menu' }) + K.body(T.wrap(
        T.out('<p>那用户改坏配置之后，界面上会看到什么？如果只是白屏，他们不会知道是自己的文件写错了。</p>')
      )) + C.quick(Q) + C.cmp({ val: '我们准备把配置全部改成 JSON 文件，方案', focus: true });
    }
  });

  screen({
    id: 'chat-compose-attach', t: '会话 · 带附件待发送', src: '设计稿 §6.5 附件行 · §6.13',
    note: ['附件行在输入框上方：缩略图 / 文件芯片，各带删除', '下方一行说明压缩与视觉能力（模型不支持视觉时明确写降级）', '上限 10 个、单个 25 MB'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat') + K.body(T.wrap(USER1 + DONE_RAIL + DONE_OUT)) + C.quick(Q) +
        C.cmp({ val: '这两张是真机截图，再看看', att: ['<span class="thumb" style="width:56px;height:56px"><span class="x">&#xE711;</span></span>', '<span class="thumb b" style="width:56px;height:56px"><span class="x">&#xE711;</span></span>', T.att('rd.xml', 'E943')],
          hint: '图片会压缩到 1600px · deepseek-chat 不支持视觉，只会发送文件名 · ' + '<span class="lnk" data-go="attachment-preview">详情</span>', hintWarn: true });
    }
  });

  screen({
    id: 'chat-history', t: '会话 · 长会话与回到底部', src: '设计稿 §4.5 · §5.4 标记行 · §13.1 自动跟随',
    note: ['顶部「加载更早的 20 条」（historyPageSize）', '压缩过的区间显示一条标记', '离开底部超过一屏出现「回到底部」，带新消息数', '用户上滑后锁定位置，不自动跟随'],
    render: function () {
      return K.chat('一个很长的会话（用来验证滚动）', '工程助理 · deepseek-chat · 41.2k') + '<div class="body">' + T.wrap(
        T.marker(K.lnk('加载更早的 20 条')) +
        T.marker('上下文已压缩 · 第 1–42 条 → 摘要 · ' + K.lnk('看摘要', 'context')) +
        T.user('第 3 步为什么要先看输出？') +
        T.out('<p>因为输出是唯一能被用户直接验证的东西。先把「对」的样子钉住，再倒推输入，中间可以省掉的步骤自然就露出来了。</p>') +
        T.user('一个不换行的路径：H:\\项目\\UWP\\Project-Messages\\src\\Yanshuai.Messages\\Core\\Storage\\JsonBridge.cs')
      ) + '</div>' + C.fab(186, 2) + C.quick(Q) + C.cmp({});
    }
  });

  screen({
    id: 'message-actions', t: '长按 · 用户消息', src: '设计稿 §5.4 用户消息触控行为 · UI-HANDOFF §9 长按菜单',
    note: ['被长按的气泡加描边，菜单全宽贴在它下面', '复制 / 编辑 / 从这里分支 / 朗读 / 翻译 / 删除'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat') + K.body(T.wrap(
        USER1.replace('class="u"', 'class="u hlu"') + DONE_RAIL
      )) + C.quick(Q) + C.cmp({}) +
        K.fly([{ ic: 'E8C8', t: '复制' }, { ic: 'E70F', t: '编辑', go: 'message-edit' }, { ic: 'E8AB', t: '从这里分支', go: 'fork' }, { ic: 'E767', t: '朗读' }, { ic: 'E8C1', t: '翻译' }, '-', { ic: 'E74D', t: '删除' }], 168);
    }
  });

  screen({
    id: 'message-actions-ai', t: '长按 · 模型输出', src: '设计稿 §5.4 最终输出 · UI-HANDOFF §9',
    note: ['输出没有气泡，长按整段', '重新生成 = 新增候选；从这里分支 = 复制到这条为止开新会话'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat') + K.body(T.wrap(USER1 + DONE_RAIL + DONE_OUT.replace('class="out"', 'class="out" style="background:var(--ChromeLow)"'))) + C.quick(Q) + C.cmp({}) +
        K.fly([{ ic: 'E8C8', t: '复制' }, { ic: 'E8C8', t: '复制为 Markdown' }, { ic: 'E72C', t: '重新生成', s: '候选 +1' }, { ic: 'E8AB', t: '从这里分支', go: 'fork' }, { ic: 'E767', t: '朗读', go: 'chat-speaking' }, { ic: 'E8C1', t: '翻译', go: 'chat-translated' }, '-', { ic: 'E74D', t: '删除' }], 250);
    }
  });

  screen({
    id: 'message-edit', t: '编辑消息', src: '设计稿 §5.4 · §6.5 软键盘对策 2', sip: true,
    note: ['全屏编辑；键盘弹出时「保存」在标题行，不会被挡', '保存后从这条之后重新生成，原回答保留为候选'],
    render: function () {
      return K.ph('编辑消息 · 排查列表永远为空', '你的消息', K.hb('取消', { go: 'chat' }) + K.hb('保存', { acc: true, go: 'chat' })) + K.body(
        K.tb('', '列表永远是空的，diagnostics.log 里一直报「解析失败」。帮我看看 JsonBridge 和 rd.xml 到底哪里不对', { focus: true, tall: true }) +
        K.tx('保存后会从这条消息之后重新生成；原来的回答保留为候选 1。', true));
    }
  });

  screen({
    id: 'fork', t: '从这里分支', src: 'Conversation.ForkAt · 标题去重',
    note: ['复制到这一条为止的消息，开一个新会话', '标题自动去重：「…（分支 2）」'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat') + K.body(T.wrap(USER1 + DONE_RAIL)) + C.cmp({}) +
        K.dlg({
          title: '从这里分支',
          body: K.tb('新会话标题', '排查列表永远为空（分支 2）') + K.chk('复制到这一条为止的 3 条消息', true, { dis: true }) + K.chk('创建后直接打开', true) + K.gap(8),
          btns: [{ t: '创建', acc: true, go: 'chat' }, '取消']
        });
    }
  });

  screen({
    id: 'chat-landscape', t: '会话 · 横屏', src: 'UI-HANDOFF §7 旋转', land: true,
    note: ['横屏时系统栏在左、导航栏在右；时间线与输入区自适应宽度', '用户气泡最大宽度按屏宽比例，不写死'],
    render: function () {
      return K.chat('排查列表永远为空', '工程助理 · deepseek-chat') + K.body(T.wrap(USER1 + DONE_RAIL + T.out('<p>不是 JsonBridge 的映射写错了，而是它在 .NET Native 下拿不到属性。</p>'))) + C.cmp({});
    }
  });

  screen({
    id: 'chat-resumed', t: '会话 · 离开后切回', src: '设计稿 §4.5 返回键与生命周期 · UI-HANDOFF §4「生成中离开页面不取消」/ §7 挂起恢复', sb: '言枢 · 正在生成',
    note: ['离开会话页或应用被挂起都不取消生成：只解绑页面级回调，任务经 AppState.RegisterTask 继续', '切回时重新挂接，时间线直接停在最新一步；离开期间新增的步骤上方插一条标记行', '运行条、「追加」状态与离开前一致；挂起前已先落盘当前会话与记忆池', 'FullTrust 在重新进入会话时复位'],
    render: function () {
      return K.chat('排查列表永远为空', '运行中 · 第 11 步', { state: 'running' }) + K.body(T.wrap(
        USER1 +
        T.rail([
          { k: 'more', t: '显示前面 6 步', sub: '思考 2 · 工具 4 · 读取 3 个文件' },
          { k: 'tool', name: 'read_file', t: 'Properties/Default.rd.xml', dur: '0.2s' }
        ]) +
        T.marker('你离开的 2 分钟里完成了 3 步') +
        T.rail([
          { k: 'note', t: 'rd.xml 只声明了 Profile，缺 ConversationIndexEntry 等 11 个类型。' },
          { k: 'tool', name: 'list_files', t: 'Core/Models/', dur: '0.1s' },
          { k: 'tool', st: 'run', name: 'read_file', t: 'Core/Models/ConversationIndexEntry.cs', sub: '运行中 · 0.6s' }
        ])
      )) + C.run('第 11 步 · 正在执行 read_file') + C.quick(Q) + C.cmp({ mode: 'steer', ph: '追加到当前任务…' });
    }
  });

  /* ======================= 03 审批 · 追问 · 群聊 ======================= */
  group('approve', '审批 · 追问 · 群聊', '审批与追问都是全屏二级页（不是底部滑出面板），内容区自适应、底部按钮固定，软键盘弹出也够得着。群聊的成员、回复模式在 Core 里已经就绪。');

  screen({
    id: 'approval', t: '敏感工具审批 · 写入文件', src: '设计稿 §6.6 · UI-HANDOFF §4 白名单',
    note: ['标题写人话：「是否允许 写入文件」', '参数键值 + 内容预览（新增行绿色）', '允许一次 / 始终允许 / 拒绝；「始终允许」只记一个键，可在权限记录里撤销', 'FullTrust 每次进入会话复位'],
    render: function () {
      return K.ph('工程助理 · 需要你批准', '是否允许 写入文件') + K.body(
        K.tx('工程助理想在这个档案的文件夹里新建一个文件。') +
        K.kv([['工具', 'write_file', 1], ['路径', 'notes/weekly-41.md', 1], ['方式', '新建（不覆盖已有文件）'], ['大小', '2.3 KB · 48 行']]) +
        K.sec('内容预览') + '<div class="pre"><span class="add">+ # 第 41 周周报</span>\n<span class="add">+ ## 做了什么</span>\n<span class="add">+ - 修复 .NET Native 下 JSON 映射为空</span>\n<span class="add">+ - 补齐 rd.xml 全部模型类型</span>\n<span class="add">+ …共 48 行</span></div>' +
        K.note('「始终允许」只记 <span class="mono">write_file:notes/weekly-41.md</span> 这一条，可在「我的 → 权限记录」撤销。')
      ) + '<div class="foot">' + K.btn('允许一次', { acc: true, go: 'chat-running' }) + K.btn('始终允许', { go: 'chat-running' }) + K.btn('拒绝', { go: 'chat' }) + '</div>';
    }
  });

  screen({
    id: 'approval-python', t: '敏感工具审批 · 运行脚本', src: 'UI-HANDOFF §4 脚本双重门控 · §9.1 PyUwp',
    note: ['第一道门：档案里的脚本开关（已开）；第二道门：每次执行都审批', '显示脚本全文、超时与输出上限、运行时能力缺口'],
    render: function () {
      return K.ph('工程助理 · 需要你批准', '是否允许 运行 Python 脚本') + K.body(
        K.kv([['工具', 'run_python', 1], ['工作目录', 'files/', 1], ['超时', '30 秒 · 输出上限 20000 字']]) +
        '<div class="pre">import json, re\nrows = [json.loads(l) for l in open("usage.jsonl")]\nprint(sum(r["input"] for r in rows))</div>' +
        K.note('运行时：<b>PyUwp 3.15（ARM）</b>。没有 _socket / _ssl / zipfile / _sqlite3：脚本不能联网、解压或用数据库。', 'info', 'E943') +
        K.note('档案「工程助理」已开启脚本，这是第二道门：每次执行都会问你。', 'warn')
      ) + '<div class="foot">' + K.btn('允许一次', { acc: true }) + K.btn('拒绝') + '</div>';
    }
  });

  screen({
    id: 'approval-device', t: '敏感工具审批 · 发短信', src: '设计稿 §9.1 send_sms / make_call',
    note: ['设备类工具只打开系统应用，用户仍需自己按发送', '号码与正文完整展示，不截断'],
    render: function () {
      return K.ph('工程助理 · 需要你批准', '是否允许 发送短信') + K.body(
        K.kv([['工具', 'send_sms', 1], ['收件人', '王工 · +86 138 0000 0000'], ['内容', '明早 9 点的评审改到 10 点，会议室不变。']]) +
        K.note('会打开系统短信应用并填好内容，你仍需自己按下发送。', 'info', 'E8BD')
      ) + '<div class="foot">' + K.btn('允许一次', { acc: true }) + K.btn('始终允许') + K.btn('拒绝') + '</div>';
    }
  });

  screen({
    id: 'ask-user', t: '向你追问', src: '设计稿 §6.6 追问 · UI-HANDOFF §9 ask_user',
    note: ['一次最多 4 题；每题 2–4 个选项 + 「其他（自己填）」', '多选题标「可多选」', '答完一次性提交；软键盘弹出时提交按钮仍固定在底部'],
    render: function () {
      return K.ph('工程助理 · 2 个问题', '先确认两件事') + K.body(
        K.sec('1 / 2 · 周报发给谁看') + K.rgrp('', ['只给自己留档', '发给组里', '发给上级（要更正式）'], 1) +
        K.sec('2 / 2 · 要包含哪些（可多选）') + K.chk('本周提交', true) + K.chk('遇到的问题', true) + K.chk('下周计划', false) + K.chk('其他', false) +
        K.tb('', '', { ph: '其他：自己填' })
      ) + '<div class="foot">' + K.btn('提交', { acc: true, go: 'chat-running' }) + K.btn('跳过') + '</div>';
    }
  });

  screen({
    id: 'schema-form', t: '结构化输入', src: '设计稿 §6.6 SchemaForm',
    note: ['JSON Schema → 原生控件：文本、日期、时间、开关', '逐字段报错，不整页报错'],
    render: function () {
      return K.ph('工程助理 · 需要填写', '新建日程') + K.body(
        K.tb('标题', '方案评审') +
        '<div class="fld"><div class="fld-hd">日期</div><div class="dp"><span>2026</span><span>10月</span><span>14日 周三</span></div></div>' +
        '<div class="fld"><div class="fld-hd">时间</div><div class="dp"><span>10</span><span>00</span></div></div>' +
        K.tb('地点', '', { ph: '必填', err: '地点不能为空' }) +
        K.tog('提前 15 分钟提醒', true)
      ) + '<div class="foot">' + K.btn('提交', { acc: true }) + K.btn('取消') + '</div>';
    }
  });

  screen({
    id: 'group-chat', t: '群聊会话', src: 'UI-HANDOFF §1.2 群聊 · GroupChat.DecideSpeakers',
    note: ['每段输出上方一行发言人（小头像 + 名字 + 为什么轮到他）', '仍然只有用户消息是气泡', '占位符提示可以 @ 点名'],
    render: function () {
      var xs = function (c, img) { return K.av(c, { size: 'xs', img: img }); };
      return K.chat('三个人的方案评审', '群聊 · 3 位 · 轮流发言', { more: 'group-members' }) + K.body(T.wrap(
        T.user('我们准备把配置全部改成 JSON 文件，大家怎么看？') +
        T.spk('老周', xs('周'), '<span>轮到 老周</span>') + T.out('<p>风险在于没有 schema 校验时，一个手抖的逗号就会让应用起不来。建议至少加一层加载时校验，失败就退回上一份可用配置。</p>') +
        T.spk('小满', xs('满'), '<span>轮到 小满</span>') + T.out('<p>同意。补一句：配置的默认值要写在代码里，而不是写在文件里——否则以后加字段时，老配置文件会缺字段。</p>') +
        T.spk('林', xs('林', 1), '<span>轮到 林</span>') + T.out('<p>那用户改坏配置之后，界面上会看到什么？如果只是白屏，他们不会知道是自己的文件写错了。</p>')
      )) + C.quick(['@老周', '@小满', '@林', '继续']) + C.cmp({ ph: '发消息，或 @某人只让他回复…' });
    }
  });

  screen({
    id: 'group-members', t: '群聊成员', src: 'UI-HANDOFF §1.2 · Conversation.Members / GroupMember',
    note: ['回复模式放最上（决定谁说话）', '成员行：拖动柄排顺序（轮流模式按 Order）· 头像 · 群内名字 · 来源档案', '停用的成员保留在列表里但变灰'],
    render: function () {
      return K.ph('群聊 · 三个人的方案评审', '成员') + K.body(
        K.row({ ic: 'E8AB', t: '回复模式', s: '轮流发言', go: 'group-mode' }) +
        K.sec('成员 · 3 位启用') +
        K.row({ hdl: true, av: K.av('周', { size: 's' }), t: '老周', s: '档案：工程助理 · 有首句', go: 'group-member-edit' }) +
        K.row({ hdl: true, av: K.av('满', { size: 's' }), t: '小满', s: '档案：本地研究员', go: 'group-member-edit' }) +
        K.row({ hdl: true, av: K.av('林', { size: 's', img: 1 }), t: '林', s: '档案：林', go: 'group-member-edit' }) +
        K.row({ hdl: true, av: K.av('译', { size: 's' }), t: '翻译官', s: '已停用', dis: true, go: 'group-member-edit' })
      ) + K.ab([{ ic: 'E710', t: '添加成员', go: 'group-add' }, { ic: 'E74E', t: '保存', go: 'group-chat' }]);
    }
  });

  screen({
    id: 'group-add', t: '添加群聊成员', src: 'GroupMember.profileId',
    note: ['多选全屏选择页；已在群里的显示「已加入」', '同一个档案可以以不同名字加入两次'],
    render: function () {
      return K.picker('添加成员 · 可多选', [
        { t: P.eng.n, s: '已加入（老周）', av: avOf(P.eng), multi: true },
        { t: P.lin.n, s: '已加入', av: avOf(P.lin), multi: true },
        { t: P.tr.n, s: P.tr.m, av: avOf(P.tr), multi: false },
        { t: P.loc.n, s: '已加入（小满）', av: avOf(P.loc), multi: true }]) +
        K.ab([{ ic: 'E8FB', t: '完成', go: 'group-members' }, { ic: 'E711', t: '取消', go: 'group-members' }]);
    }
  });

  screen({
    id: 'group-member-edit', t: '编辑群聊成员', src: 'GroupMember：name / avatar / greeting / enabled / order',
    note: ['只改这个成员在群里的样子，不动原档案', '首句只在群里第一次发言时用'],
    render: function () {
      return K.ph('群聊成员 · 来自「工程助理」', '老周') + K.body(
        K.row({ av: K.av('周'), t: '头像', s: '用档案头像 · 点击更换', go: 'avatar' }) +
        K.tb('群里显示的名字', '老周', { help: '留空则用档案名「工程助理」。@ 点名按这个名字匹配。' }) +
        K.tb('首句（可空）', '我先说风险，你们再说方案。', { multi: true }) +
        '<div class="fld"><div class="fld-hd">发言顺序<small>轮流模式按这个排</small></div><div style="display:flex;gap:8px;align-items:center">' + K.btn('', { icon: 'E70E', sm: true }) + '<span class="t-base" style="width:40px;text-align:center">1</span>' + K.btn('', { icon: 'E70D', sm: true }) + '</div></div>' +
        K.tog('启用', true, { s: '停用后不发言，但保留设置' })
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'group-members' }, { ic: 'E711', t: '取消', go: 'group-members' }], [{ t: '移出群聊' }]);
    }
  });

  screen({
    id: 'group-mode', t: '群聊回复模式', src: 'GroupReplyModes：round_robin / mention / free',
    note: ['三种模式各一句人话说明', '自由发言不额外调用模型，靠提示词引导'],
    render: function () {
      return K.ph('群聊 · 三个人的方案评审', '回复模式') + K.body(
        K.rgrp('', [
          { t: '轮流发言', s: '按成员顺序一人一句。适合评审、头脑风暴。' },
          { t: '点名回复（@某人）', s: '消息里 @谁 就只让谁说；没 @ 时退回轮流。' },
          { t: '自由发言（模型决定）', s: '把成员清单写进提示词，由模型选以谁的口吻说。适合角色扮演。' }], 0) +
        K.note('自由发言不会为了「选人」额外请求一次模型，不额外消耗额度。')
      ) + K.ab([{ ic: 'E74E', t: '保存', go: 'group-members' }, { ic: 'E711', t: '取消', go: 'group-members' }]);
    }
  });
})();
