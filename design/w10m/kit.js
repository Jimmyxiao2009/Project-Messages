/* 言枢 W10M 设计稿 —— 组件函数
   每个函数返回一段 HTML 字符串。页面在 screens-*.js 里用它们拼出来。
   命名对应 XAML 侧的控件 / 样式，方便实现时对照。 */
(function () {
  'use strict';

  var GROUPS = window.GROUPS = [];
  var SCREENS = window.SCREENS = [];
  var current = null;

  window.group = function (id, title, desc) {
    current = { id: id, title: title, desc: desc || '' };
    GROUPS.push(current);
  };
  window.screen = function (def) {
    def.group = current.id;
    SCREENS.push(def);
  };

  function ic(code, cls) { return '<i class="ic' + (cls ? ' ' + cls : '') + '">&#x' + code + ';</i>'; }
  function go(r) { return r ? ' data-go="' + r + '"' : ''; }
  function cls(base, extra) { return extra ? base + ' ' + extra : base; }
  function join(a) { return (a || []).join(''); }

  var K = window.K = {};
  K.ic = ic;
  K.go = go;

  /* ---------- 页头 ---------- */

  /** 根 Pivot：选中的标题轮转到最左（W10M 行为），其余按顺序跟在后面。 */
  K.pivot = function (title, heads, active, o) {
    o = o || {};
    var order = [];
    for (var i = 0; i < heads.length; i++) order.push(heads[(active + i) % heads.length]);
    return '<div class="pv' + (o.sm ? ' sm' : '') + '"><div class="pv-title">' + title + '</div><div class="pv-heads">' +
      order.map(function (h, i) { return '<span class="pv-h' + (i === 0 ? ' on' : '') + '"' + go(h.go) + '>' + h.t + '</span>'; }).join('') +
      '</div></div>';
  };
  K.root = function (active) {
    return K.pivot('言枢 <small>4 个档案</small>', [
      { t: '对话', go: 'home' }, { t: '档案', go: 'profiles' }, { t: '我的', go: 'me' }], active);
  };

  /** 子页标题：12px 上标签 + 24px SemiLight 标题。right 放标题行右侧动作（键盘弹出时的「保存」）。 */
  K.ph = function (over, title, right) {
    return '<div class="ph"><div class="ph-main"><div class="ph-over">' + over + '</div><div class="ph-title">' + title + '</div></div>' +
      (right ? '<div class="ph-act">' + right + '</div>' : '') + '</div>';
  };
  K.hb = function (label, o) {
    o = o || {};
    var inner = o.icon ? ic(o.icon) : label;
    return '<span class="hb tilt' + (o.acc ? ' acc' : '') + '"' + go(o.go) + (o.icon ? ' title="' + label + '"' : '') + '>' + inner + '</span>';
  };

  /** 会话页顶栏（例外：Chrome 色块 48px）。state: running | offline | group */
  K.chat = function (title, sub, o) {
    o = o || {};
    var lead = '';
    if (o.state === 'running') lead = '<span class="pulse"></span>';
    if (o.state === 'offline') lead = '<span class="offdot"></span>';
    return '<div class="ch"><div class="c"><b>' + title + '</b><small>' + lead + '<span class="ell">' + sub + '</span></small></div>' +
      '<span class="hb tilt"' + go(o.more || 'chat-menu') + '>' + ic('E712') + '</span></div>';
  };

  K.body = function (html, extra) { return '<div class="' + cls('body', extra) + '">' + html + '</div>'; };
  K.sec = function (t, o) {
    o = o || {};
    return '<div class="sec' + (o.warn ? ' warn' : '') + '"><span>' + t + '</span>' + (o.right ? '<span class="sp"></span>' + o.right : '') + '</div>';
  };
  K.tx = function (t, small) { return '<p class="tx' + (small ? ' s' : '') + '">' + t + '</p>'; };
  K.gap = function (n) { return '<div class="gap' + (n || 8) + '"></div>'; };

  /* ---------- 列表行 ---------- */

  /** o: { ic, av, t, s, s2, rt, go, bar:'warn|acc|err|n', sel, dis, wrap, right, l3, dense, hdl, lead } */
  K.row = function (o) {
    var lead = '';
    if (o.hdl) lead += '<span class="hdl">' + ic('E700') + '</span>';
    if (o.ic) lead += '<span class="lead' + (o.icMid ? ' mid' : '') + '">' + ic(o.ic) + '</span>';
    if (o.av) lead += o.av;
    if (o.lead) lead += o.lead;
    var c = '<div class="c"><b' + (o.wrapT ? ' class="wrap"' : '') + '>' + o.t + '</b>' +
      (o.s ? '<small' + (o.wrap ? ' class="wrap"' : '') + '>' + o.s + '</small>' : '') +
      (o.l3 ? '<div class="l3">' + o.l3 + '</div>' : '') + '</div>';
    var rt = o.rt ? '<span class="rt' + (o.rtMid ? ' mid' : '') + '">' + o.rt + '</span>' : '';
    var k = 'row tilt';
    if (o.bar) k += ' bar' + (o.bar === 'n' ? '' : ' ' + o.bar);
    if (o.sel) k += ' sel';
    if (o.dis) k += ' dis';
    if (o.dense) k += ' dense';
    return '<div class="' + k + '"' + go(o.go) + '>' + lead + c + rt + (o.right || '') + '</div>';
  };
  K.ibtn = function (code, o) {
    o = o || {};
    return '<span class="ibtn' + (o.on ? ' on' : '') + (o.toggle ? ' star' : '') + '"' + go(o.go) + '>' + ic(code) + '</span>';
  };

  /** 头像：首字 + 强调色柔底；img: 1|2|3 用渐变占位立绘；n: 中性底 */
  K.av = function (ch, o) {
    o = o || {};
    var k = 'av';
    if (o.size) k += ' ' + o.size;
    if (o.img) k += ' img' + o.img;
    if (o.n) k += ' n';
    return '<span class="' + k + '">' + (o.img ? '' : ch) + (o.dot ? '<i class="badge-dot"></i>' : '') + '</span>';
  };

  K.bd = function (t, kind) { return '<span class="bd' + (kind ? ' ' + kind : '') + '">' + t + '</span>'; };
  K.bds = function (arr) { return '<div class="bdrow">' + join(arr) + '</div>'; };

  /* ---------- 控件 ---------- */

  /** ToggleSwitch：标题在上，开关 + 「开/关」在下（W10M 设置页标准形态）。 */
  K.tog = function (t, on, o) {
    o = o || {};
    return '<div class="tg-wrap"><div class="hd">' + t + '</div>' + (o.s ? '<div class="ds">' + o.s + '</div>' : '') +
      '<div class="tg' + (on ? ' on' : '') + (o.dis ? ' dis' : '') + '" data-tg' + (o.ctl ? ' data-ctl="' + o.ctl + '"' : '') + '><i></i><span>' + (on ? '开' : '关') + '</span></div></div>';
  };
  K.chk = function (t, on, o) {
    o = o || {};
    return '<div class="chk' + (on ? ' on' : '') + (o.dis ? ' dis' : '') + '" data-ck><i></i><div class="c">' + t + (o.s ? '<small>' + o.s + '</small>' : '') + '</div>' + (o.right || '') + '</div>';
  };
  K.rad = function (t, on, o) {
    o = o || {};
    return '<div class="rad' + (on ? ' on' : '') + (o.dis ? ' dis' : '') + '" data-rd><i></i><div class="c">' + t + (o.s ? '<small>' + o.s + '</small>' : '') + '</div></div>';
  };
  K.rgrp = function (hd, items, sel) {
    return '<div class="rgrp">' + (hd ? '<div class="fld-hd" style="padding:0 12px">' + hd + '</div>' : '') +
      items.map(function (it, i) {
        if (typeof it === 'string') it = { t: it };
        return K.rad(it.t, i === sel, { s: it.s, dis: it.dis });
      }).join('') + '<div class="gap8"></div></div>';
  };

  /** TextBox。o: { ph, focus, multi, tall, mono, err, help, pw, one, dis, icon, small } */
  K.tb = function (hd, val, o) {
    o = o || {};
    var k = 'tb';
    if (!val) k += ' ph';
    if (o.focus) k += ' focus';
    if (o.multi) k += ' multi';
    if (o.tall) k += ' tall';
    if (o.mono) k += ' mono';
    if (o.err) k += ' err';
    if (o.dis) k += ' dis';
    if (!o.multi && !o.tall) k += ' one';
    var v = val || o.ph || '';
    if (o.pw && val) v = '<span class="pw">' + val + '</span>';
    var caret = o.focus ? '<span class="caret"></span>' : '';
    var tail = o.icon ? '<span class="tic">' + ic(o.icon) + '</span>' : '';
    var box = '<div class="' + k + '"><span class="v">' + v + (val ? caret : '') + '</span>' + tail + '</div>';
    if (o.bare) return box;
    return '<div class="fld">' + (hd ? '<div class="fld-hd">' + hd + (o.small ? '<small>' + o.small + '</small>' : '') + '</div>' : '') + box +
      (o.err && typeof o.err === 'string' ? '<div class="fld-err">' + o.err + '</div>' : '') +
      (o.help ? '<div class="fld-help">' + o.help + '</div>' : '') + '</div>';
  };
  /** ComboBox：手机上选项多时点开进全屏选择页，所以带 go。 */
  K.cb = function (hd, val, o) {
    o = o || {};
    return '<div class="fld">' + (hd ? '<div class="fld-hd">' + hd + '</div>' : '') +
      '<div class="cbx tilt"' + go(o.go) + '><span class="v">' + val + '</span>' + ic('E70D') + '</div>' +
      (o.help ? '<div class="fld-help">' + o.help + '</div>' : '') + '</div>';
  };
  /** Slider：8×24 竖条滑块。pct 0–100，val 显示值。 */
  K.sl = function (hd, pct, val, o) {
    o = o || {};
    return '<div class="sld' + (o.dis ? ' dis' : '') + '"><div class="hd"><span>' + hd + '</span><span class="sp"></span><span class="v">' + val + '</span></div>' +
      '<div class="tr"><i class="f" style="width:' + pct + '%"></i><i class="th" style="left:' + pct + '%"></i></div>' +
      (o.min ? '<div class="sub"><span>' + o.min + '</span><span>' + o.max + '</span></div>' : '') +
      (o.help ? '<div class="fld-help">' + o.help + '</div>' : '') +
      (o.def !== undefined ? K.chk(o.defT || '用模型默认', o.def) : '') + '</div>';
  };

  K.btn = function (t, o) {
    o = o || {};
    var k = 'btn tilt';
    if (o.acc) k += ' acc';
    if (o.dis) k += ' dis';
    if (o.full) k += ' full';
    if (o.sm) k += ' sm';
    return '<span class="' + k + '"' + go(o.go) + '>' + (o.icon ? ic(o.icon) : '') + t + '</span>';
  };
  K.btns = function (arr, extra) { return '<div class="' + cls('btns', extra) + '">' + join(arr) + '</div>'; };
  K.lnk = function (t, r) { return '<span class="lnk"' + go(r) + '>' + t + '</span>'; };

  /* ---------- 底部应用栏 CommandBar ---------- */

  /** prim: [{ic,t,go,dis}]（≤3），sec: [文本 | {t,go}]。o.open 展开态（显示标签与二级命令），o.min 最小化。 */
  K.ab = function (prim, sec, o) {
    o = o || {};
    var k = 'cbar' + (o.open ? ' open' : '') + (o.min ? ' min' : '');
    var p = (prim || []).map(function (b) {
      return '<div class="ab tilt' + (b.dis ? ' dis' : '') + '"' + go(b.go) + '>' + ic(b.ic) + '<span>' + b.t + '</span></div>';
    }).join('');
    var s = (sec || []).map(function (b) {
      if (typeof b === 'string') b = { t: b };
      return '<div class="tilt"' + go(b.go) + '>' + b.t + '</div>';
    }).join('');
    var dim = o.open ? '<div class="cbar-dim"></div>' : '';
    return dim + '<div class="' + k + '" data-cbar>' + (s ? '<div class="secm">' + s + '</div>' : '') +
      '<div class="prim">' + p + '<div class="more" data-more>' + ic('E712') + '</div></div></div>';
  };

  /* ---------- 弹层 ---------- */

  /** ContentDialog：手机上贴顶、全宽、按钮等分。 */
  K.dlg = function (o) {
    return '<div class="ovl"></div><div class="dlg"><h3>' + o.title + '</h3>' + (o.body || '') +
      K.btns(o.btns.map(function (b, i) {
        if (typeof b === 'string') b = { t: b };
        return K.btn(b.t, { acc: b.acc, go: b.go });
      })) + '</div>';
  };
  /** MenuFlyout：全宽。top 指定纵向位置（px）。 */
  K.fly = function (items, top, o) {
    o = o || {};
    return (o.noDim ? '' : '<div class="ovl" style="background:transparent"></div>') +
      '<div class="fly" style="top:' + top + 'px">' + items.map(function (it) {
        if (it === '-') return '<div class="sepr"></div>';
        if (typeof it === 'string') it = { t: it };
        return '<div class="it tilt' + (it.dis ? ' dis' : '') + '"' + go(it.go) + '>' + (it.ic ? ic(it.ic) : '') + '<span>' + it.t + '</span>' + (it.s ? '<small>' + it.s + '</small>' : '') + '</div>';
      }).join('') + '</div>';
  };

  /** 全屏选择页（ListPickerFlyout 样式）。items: [{t,s,av,on,go}] */
  K.picker = function (title, items, o) {
    o = o || {};
    return '<div class="pk-hd">' + title + '</div>' + K.body(items.map(function (it) {
      return '<div class="pk-it tilt' + (it.on ? ' on' : '') + '"' + go(it.go || o.go) + '>' + (it.multi !== undefined ? '<div class="chk' + (it.multi ? ' on' : '') + '" style="padding:0" data-ck><i></i></div>' : '') +
        (it.av || '') + '<div class="c">' + it.t + (it.s ? '<small>' + it.s + '</small>' : '') + '</div></div>';
    }).join(''));
  };

  /* ---------- 状态与杂项 ---------- */

  K.empty = function (o) {
    return '<div class="empty"><div class="eic">' + ic(o.ic) + '</div><div class="et">' + o.t + '</div><div class="es">' + o.s + '</div>' +
      (o.btns ? K.btns(o.btns, 'left') : '') + '</div>';
  };
  K.note = function (t, kind, code) {
    var map = { warn: 'E7BA', err: 'EA39', ok: 'E930', info: 'E946' };
    return '<div class="note' + (kind ? ' ' + kind : '') + '">' + ic(code || map[kind || 'info']) + '<div class="c">' + t + '</div></div>';
  };
  K.kv = function (pairs) {
    return '<div class="kv">' + pairs.map(function (p) {
      return '<div><span>' + p[0] + '</span><span' + (p[2] ? ' class="mono"' : '') + '>' + p[1] + '</span></div>';
    }).join('') + '</div>';
  };
  K.chips = function (arr, o) {
    o = o || {};
    return '<div class="chips"' + (o.pad ? ' style="padding:0 12px 12px"' : '') + '>' + arr.map(function (c) {
      return '<span class="chip' + (o.mono ? ' mono' : '') + '">' + c + (o.x ? '<span class="x">&#xE711;</span>' : '') + '</span>';
    }).join('') + (o.add ? '<span class="chip add">' + ic('E710') + o.add + '</span>' : '') + '</div>';
  };
  K.flt = function (arr, on) {
    return '<div class="flt">' + arr.map(function (t, i) { return '<span class="' + (i === on ? 'on' : '') + '" data-flt>' + t + '</span>'; }).join('') + '</div>';
  };
  K.pbar = function (pct, kind, style) {
    return '<div class="pbar' + (kind ? ' ' + kind : '') + '"' + (style ? ' style="' + style + '"' : '') + '><i style="width:' + pct + '%"></i></div>';
  };
  K.dots = function (extra) { return '<div class="dots' + (extra ? ' ' + extra : '') + '"><i></i><i></i><i></i><i></i><i></i></div>'; };
  K.ring = function (big) { return '<span class="ring' + (big ? ' l' : '') + '"><i></i><i></i><i></i><i></i><i></i></span>'; };
  K.steps = function (n, on) {
    var s = '';
    for (var i = 0; i < n; i++) s += '<i class="' + (i <= on ? 'on' : '') + '"></i>';
    return '<div class="steps">' + s + '</div>';
  };
  K.swg = function (colors, sel, o) {
    o = o || {};
    return '<div class="swg' + (o.s8 ? ' s8' : '') + (o.dis ? ' dis' : '') + '"' + (o.ctlt ? ' data-ctlt="' + o.ctlt + '"' : '') + '>' + colors.map(function (c, i) {
      if (c === 'none') return '<i class="none' + (i === sel ? ' on' : '') + '" data-sw></i>';
      return '<i style="background:' + c + '" class="' + (i === sel ? 'on' : '') + '" data-sw data-c="' + c + '"></i>';
    }).join('') + '</div>';
  };
  K.seg = function (parts) {
    return '<div class="seg">' + parts.map(function (p) { return '<i style="width:' + p[1] + '%;background:' + p[2] + '"></i>'; }).join('') + '</div>' +
      '<div class="legend">' + parts.map(function (p) { return '<span><i style="background:' + p[2] + '"></i>' + p[0] + '</span>'; }).join('') + '</div>';
  };
  K.crumb = function (parts) {
    return '<div class="crumb">' + parts.map(function (p, i) {
      return i === parts.length - 1 ? '<b>' + p + '</b>' : '<span>' + p + '</span>' + ic('E76C');
    }).join('') + '</div>';
  };
  K.metric = function (items) {
    return '<div class="metric">' + items.map(function (m) { return '<div><b>' + m[0] + '</b><small>' + m[1] + '</small></div>'; }).join('') + '</div>';
  };

  /* ---------- 会话：时间线 ---------- */
  var T = window.T = {};

  T.wrap = function (html) { return '<div class="tl">' + html + '</div>'; };
  /** 用户消息：唯一的气泡。o: { att:[html], meta } */
  T.user = function (text, o) {
    o = o || {};
    return '<div class="u" data-long="message-actions">' + (o.att ? '<div class="att">' + join(o.att) + '</div>' : '') +
      '<div class="bub">' + text + '</div>' + (o.meta ? '<div class="meta">' + o.meta + '</div>' : '') + '</div>';
  };
  T.att = function (name, code) { return '<span class="attc">' + ic(code || 'E8A5') + '<span class="ell">' + name + '</span></span>'; };
  T.thumb = function (k, size) { return '<span class="thumb ' + (k || '') + '" style="width:' + (size || 64) + 'px;height:' + (size || 64) + 'px"></span>'; };
  /** 独立的思考行（无工具的一轮）：虚线圆圈 + 斜体。 */
  T.think = function (text, o) {
    o = o || {};
    return '<div class="think"><span class="rg' + (o.run ? ' spin' : '') + '"></span><div class="c"><div class="tt' + (o.open ? '' : ' cl') + '">' + text + '</div>' +
      (o.sub ? '<small>' + o.sub + '</small>' : '') + '</div></div>';
  };
  /** 过程轨道。steps: [{k:'think|tool|note|sys|err|more', st:'done|run|fail|stopped', t, name, arg, out, sub, dur, act}] */
  // Only touches text between tags, so slashes inside markup like </b> stay intact.
  function pathBreaks(html) {
    return html.replace(/(^|>)([^<]*)/g, function (m, lead, text) { return lead + text.replace(/([\/\\])/g, '$1<wbr>'); });
  }
  T.rail = function (steps, o) {
    o = o || {};
    return '<div class="rail' + (o.fail ? ' fail' : '') + (o.stop ? ' stop' : '') + '">' + steps.map(function (s) {
      var k = 'st k-' + (s.k || 'tool') + ' ' + (s.st || 'done');
      var title = s.name ? '<b>' + s.name + '</b><span>' + pathBreaks(s.t || '') + '</span>' : '<span>' + s.t + '</span>';
      return '<div class="' + k + '"><div class="sl">' + title + (s.dur ? '<span class="dur">' + s.dur + '</span>' : '') + '</div>' +
        (s.sub ? '<div class="sub">' + s.sub + '</div>' : '') +
        (s.arg ? '<div class="arg">' + s.arg + '</div>' : '') +
        (s.out ? '<div class="out">' + s.out + '</div>' : '') +
        (s.act ? '<div class="act">' + s.act + '</div>' : '') + '</div>';
    }).join('') + '</div>';
  };
  T.out = function (html, o) {
    o = o || {};
    return '<div class="out' + (o.mid ? ' mid' : '') + '" data-long="message-actions-ai">' + html + '</div>';
  };
  T.code = function (lang, code) {
    return '<div class="code"><div class="lb"><span>' + lang + '</span><span class="sp"></span><span class="lnk">复制</span><span class="lnk">保存</span></div><pre>' + code + '</pre></div>';
  };
  T.table = function (head, rows) {
    return '<div class="mdt"><table><tr>' + head.map(function (h) { return '<th>' + h + '</th>'; }).join('') + '</tr>' +
      rows.map(function (r) { return '<tr>' + r.map(function (c) { return '<td>' + c + '</td>'; }).join('') + '</tr>'; }).join('') + '</table></div>';
  };
  T.marker = function (html) { return '<div class="mk"><span>' + html + '</span></div>'; };
  T.err = function (title, sub, o) {
    o = o || {};
    return '<div class="erow"><b>' + ic('EA39') + title + '</b><small>' + sub + '</small>' + (o.detail ? '<div class="eout">' + o.detail + '</div>' : '') +
      K.btns([K.btn('重试', { sm: true, icon: 'E72C' }), K.btn('查看详情', { sm: true, go: 'debug' })], 'left') + '</div>';
  };
  T.spk = function (name, av, extra) { return '<div class="spk">' + av + '<b>' + name + '</b>' + (extra || '') + '</div>'; };

  /* ---------- 会话：上下各条 ---------- */
  var C = window.C = {};

  /** 审批固定条（NoticeBanner）：会话顶部，不进时间线。 */
  C.notice = function (title, sub, btn, o) {
    o = o || {};
    return '<div class="notice tilt' + (o.kind ? ' ' + o.kind : '') + '"' + go(o.go === undefined ? 'approval' : o.go) + '><div class="c"><b>' + title + '</b><small>' + sub + '</small></div>' +
      (btn ? K.btn(btn, { sm: true, acc: !o.kind }) : '') + '</div>';
  };
  C.strip = function (lead, text, actions, o) {
    o = o || {};
    return '<div class="strip' + (o.kind ? ' ' + o.kind : '') + '">' + lead + '<span class="c">' + text + '</span>' + join(actions) + '</div>';
  };
  C.sb = function (t, o) {
    o = o || {};
    return '<span class="sb2 tilt' + (o.stop ? ' stop' : '') + (o.dis ? ' dis' : '') + '"' + go(o.go) + '>' + (o.icon ? ic(o.icon) : '') + (t ? '<span>' + t + '</span>' : '') + '</span>';
  };
  /** 运行条：进度环 + 状态 + 打断（永远点得到）。 */
  C.run = function (text) { return C.strip(K.ring(), text, [C.sb('打断', { stop: true, icon: 'E71A' })]); };
  /** 分支条：候选 N / M + 左右切换。 */
  C.branch = function (i, n) {
    return C.strip(ic('E8AB', 'lead'), '候选 ' + i + ' / ' + n, [C.sb('', { icon: 'E76B', dis: i === 1 }), C.sb('', { icon: 'E76C', dis: i === n }), C.sb('重新生成', { icon: 'E72C' })]);
  };
  C.speak = function (text) { return C.strip(ic('E767', 'lead'), text, [C.sb('停止', { stop: true, icon: 'E71A' })]); };
  /** 快捷消息栏：点一下是填进输入框，不是直接发出去。 */
  C.quick = function (items) {
    return '<div class="qbar">' + items.map(function (t) { return '<span class="tilt" data-fill="' + t + '">' + t + '</span>'; }).join('') +
      '<span class="mgr tilt" data-go="quick-messages">' + ic('E713') + '</span></div>';
  };
  /** 输入区。o: { ph, val, focus, mode:'send|steer|off', att:[html], hint, hintWarn, noAttach } */
  C.cmp = function (o) {
    o = o || {};
    var mode = o.mode || 'send';
    var right = mode === 'steer'
      ? '<span class="steer tilt">' + ic('E72A') + '追加</span>'
      : '<span class="send tilt' + (mode === 'off' || (!o.val && !o.ready) ? ' dis' : '') + '">' + ic('E724') + '</span>';
    return '<div class="cmp">' + (o.att ? '<div class="attrow">' + join(o.att) + '</div>' : '') +
      '<div class="row1"><span class="ib tilt' + (o.noAttach ? ' dis' : '') + '"' + go(o.noAttach ? '' : 'attachment-add') + '>' + ic('E723') + '</span>' +
      K.tb('', o.val, { ph: o.ph || '发消息…', focus: o.focus, bare: true, multi: false }) + right + '</div>' +
      (o.hint ? '<div class="hint' + (o.hintWarn ? ' warn' : '') + '">' + o.hint + '</div>' : '') + '</div>';
  };
  C.fab = function (bottom, n) {
    return '<span class="fab tilt" style="bottom:' + bottom + 'px">' + ic('E70D') + (n ? '<small>' + n + '</small>' : '') + '</span>';
  };

  /* ---------- 常用片段 ---------- */
  K.QUICK = ['继续', '展开说说', '更简短', '翻译成英文', '改写成代码', '检查错误'];
  K.ACCENTS = ['#FFB900', '#FF8C00', '#F7630C', '#CA5010', '#DA3B01', '#EF6950', '#D13438', '#FF4343', '#E74856', '#E81123', '#EA005E', '#C30052',
    '#E3008C', '#BF0077', '#C239B3', '#9A0089', '#0078D7', '#0063B1', '#8E8CD8', '#6B69D6', '#8764B8', '#744DA9', '#B146C2', '#881798',
    '#0099BC', '#2D7D9A', '#00B7C3', '#038387', '#00B294', '#018574', '#00CC6A', '#10893E', '#7A7574', '#5D5A58', '#68768A', '#515C6B'];
})();
