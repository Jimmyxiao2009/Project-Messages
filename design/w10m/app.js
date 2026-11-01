/* 言枢 W10M 设计稿 —— 画廊 + 单屏交互预览
   画廊：按组列出全部页面。点任一手机进入交互预览，里面的导航、开关、单选、
   CommandBar「•••」、快捷消息填入、长按菜单都能点。系统导航栏的返回键 = 返回上一页。 */
(function () {
  'use strict';

  var byId = {};
  var order = [];
  SCREENS.forEach(function (s, i) {
    s.no = i + 1;
    byId[s.id] = s;
    order.push(s.id);
  });

  function pad(n) { return (n < 10 ? '0' : '') + n; }

  /* ---------- 画板 ---------- */
  function statusBar(def) {
    var sig = '<span class="sig"><i style="height:3px"></i><i style="height:5px"></i><i style="height:7px"></i><i style="height:10px"></i></span>';
    var wifi = K.ic('E701');
    if (def.sb) {
      return '<div class="sb">' + K.dots('sbdots') + '<span class="sbtext">' + def.sb + '</span><span class="sp"></span><span class="bat"><i></i></span><span>10:42</span></div>';
    }
    return '<div class="sb">' + sig + wifi + '<span class="sp"></span><span class="bat"><i></i></span><span>10:42</span></div>';
  }
  function navBar() {
    return '<div class="nb" title="系统导航栏（画板模拟物，真机由系统提供）"><span data-back>' + K.ic('E72B') + '</span>' +
      '<span><span class="win"><i></i><i></i><i></i><i></i></span></span><span>' + K.ic('E721') + '</span></div>';
  }
  function sip() {
    function row(keys, extra) {
      return '<div class="kr ' + (extra || '') + '">' + keys.map(function (k) {
        if (typeof k === 'string') return '<span class="k">' + k + '</span>';
        return '<span class="k ' + (k.c || '') + '">' + k.t + '</span>';
      }).join('') + '</div>';
    }
    return '<div class="sip" title="软键盘（系统提供，画板模拟）"><div class="cand"><span>方案</span><span>放案</span><span>防暗</span><span>方安</span><span>芳</span><span>' + K.ic('E70D') + '</span></div>' +
      row('qwertyuiop'.split('')) + row('asdfghjkl'.split(''), 'i1') +
      row([{ t: K.ic('E752'), c: 'a w2' }].concat('zxcvbnm'.split('')).concat([{ t: K.ic('E750'), c: 'a w2' }])) +
      row([{ t: '&amp;123', c: 'a w2' }, { t: '中', c: 'a' }, { t: '，', c: 'a' }, { t: '空格', c: 'a w5' }, { t: '。', c: 'a' }, { t: K.ic('E751'), c: 'ent w2' }]) + '</div>';
  }
  function phone(def) {
    var html;
    try { html = def.render(); }
    catch (e) { html = '<div class="body"><p class="tx t-err">渲染失败：' + e.message + '</p></div>'; console.error(def.id, e); }
    return '<div class="phone' + (def.land ? ' land' : '') + '" data-id="' + def.id + '">' + statusBar(def) +
      '<div class="app">' + html + '</div>' + (def.sip ? sip() : '') + navBar() + '</div>';
  }
  window.renderPhone = phone;

  function caption(def, full) {
    var notes = (def.note || []).map(function (n) { return '<li>' + n + '</li>'; }).join('');
    return '<b>' + pad(def.no) + '　' + def.t + '</b><code>#' + def.id + '</code>' +
      (def.src ? '<div class="src">依据：' + def.src + '</div>' : '') +
      (notes ? '<ul>' + notes + '</ul>' : '') + (full ? '' : '');
  }

  /* ---------- 画廊 ---------- */
  var params = {};
  location.search.replace(/^\?/, '').split('&').forEach(function (kv) {
    if (!kv) return;
    var p = kv.split('=');
    params[decodeURIComponent(p[0])] = decodeURIComponent(p[1] || '');
  });

  function renderGallery() {
    var out = [];
    GROUPS.forEach(function (g, gi) {
      if (params.only && params.only !== g.id) return;
      var pick = params.ids ? params.ids.split(',') : null;
      var items = SCREENS.filter(function (s) { return s.group === g.id && (!pick || pick.indexOf(s.id) >= 0); });
      if (!items.length) return;
      out.push('<section class="grp" id="g-' + g.id + '"><h2><span>' + pad(gi + 1) + '</span>' + g.title + '<small>' + items.length + ' 屏</small></h2>' +
        (g.desc ? '<p class="gdesc">' + g.desc + '</p>' : '') + '<div class="grid">' +
        items.map(function (s) {
          return '<figure class="shot' + (s.land ? ' landf' : '') + '" id="s-' + s.id + '">' + phone(s) + '<figcaption>' + caption(s) + '</figcaption></figure>';
        }).join('') + '</div></section>');
    });
    document.getElementById('gallery').innerHTML = out.join('');

    var nav = GROUPS.map(function (g, gi) {
      var n = SCREENS.filter(function (s) { return s.group === g.id; }).length;
      return '<a href="#g-' + g.id + '" data-jump="g-' + g.id + '">' + pad(gi + 1) + ' ' + g.title + ' <small>' + n + '</small></a>';
    }).join('');
    document.getElementById('toc').innerHTML = nav;
    document.getElementById('count').textContent = SCREENS.length;
  }

  /* ---------- 覆盖表 ---------- */
  function renderCoverage() {
    var host = document.getElementById('coverage');
    if (!host || !window.COVER) return;
    var missing = [];
    var rows = COVER.map(function (c) {
      var links = c[2].map(function (id) {
        if (!byId[id]) { missing.push(id); return '<span class="miss">' + id + '?</span>'; }
        return '<a href="#' + id + '" data-open="' + id + '">' + pad(byId[id].no) + ' ' + byId[id].t + '</a>';
      }).join('');
      return '<tr><td class="k">' + c[0] + '</td><td>' + c[1] + '</td><td class="links">' + links + '</td></tr>';
    }).join('');
    host.innerHTML = '<table><tr><th>来源</th><th>要求</th><th>对应页面</th></tr>' + rows + '</table>';
    if (missing.length) console.warn('COVER 引用了不存在的页面：', missing);
  }

  /* ---------- 主题与强调色（与 Ui\ThemeHelper.ApplyAccent 同算法） ---------- */
  function hex2rgb(h) { h = h.replace('#', ''); return [parseInt(h.substr(0, 2), 16), parseInt(h.substr(2, 2), 16), parseInt(h.substr(4, 2), 16)]; }
  function rgb2hex(c) { return '#' + c.map(function (v) { v = Math.max(0, Math.min(255, Math.round(v))); return (v < 16 ? '0' : '') + v.toString(16); }).join('').toUpperCase(); }
  function mix(c, t, k) { return [c[0] + (t[0] - c[0]) * k, c[1] + (t[1] - c[1]) * k, c[2] + (t[2] - c[2]) * k]; }
  var accentStyle = document.createElement('style');
  document.head.appendChild(accentStyle);
  var state = { accent: '#0078D7', light: false };
  function applyAccent() {
    var c = hex2rgb(state.accent);
    var darkText = rgb2hex(mix(c, [255, 255, 255], 0.55));
    var lightText = rgb2hex(mix(c, [0, 0, 0], 0.22));
    var soft = 'rgba(' + c.join(',') + ',.35)';
    var lum = c[0] * 0.299 + c[1] * 0.587 + c[2] * 0.114;
    var on = lum > 186 ? '#000000' : '#FFFFFF';
    accentStyle.textContent =
      '.phone,.tok{--Accent:' + state.accent + ';--AccentText:' + darkText + ';--AccentSoft:' + soft + ';--OnAccent:' + on + '}' +
      '.light .phone,.light .tok{--AccentText:' + lightText + '}';
  }

  function setBodyClass(name, on) { document.body.classList.toggle(name, !!on); }

  function bindToolbar() {
    document.querySelectorAll('[data-theme]').forEach(function (b) {
      b.addEventListener('click', function () {
        state.light = b.getAttribute('data-theme') === 'light';
        setBodyClass('light', state.light);
        document.querySelectorAll('[data-theme]').forEach(function (x) { x.classList.toggle('on', x === b); });
      });
    });
    document.querySelectorAll('[data-accent]').forEach(function (b) {
      b.style.background = b.getAttribute('data-accent');
      b.addEventListener('click', function () {
        state.accent = b.getAttribute('data-accent');
        applyAccent();
        document.querySelectorAll('[data-accent]').forEach(function (x) { x.classList.toggle('on', x === b); });
      });
    });
    document.querySelectorAll('[data-w]').forEach(function (b) {
      b.addEventListener('click', function () {
        var w = b.getAttribute('data-w');
        setBodyClass('w320', w === '320');
        setBodyClass('w430', w === '430');
        document.querySelectorAll('[data-w]').forEach(function (x) { x.classList.toggle('on', x === b); });
      });
    });
    document.querySelectorAll('[data-nav]').forEach(function (b) {
      b.addEventListener('click', function () {
        setBodyClass('hwnav', b.getAttribute('data-nav') === 'hw');
        document.querySelectorAll('[data-nav]').forEach(function (x) { x.classList.toggle('on', x === b); });
      });
    });
  }

  /* ---------- 交互预览 ---------- */
  var viewer, stage, cap, stack = [];

  function show(id, push) {
    var def = byId[id];
    if (!def) { flash('页面 ' + id + ' 不在本稿中'); return; }
    if (push !== false && stack[stack.length - 1] !== id) stack.push(id);
    stage.innerHTML = phone(def);
    var app = stage.querySelector('.app');
    app.classList.add('enter');
    cap.innerHTML = caption(def, true) + '<div class="vnav"><span data-step="-1">‹ 上一屏</span><span>' + def.no + ' / ' + SCREENS.length + '</span><span data-step="1">下一屏 ›</span></div>' +
      '<p class="vtip">点页面里的行与按钮可以跳转；系统导航栏的 ← 是返回键；右键或长按消息打开长按菜单；Esc 关闭。</p>';
    viewer.classList.add('open');
    document.body.classList.add('noscroll');
    if (location.hash !== '#' + id) history.replaceState(null, '', '#' + id);
  }
  function back() {
    stack.pop();
    if (stack.length) show(stack[stack.length - 1], false);
    else close();
  }
  function close() {
    viewer.classList.remove('open');
    document.body.classList.remove('noscroll');
    stack = [];
    history.replaceState(null, '', location.pathname + location.search);
  }
  function flash(msg) {
    var f = document.getElementById('flash');
    f.textContent = msg;
    f.classList.add('on');
    clearTimeout(flash.t);
    flash.t = setTimeout(function () { f.classList.remove('on'); }, 1600);
  }

  function interact(e, ph) {
    var t = e.target;
    var el;
    if ((el = t.closest('[data-back]'))) { back(); return true; }
    if ((el = t.closest('[data-more]'))) {
      var bar = el.closest('[data-cbar]');
      var open = !bar.classList.contains('open');
      bar.classList.toggle('open', open);
      var dim = bar.previousElementSibling;
      if (open && !(dim && dim.classList.contains('cbar-dim'))) { var d = document.createElement('div'); d.className = 'cbar-dim'; bar.parentNode.insertBefore(d, bar); }
      if (!open && dim && dim.classList.contains('cbar-dim')) dim.remove();
      return true;
    }
    if ((el = t.closest('.cbar-dim'))) {
      var b2 = el.nextElementSibling; if (b2) b2.classList.remove('open'); el.remove(); return true;
    }
    if ((el = t.closest('[data-tg]'))) {
      if (el.classList.contains('dis')) return true;
      el.classList.toggle('on');
      el.querySelector('span').textContent = el.classList.contains('on') ? '开' : '关';
      var ctl = el.getAttribute('data-ctl');
      if (ctl) (el.closest('.phone') || document).querySelectorAll('[data-ctlt="' + ctl + '"]').forEach(function (x) { x.classList.toggle('dis', el.classList.contains('on')); });
      return true;
    }
    if ((el = t.closest('[data-ck]'))) {
      if (!el.classList.contains('dis')) el.classList.toggle('on');
      if (el.parentNode.classList.contains('sld')) el.parentNode.classList.toggle('dis', el.classList.contains('on'));
      if (!el.closest('[data-go]')) return true;
    }
    if ((el = t.closest('[data-rd]'))) {
      if (el.classList.contains('dis')) return true;
      var grp = el.closest('.rgrp') || el.parentNode;
      grp.querySelectorAll('[data-rd]').forEach(function (r) { r.classList.toggle('on', r === el); });
      return true;
    }
    if ((el = t.closest('[data-flt]'))) {
      el.parentNode.querySelectorAll('[data-flt]').forEach(function (r) { r.classList.toggle('on', r === el); });
      return true;
    }
    if ((el = t.closest('[data-sw]'))) {
      el.parentNode.querySelectorAll('[data-sw]').forEach(function (r) { r.classList.toggle('on', r === el); });
      return true;
    }
    if ((el = t.closest('.star'))) { el.classList.toggle('on'); el.innerHTML = K.ic(el.classList.contains('on') ? 'E735' : 'E734'); return true; }
    if ((el = t.closest('[data-fill]'))) {
      var box = ph.querySelector('.cmp .tb');
      if (box) {
        box.classList.remove('ph');
        box.classList.add('focus');
        box.querySelector('.v').innerHTML = el.getAttribute('data-fill') + '<span class="caret"></span>';
        var send = ph.querySelector('.cmp .send');
        if (send) send.classList.remove('dis');
      }
      flash('已填进输入框（不会直接发出）');
      return true;
    }
    if ((el = t.closest('[data-go]'))) { show(el.getAttribute('data-go')); return true; }
    return false;
  }

  function bindViewer() {
    viewer = document.getElementById('viewer');
    stage = document.getElementById('stage');
    cap = document.getElementById('vcap');
    stage.addEventListener('click', function (e) { interact(e, stage.querySelector('.phone')); });
    stage.addEventListener('contextmenu', function (e) {
      var el = e.target.closest('[data-long]');
      if (el) { e.preventDefault(); show(el.getAttribute('data-long')); }
    });
    var holdTimer = null;
    stage.addEventListener('pointerdown', function (e) {
      var el = e.target.closest('[data-long]');
      if (!el) return;
      holdTimer = setTimeout(function () { show(el.getAttribute('data-long')); }, 550);
    });
    ['pointerup', 'pointerleave', 'pointercancel'].forEach(function (n) { stage.addEventListener(n, function () { clearTimeout(holdTimer); }); });
    cap.addEventListener('click', function (e) {
      var s = e.target.closest('[data-step]');
      if (!s) return;
      var cur = stack[stack.length - 1];
      var i = order.indexOf(cur) + parseInt(s.getAttribute('data-step'), 10);
      if (i >= 0 && i < order.length) { stack = []; show(order[i]); }
    });
    document.getElementById('vclose').addEventListener('click', close);
    viewer.addEventListener('click', function (e) { if (e.target === viewer) close(); });
    document.addEventListener('keydown', function (e) {
      if (!viewer.classList.contains('open')) return;
      if (e.key === 'Escape') close();
      if (e.key === 'Backspace') { e.preventDefault(); back(); }
      if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
        var cur = stack[stack.length - 1];
        var i = order.indexOf(cur) + (e.key === 'ArrowRight' ? 1 : -1);
        if (i >= 0 && i < order.length) { stack = []; show(order[i]); }
      }
    });

    document.getElementById('gallery').addEventListener('click', function (e) {
      var p = e.target.closest('.phone');
      if (!p) return;
      stack = [];
      show(p.getAttribute('data-id'));
    });
    document.addEventListener('click', function (e) {
      var o = e.target.closest('[data-open]');
      if (o) { e.preventDefault(); stack = []; show(o.getAttribute('data-open')); }
    });
  }

  /** ?theme=light&w=320|430&accent=E81123&nav=hw&only=<分组 id>，便于分享与无头截图。 */
  function applyParams() {
    function press(sel) { var b = document.querySelector(sel); if (b) b.click(); }
    if (params.bare) document.body.classList.add('bare');
    if (params.nocap) document.body.classList.add('nocap');
    if (params.theme) press('[data-theme="' + params.theme + '"]');
    if (params.w) press('[data-w="' + params.w + '"]');
    if (params.nav) press('[data-nav="' + params.nav + '"]');
    if (params.accent) {
      var hex = '#' + params.accent.replace('#', '').toUpperCase();
      var b = document.querySelector('[data-accent="' + hex + '"]');
      if (b) b.click(); else { state.accent = hex; applyAccent(); }
    }
  }

  /**
   * ?audit=1：在 320 / 360 / 430 三种宽度下，列出超出手机左右边缘、且没有被中间容器
   * （省略号、横向滚动条）有意裁切的元素。.app / .body 的裁切不算，那正是要抓的溢出。
   */
  function audit() {
    document.body.classList.add('audit');
    var phones = document.querySelectorAll('#gallery .phone'), lines = [];
    function label(el) {
      var c = typeof el.className === 'string' && el.className ? '.' + el.className.trim().split(/\s+/).join('.') : '';
      return el.tagName.toLowerCase() + c;
    }
    ['320', '360', '430'].forEach(function (w) {
      var b = document.querySelector('[data-w="' + w + '"]');
      if (b) b.click();
      var bad = 0;
      [].forEach.call(phones, function (ph) {
        var pr = ph.getBoundingClientRect(), hits = [];
        [].forEach.call(ph.querySelectorAll('*'), function (el) {
          var r = el.getBoundingClientRect();
          if (!r.width || !r.height) return;
          if (r.right <= pr.right + 1 && r.left >= pr.left - 1) return;
          for (var a = el.parentElement; a && a !== ph; a = a.parentElement) {
            if (a.classList.contains('app') || a.classList.contains('body')) continue;
            if (getComputedStyle(a).overflowX !== 'visible') return;
          }
          hits.push(el);
        });
        hits = hits.filter(function (el) { return !hits.some(function (o) { return o !== el && o.contains(el); }); });
        if (!hits.length) return;
        bad++;
        lines.push('[' + w + '] ' + ph.getAttribute('data-id') + ' :: ' + hits.slice(0, 3).map(function (el) {
          var r = el.getBoundingClientRect();
          return label(el) + ' +' + Math.round(Math.max(r.right - pr.right, pr.left - r.left)) + 'px "' + (el.textContent || '').trim().slice(0, 20) + '"';
        }).join(' | ') + (hits.length > 3 ? ' (+' + (hits.length - 3) + ')' : ''));
      });
      lines.unshift('w=' + w + ' phones=' + phones.length + ' overflow=' + bad);
    });
    var b0 = document.querySelector('[data-w="' + (params.w || '360') + '"]');
    if (b0) b0.click();
    var pre = document.createElement('pre');
    pre.id = 'audit';
    pre.textContent = 'AUDIT\n' + lines.join('\n');
    document.body.insertBefore(pre, document.body.firstChild);
  }

  function init() {
    renderGallery();
    renderCoverage();
    bindToolbar();
    bindViewer();
    applyAccent();
    applyParams();
    if (params.audit) audit();
    var h = location.hash.replace('#', '');
    if (h && byId[h]) show(h);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
})();
