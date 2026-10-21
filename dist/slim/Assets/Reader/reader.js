/* EBookStudio 阅读器前端。
 *
 * 职责：把宿主给的章节清单渲染出来，并把用户的位置变化回报给宿主。
 * 用 iframe 装章节而不是 innerHTML：书的 CSS 污染不到外壳，
 * 且 iframe 的滚动位置天然独立，便于定位（设计文档 8.2）。
 *
 * 协议见设计文档 8.1。信封 { v, id, type, payload }，v 不匹配就拒绝。
 */
(function () {
  'use strict';

  var PROTOCOL_VERSION = 1;

  var state = {
    manifest: null,
    /* T6-2：'epub' | 'txt' | 'pdf'。kind='pdf' 时正文区那个 iframe 直接指到原始 PDF，
       工具栏切到 PDF 项目集（旋转/布局/目录置灰，页码框生效）。 */
    kind: 'epub',
    pdf: null,
    index: 0,
    pendingScrollTop: 0,
    pendingLocator: null,
    pendingFingerprint: null,
    lastReportMs: 0,
    trailingTimer: null,
    theme: {
      id: 'light',
      background: '#FFFFFF',
      foreground: '#1F1F1F',
      link: '#0067C0'
    },
    /* 用户自定义主题（T4-2）：底/字/链接三色，可改，落到 Config.json 的 reader.customTheme.* */
    custom: {
      bg: '#20242B',
      fg: '#D8DEE9',
      link: '#88C0D0'
    },
    typography: {
      fontSize: 17,
      lineHeight: 1.75,
      fontFamily: '"Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif',
marginH: 32,
      marginV: 24,
      /* T4-3 排版设置：页边距按视口百分比；段距/缩进用 em；分栏只作用于滚动模式 */
      marginTop: 5,
      marginBottom: 5,
      marginLeft: 5,
      marginRight: 5,
      paraSpacing: 0.8,
      textIndent: 2,
      columns: 1,
      columnGap: 24,
      columnRule: false
    },
    /* 阅读模式：'scroll'（默认，滚动）| 'paged'（分页，T4-1）。
       默认滚动是计划 T4-1 的明确要求：审计实测 Chromium 的 CSS 多列分页不可靠，
       所以分页是"可选模式"，不是默认模式。 */
    mode: 'scroll',
    pageIndex: 0,
    pageCount: 1,
    /* 'last' = 打开新章后停在末页（从下一页往回翻时用）；数字 = 停在第几页 */
    pendingPage: null,
    /* T4-4：目录项带的章内锚点，等这一章渲染完再滚过去 */
    pendingFragment: null,
    lastPaginateMs: 0,
    /* T5-3：宿主指定"跳到这一章后高亮第一个命中" */
    pendingFind: null, bookTotal: null, bookCountText: null,
    /* T6-5：书签与笔记。宿主在 reader.load / reader.annotations 里下发；
       selPending 是"弹条打开时锁住的那次选区"——点弹条按钮时焦点与选区会丢，
       所以动作必须用锁住的那份（quote + anchor），不能现取 window.getSelection()。 */
    bookmarks: [],
    notes: [],
    selPending: null,
    /* 'bookmarks' | 'notes'：面板只显示一类（用户要求书签页与笔记页分开）。 */
    panelMode: 'bookmarks',
    /* 笔记面板的状态：panelNoteId 非空 = 改已有笔记；panelDraft 非空 = 新建未保存。 */
    panelNoteId: null,
    panelDraft: null,
    panelStyle: 0,
    panelColor: 0
  };

  var frame = null;
  var noticeTimer = null;

  // ---------- 与宿主通信 ----------

  function post(type, payload) {
    if (!window.chrome || !window.chrome.webview) {
      return;
    }

    window.chrome.webview.postMessage({
      v: PROTOCOL_VERSION,
      id: null,
      type: type,
      payload: payload || {}
    });
  }

  function reportError(message, stack) {
    post('reader.error', {
      code: 'js',
      message: String(message),
      stack: stack ? String(stack) : null
    });
  }

  function notice(text) {
    var box = document.getElementById('notice');
    if (!box) {
      return;
    }

    box.textContent = text;
    box.hidden = false;

    if (noticeTimer) {
      clearTimeout(noticeTimer);
    }

    noticeTimer = setTimeout(function () {
      box.hidden = true;
    }, 8000);
  }

  // 外壳自身的脚本错误
  window.onerror = function (message, source, line, column, error) {
    reportError(message, error && error.stack
      ? error.stack
      : source + ':' + line + ':' + column);
    return false;
  };

  window.addEventListener('unhandledrejection', function (event) {
    var reason = event.reason;
    reportError('unhandledrejection: ' + (reason && reason.message ? reason.message : reason));
  });

  // ---------- 主题与排版 ----------

  /* ---------- 5 套主题（T4-2） ----------
     计划 T4-2 的 5 套：白（默认）/ 米黄 / 深灰 / 纯黑 / 护眼绿。
     一套主题同时管两处：
       1) 外壳（工具栏、页面底色）：改 documentElement 上的 CSS 变量 + data-theme 属性；
       2) 章节 iframe（正文底色、字色、链接色）：由 buildContentCss 生成并注入。
     深色主题下图片**不加任何滤镜**（计划明确要求：反色/变暗反而更难看）。 */
  var THEMES = [
    {
      id: 'light', name: '\u767d',
      shellBg: '#F3F3F3', shellFg: '#1F1F1F', shellFgDim: '#6B6B6B', shellBorder: '#E0E0E0',
      shellBtnBg: '#FFFFFF', shellBtnHover: '#F0F0F0', shellAccent: '#0067C0',
      bg: '#FFFFFF', fg: '#1A1A1A', link: '#0066CC', selection: 'rgba(0, 103, 192, 0.28)'
    },
    {
      id: 'sepia', name: '\u7C73\u9EC4',
      shellBg: '#F0E6CF', shellFg: '#5B4636', shellFgDim: '#7A6A55', shellBorder: '#DCCFB0',
      shellBtnBg: '#FAF3E4', shellBtnHover: '#EDE2C8', shellAccent: '#8B5A2B',
      bg: '#F4ECD8', fg: '#5B4636', link: '#8B5A2B', selection: 'rgba(139, 90, 43, 0.25)'
    },
    {
      id: 'dark', name: '\u6DF1\u7070',
      shellBg: '#202020', shellFg: '#F0F0F0', shellFgDim: '#A0A0A0', shellBorder: '#383838',
      shellBtnBg: '#2D2D2D', shellBtnHover: '#3A3A3A', shellAccent: '#4CC2FF',
      bg: '#1C1C1C', fg: '#C8C8C8', link: '#6CB6FF', selection: 'rgba(108, 182, 255, 0.28)'
    },
    {
      id: 'night', name: '\u7EAF\u9ED1',
      shellBg: '#000000', shellFg: '#8A8A8A', shellFgDim: '#6E6E6E', shellBorder: '#2A2A2A',
      shellBtnBg: '#141414', shellBtnHover: '#1F1F1F', shellAccent: '#5A8FBF',
      bg: '#000000', fg: '#8A8A8A', link: '#5A8FBF', selection: 'rgba(90, 143, 191, 0.30)'
    },
    {
      id: 'green', name: '\u62A4\u773C\u7EFF',
      shellBg: '#CCE8CF', shellFg: '#22361F', shellFgDim: '#4A6B45', shellBorder: '#A9CDA9',
      shellBtnBg: '#DDF0DD', shellBtnHover: '#C3E2C6', shellAccent: '#2D5A27',
      bg: '#CCE8CF', fg: '#22361F', link: '#2D5A27', selection: 'rgba(45, 90, 39, 0.25)'
    }
  ];

  /* ---------- 自定义主题的派生色：不管用户选什么颜色，外框都要看得见 ----------
     用户 2026-10-21 反馈：自定义主题下工具栏的下拉框、三个调色框、章号/页码框都"看不到边框"，
     因为 customTheme 里 shellBorder / shellBtnBg 被直接设成了用户选的**底色**（和背景同色）。
     这里改成"用前景色往底色上叠"得到边框/按钮色，并保证与底色的对比度不低于下限：
     即使用户选的前景色贴着底色，也会自动往黑/白一侧推，边框始终可见。 */
  function parseColor(value) {
    var text = String(value || '').trim();
    var hex = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(text);

    if (hex) {
      var h = hex[1];

      if (h.length === 3) {
        h = h.charAt(0) + h.charAt(0) + h.charAt(1) + h.charAt(1) + h.charAt(2) + h.charAt(2);
      }

      return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
    }

    /* getComputedStyle 读回来的是 "rgb(r, g, b)"，冒烟对比度断言要用 */
    var rgb = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)/i.exec(text);

    return rgb ? [Number(rgb[1]), Number(rgb[2]), Number(rgb[3])] : null;
  }

  function rgbToHex(rgb) {
    var hex = '#';

    for (var i = 0; i < 3; i++) {
      var v = Math.round(Math.max(0, Math.min(255, rgb[i])));
      hex += (v < 16 ? '0' : '') + v.toString(16);
    }

    return hex.toUpperCase();
  }

  /* t = 混入 b 的比例（0..1）；解析不了就原样返回 a。 */
  function mixHex(a, b, t) {
    var ca = parseColor(a);
    var cb = parseColor(b);

    if (!ca || !cb) { return a; }

    var k = Math.max(0, Math.min(1, t));

    return rgbToHex([
      ca[0] + (cb[0] - ca[0]) * k,
      ca[1] + (cb[1] - ca[1]) * k,
      ca[2] + (cb[2] - ca[2]) * k
    ]);
  }

  /* 相对亮度（sRGB 线性化，和 WCAG 同一套），用来算对比度。 */
  function colorLuminance(value) {
    var rgb = parseColor(value);

    if (!rgb) { return 0; }

    var weights = [0.2126, 0.7152, 0.0722];
    var sum = 0;

    for (var i = 0; i < 3; i++) {
      var c = rgb[i] / 255;
      sum += weights[i] * (c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
    }

    return sum;
  }

  function contrastRatio(a, b) {
    var la = colorLuminance(a);
    var lb = colorLuminance(b);
    var hi = Math.max(la, lb);
    var lo = Math.min(la, lb);

    return (hi + 0.05) / (lo + 0.05);
  }

  /* 与底色拉开对比的派生色：先用前景色叠 ratio；对比度不够就退回黑/白一侧。 */
  function chromeColor(bg, fg, ratio, minContrast) {
    var mixed = mixHex(bg, fg, ratio);

    if (contrastRatio(mixed, bg) >= (minContrast || 1.35)) { return mixed; }

    var toward = colorLuminance(bg) > 0.5 ? '#000000' : '#FFFFFF';

    return mixHex(bg, toward, Math.max(0.35, ratio));
  }

  /* 用户自定义主题：颜色来自 state.custom（宿主从 Config.json 读出来下发）。 */
  function customTheme() {
    var c = state.custom || { bg: '#20242B', fg: '#D8DEE9', link: '#88C0D0' };

    return {
      id: 'custom', name: '自定义',
      shellBg: c.bg,
      shellFg: c.fg,
      shellFgDim: mixHex(c.fg, c.bg, 0.2),
      /* 边框 / 按钮底色全部由"前景色叠到背景上"派生，并保证最低对比度：
         以前这三个直接等于 c.bg，和背景同色 → 自定义主题下所有控件框都像没有边框。 */
      shellBorder: chromeColor(c.bg, c.fg, 0.32, 1.35),
      shellBorderStrong: chromeColor(c.bg, c.fg, 0.6, 1.6),
      shellBtnBg: chromeColor(c.bg, c.fg, 0.08, 1.12),
      shellBtnHover: chromeColor(c.bg, c.fg, 0.18, 1.25),
      shellIconFg: c.fg,
      shellAccent: c.link,
      bg: c.bg, fg: c.fg, link: c.link,
      selection: 'rgba(136, 192, 208, 0.28)'
    };
  }

  /* 改自定义主题的颜色：立即生效，并回报宿主落盘（notify=false 是宿主下发时用）。 */
  function setCustomColors(bg, fg, link, notify) {
    var current = state.custom || { bg: '#20242B', fg: '#D8DEE9', link: '#88C0D0' };

    state.custom = {
      bg: bg || current.bg,
      fg: fg || current.fg,
      link: link || current.link
    };

    if (currentThemeId() === 'custom') {
      applyTheme('custom', false);
    }

    updateThemeUi();

    if (notify !== false) {
      post('reader.customThemeChanged', state.custom);
    }
  }

  function themeById(id) {
    if (id === 'custom') {
      return customTheme();
    }

    for (var i = 0; i < THEMES.length; i++) {
      if (THEMES[i].id === id) {
        return THEMES[i];
      }
    }

    return THEMES[0];
  }

  function currentThemeId() {
    return state.theme && state.theme.id ? state.theme.id : 'light';
  }

  /* 应用主题：外壳与章节 iframe 一起换。
     notify=false 用于"宿主下发的主题"，避免回声成 themeChanged。 */
  function applyTheme(id, notify) {
    var palette = themeById(id);
    var root = document.documentElement;

    state.theme = {
      id: palette.id,
      background: palette.bg,
      foreground: palette.fg,
      link: palette.link,
      selection: palette.selection
    };

    /* 属性方案（计划 T4-2 关键点 1）：改 data-theme 最快，不用重新加载任何东西。 */
    root.setAttribute('data-theme', palette.id);

    /* 外壳的 CSS 变量（reader.css 里用的就是这些） */
    root.style.setProperty('--shell-bg', palette.shellBg);
    root.style.setProperty('--shell-fg', palette.shellFg);
    root.style.setProperty('--shell-fg-dim', palette.shellFgDim);
    root.style.setProperty('--shell-border', palette.shellBorder);
    root.style.setProperty('--shell-btn-bg', palette.shellBtnBg);
    root.style.setProperty('--shell-btn-hover', palette.shellBtnHover);
    root.style.setProperty('--shell-accent', palette.shellAccent);
    /* T7-3：这两个变量一直有默认值但 applyTheme 从没写过 → 主题/系统不一致时页码框、缩放图标对比度错 */
    root.style.setProperty('--shell-border-strong',
      palette.shellBorderStrong || (palette.id === 'dark' || palette.id === 'night' ? '#9A9A9A' : '#8C8C8C'));
    root.style.setProperty('--shell-icon-fg', palette.shellIconFg || palette.shellFgDim);

    applyContentCss();
    updateThemeUi();

    /* T6-2B：PDF 的页面是宿主按主题渲染好送进来的（不是 CSS），底色变了要么换容器底色、
       要么按新的 theme 值重新请求可见页；applyPdfTheme 自己判断是哪种。 */
    if (isPdf()) { applyPdfTheme(); }

    if (notify !== false) {
      post('reader.themeChanged', { themeId: palette.id });
    }
  }

/* 界面上改了设置项：套用 + 上报宿主落盘（和宿主下发走同一套 applyTypography）。 */
  function pushTypographyFromUi() {
    var value = function (id, fallback) {
      var input = document.getElementById(id);
      return input ? input.value : fallback;
    };

    applyTypography({
      fontSize: Number(value('tyFontSizeNum', state.typography.fontSize)),
      lineHeight: Number(value('tyLineHeightNum', state.typography.lineHeight)),
      marginTop: Number(value('tyMarginTopNum', marginPctOf(state.typography, 'Top'))),
      marginBottom: Number(value('tyMarginBottomNum', marginPctOf(state.typography, 'Bottom'))),
      marginLeft: Number(value('tyMarginLeftNum', marginPctOf(state.typography, 'Left'))),
      marginRight: Number(value('tyMarginRightNum', marginPctOf(state.typography, 'Right'))),
      paraSpacing: Number(value('tyParaSpacingNum', paraSpacingOf(state.typography))),
      textIndent: Number(value('tyTextIndentNum', textIndentOf(state.typography))),
      columns: Number(value('tyColumns', columnsOf(state.typography))),
      columnGap: Number(value('tyColumnGapNum', columnGapPxOf(state.typography))),
      columnRule: (function () { var c = document.getElementById('tyColumnRule'); return c ? c.checked : columnRuleOn(state.typography); }()),
      fontFamily: value('tyFontFamily', state.typography.fontFamily)
    });
  }

  /* 应用一套排版设置：先记位置 -> 改值 -> 重排 -> 跳回 -> 上报宿主。 */
  function applyTypography(values) {
    var v = values || {};
    var keptPercent = capturePercentInChapter();
    var y = state.typography;

    if (v.fontSize) { y.fontSize = Math.min(36, Math.max(12, Math.round(Number(v.fontSize)))); }
    if (v.lineHeight) { y.lineHeight = Math.min(2.5, Math.max(1.2, Number(v.lineHeight))); }
    if (v.marginTop !== undefined) { y.marginTop = marginPctOf({ marginTop: v.marginTop }, 'Top'); }
    if (v.marginBottom !== undefined) { y.marginBottom = marginPctOf({ marginBottom: v.marginBottom }, 'Bottom'); }
    if (v.marginLeft !== undefined) { y.marginLeft = marginPctOf({ marginLeft: v.marginLeft }, 'Left'); }
    if (v.marginRight !== undefined) { y.marginRight = marginPctOf({ marginRight: v.marginRight }, 'Right'); }
    if (v.paraSpacing !== undefined) { y.paraSpacing = paraSpacingOf({ paraSpacing: v.paraSpacing }); }
    if (v.textIndent !== undefined) { y.textIndent = textIndentOf({ textIndent: v.textIndent }); }
    if (v.columns !== undefined) { y.columns = columnsOf({ columns: v.columns }); }
    if (v.columnGap !== undefined) { y.columnGap = columnGapPxOf({ columnGap: v.columnGap }); }
    if (v.columnRule !== undefined) { y.columnRule = columnRuleOn({ columnRule: v.columnRule }); }

    /* 分页模式：列数/间距变了要重排页框（列宽、页数、当前页位移）。
       但拖动滑块时 input 会连发几十次，每次都重排整章会把界面卡死（用户反馈"滑块拖一点就卡住"），
       所以这里节流：拖动过程中只改 CSS（浏览器自己重排），停手 120ms 后再同步页框与页数。 */
    if (isPaged()) {
      applyPagedCss();
      schedulePageRelayout();
    }
    if (v.fontFamily) { y.fontFamily = wrapFontFamily(v.fontFamily); }

    applyContentCss();

    if (isPaged()) {
      onViewportResize();
      applyPercentInChapter(keptPercent);
    }

    updateTypographyUi();
    post('reader.typographyChanged', {
      fontSize: String(y.fontSize),
      lineHeight: String(y.lineHeight),
      fontFamily: String(y.fontFamily),
      marginTop: String(marginPctOf(y, 'Top')),
      marginBottom: String(marginPctOf(y, 'Bottom')),
      marginLeft: String(marginPctOf(y, 'Left')),
      marginRight: String(marginPctOf(y, 'Right')),
      paraSpacing: String(paraSpacingOf(y)),
      textIndent: String(textIndentOf(y)),
      columns: String(columnsOf(y)),
      columnGap: String(columnGapPxOf(y)),
      columnRule: String(columnRuleOn(y))
    });
  }

  /* 把当前排版设置填回界面控件。 */
  function updateTypographyUi() {
    var y = state.typography || {};
    var map = {
      tyFontSize: y.fontSize,
      tyLineHeight: y.lineHeight,
      tyMarginTop: marginPctOf(y, 'Top'),
      tyMarginBottom: marginPctOf(y, 'Bottom'),
      tyMarginLeft: marginPctOf(y, 'Left'),
      tyMarginRight: marginPctOf(y, 'Right'),
      tyParaSpacing: paraSpacingOf(y),
      tyTextIndent: textIndentOf(y),
      tyColumnGap: columnGapPxOf(y)
    };

    for (var id in map) {
      if (Object.prototype.hasOwnProperty.call(map, id)) {
        var text = map[id] === undefined ? '' : String(map[id]);
        var input = document.getElementById(id);

        /* 正在拖的控件不要写回值，否则拇指会被按回去（用户反馈"滑块拖不动"）。 */
        if (input && input.value !== text && document.activeElement !== input) { input.value = text; }

        var number = document.getElementById(id + 'Num');

        if (number && number.value !== text && document.activeElement !== number) { number.value = text; }
      }
    }

    var cols = document.getElementById('tyColumns');

    if (cols && cols.value !== String(columnsOf(y))) { cols.value = String(columnsOf(y)); }

    var rule = document.getElementById('tyColumnRule');

    if (rule) { rule.checked = columnRuleOn(y); }

    /* 分栏间距/竖线只对多栏有意义：单栏时藏起来 */
    var multi = columnsOf(y) > 1;
    var gapRow = document.getElementById('tyColumnGapRow');
    var ruleRow = document.getElementById('tyColumnRuleRow');

    if (gapRow) { gapRow.hidden = !multi; }
    if (ruleRow) { ruleRow.hidden = !multi; }
    var font = document.getElementById('tyFontFamily');

    if (font && y.fontFamily) {
      var first = String(y.fontFamily).split(',')[0].replace(/"/g, '').trim();
      var has = false;

      for (var i = 0; i < font.options.length; i++) {
        if (font.options[i].value === first) { has = true; break; }
      }

      if (!has && first) {
        var opt = document.createElement('option');
        opt.value = first;
        opt.textContent = first + '（未安装，回退到默认字体）';
        font.appendChild(opt);
      }

      if (font.value !== first) { font.value = first; }
    }
  }

  function updateThemeUi() {
    var select = document.getElementById('themeSelect');

    if (select && select.value !== currentThemeId()) {
      select.value = currentThemeId();
    }

    /* 自定义主题才显示三个取色器，并把当前颜色同步进去 */
    var panel = document.getElementById('customColors');

    if (panel) {
      panel.hidden = currentThemeId() !== 'custom';
    }

    var colors = state.custom || {};
    var map = { customBg: colors.bg, customFg: colors.fg, customLink: colors.link };

    for (var id in map) {
      if (Object.prototype.hasOwnProperty.call(map, id)) {
        var input = document.getElementById(id);

        if (input && map[id] && input.value !== map[id]) {
          input.value = map[id];
        }
      }
    }

    /* 三个取色器露出来 / 收起来会改变工具栏控件总宽 → 一行/两行的阈值跟着变，重新定一次。
       EPUB / TXT 走自己的宽度阈值；PDF / 漫画的两行结构由实测高度决定，而自定义主题下
       三个色框现在也参与第一行排版 —— 宽窗可能因为多出这三个色框而放不下，必须退回两行，
       所以这里也要重跑一次（非 PDF 时它自己会提前返回，不会误加 ets-pdf-wide）。 */
    updateEpubToolbarRowsForWidth();
    pdfUpdateToolbarRowsForWidth();
  }

  /* 统一成 #RRGGBB，方便冒烟断言（getComputedStyle 返回的是 rgb(...)）。 */
  function cssColorToHex(value) {
    var matched = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(value || '');

    if (!matched) {
      return value || '';
    }

    var parts = [matched[1], matched[2], matched[3]];
    var hex = '#';

    for (var i = 0; i < parts.length; i++) {
      var part = parseInt(parts[i], 10).toString(16).toUpperCase();
      hex += part.length === 1 ? '0' + part : part;
    }

    return hex;
  }

  /* 生成注入 iframe 的样式。
   *
   * 为什么要 !important 且把 color 强制成 inherit：
   * 大量 EPUB 把正文颜色硬编码成 #000，深色主题下就变成"黑底黑字"完全没法看。
   * 自用工具里可读性优先于"尊重原书配色"（执行计划 T1-5 判据 4 允许
   * 与 Calibre 阅读器观感不同，但不允许没法看）。
   */
  function buildContentCss() {
    var t = state.theme;
    var y = state.typography;

    return [
      'html, body {',
      '  background: ' + t.background + ' !important;',
      '  color: ' + t.foreground + ' !important;',
      '  font-family: ' + y.fontFamily + ' !important;',
      '  font-size: ' + y.fontSize + 'px !important;',
      '  line-height: ' + y.lineHeight + ' !important;',
      '  padding: ' + contentMarginsCss() + ' !important;',
      '  margin: 0 !important;',
      '  overflow-wrap: break-word !important;',
      '  word-break: break-word !important;',
      '}',
      /* 以下每条规则都必须**自己一块**（上一版把它们塞进了 html, body {} 里面，
         CSS 解析器按垃圾丢掉 -> 分栏/段距/首行缩进全都静默失效）。 */
      'body p, body blockquote { margin: 0 0 ' + paraSpacingOf(y) + 'em !important; text-indent: ' + textIndentOf(y) + 'em !important; }',
      isPaged() ? '' : ('body { column-count: ' + columnsOf(y) + ' !important; column-gap: ' + columnGapPxOf(y) + 'px !important;'
        + ' column-fill: balance !important; column-rule: ' + (columnsOf(y) > 1 && columnRuleOn(y)
          ? ('1px solid ' + colorWithAlpha(t.foreground, 0.35)) : 'none') + ' !important; }'),
      /* 多栏时最容易出的问题：某块内容的 min-content 太宽（大图/长单词/浮动首字），把栏撑破 ->
         三栏装不下就横向溢出，看起来"只显示两栏"。用这几条兜住。 */
      columnsOf(y) > 1 && !isPaged() ? 'body, body * { min-width: 0 !important; box-sizing: border-box !important; }' : '',
      columnsOf(y) > 1 && !isPaged() ? 'body { width: 100% !important; max-width: 100% !important; overflow-wrap: anywhere !important; }' : '',
      columnsOf(y) > 1 && !isPaged() ? 'body img, body table, body pre, body svg { max-width: 100% !important; }' : '',
      'body * { color: inherit !important; }',
      'body p, body div, body li { color: inherit !important; }',
      'img, svg, video, canvas {',
      '  max-width: 100% !important;',
      '  height: auto !important;',
      // T4-2 判据 5：深色主题下图片不反色、不变暗  默认不加任何滤镜
      '  filter: none !important;',
      '}',
      /* T7-2 第 14 项：图片 404 时显示占位块（onerror 会打上 .ets-img-missing），
         不能让读者看到的是一块白屏或一个破图标。 */
      'img.ets-img-missing {',
      '  display: inline-block !important;',
      '  min-width: 96px !important;',
      '  min-height: 96px !important;',
      '  padding: 6px !important;',
      '  background: ' + colorWithAlpha(t.foreground, 0.08) + ' !important;',
      '  border: 1px dashed ' + colorWithAlpha(t.foreground, 0.35) + ' !important;',
      '  color: ' + t.foreground + ' !important;',
      '  font-size: 12px !important;',
      '}',
      'table { max-width: 100% !important; }',
      'a, a * { color: ' + t.link + ' !important; }',
      /* 【用户 2026-10-12 要求】查找命中的配色：所有命中黄底；当前定位到的那一处红底白字。
         注意必须写在这里（reader.css 只管外壳文档，作用不到章节 iframe），而且必须 !important
         才压得过上面那条 body * { color: inherit !important; }。 */
      'mark.ets-find { background: #FFE066 !important; color: #1F1F1F !important; }',
      'mark.ets-find-current { background: #E53935 !important; color: #FFFFFF !important; }',

      /* T6-5 补短：七种标记的显示样式。
         **必须注入 iframe**——reader.css 只作用于外壳文档，章节正文在 iframe 里，
         写在外壳的规则对内文一个字都不生效（用户 2026-10-18 实测：四种标记全显示成
         <mark> 的浏览器默认黄底，看起来都像高亮）。
         颜色走 CSS 变量（--ets-bg / --ets-fg / --ets-line），由 applyMarkVisual 写到元素上；
         线类必须 !important 压过 body * { color: inherit !important }，
         且 background 要显式 transparent，否则会顶着 UA 的黄底。 */
      'mark.ets-note { background: var(--ets-bg, #B9E6FF) !important; color: inherit !important; border-radius: 2px; }',
      'mark.ets-mark-underline, mark.ets-mark-wavy, mark.ets-mark-dashed, mark.ets-mark-strike {'
        + ' background: transparent !important; color: inherit !important; }',
      'mark.ets-mark-underline { text-decoration: underline !important; text-decoration-thickness: 2px;'
        + ' text-underline-offset: 2px; text-decoration-color: var(--ets-line, currentColor) !important; }',
      'mark.ets-mark-wavy { text-decoration: underline wavy !important; text-decoration-thickness: 1.5px;'
        + ' text-underline-offset: 2px; text-decoration-color: var(--ets-line, #C2410C) !important; }',
      'mark.ets-mark-dashed { text-decoration: underline dashed !important; text-decoration-thickness: 2px;'
        + ' text-underline-offset: 2px; text-decoration-color: var(--ets-line, #B45309) !important; }',
      'mark.ets-mark-strike { text-decoration: line-through !important; text-decoration-thickness: 2px;'
        + ' text-decoration-color: var(--ets-line, currentColor) !important; }',
      'mark.ets-mark-background { background: var(--ets-bg, #FFE066) !important; color: inherit !important; border-radius: 2px; }',
      'mark.ets-mark-foreground { background: transparent !important; color: var(--ets-fg, inherit) !important; }',
      '::selection { background: ' + (t.selection || 'rgba(0, 103, 192, 0.28)') + '; }'
    ].join('\n');
  }

  function applyContentCss() {
    var doc = frame && frame.contentDocument;
    if (!doc) {
      return;
    }

    var head = doc.head || doc.documentElement;
    if (!head) {
      return;
    }

    var style = doc.getElementById('ets-reader-style');
    if (!style) {
      style = doc.createElement('style');
      style.id = 'ets-reader-style';

      // 必须排在书自带样式之后才压得住，所以追加而不是插到最前
      head.appendChild(style);
    }

    // T4-2 判据 2：书自带的 CSS 可能写 body { background: #fff !important }，
    // 甚至在我之后才注入。主题样式必须**始终排在 head 最后**才压得住。
    if (head.lastElementChild !== style) {
      head.appendChild(style);
    }

    style.textContent = buildContentCss();
  }

  // ---------- 文本编码（T4-5） ----------
  //
  // TXT 的编码是宿主识别出来的，识别不准时用户可以在工具栏里手选；
  // 选完宿主会按新编码重建缓存并重新下发 load（立即重排）。
  function applyTextEncodingUi(payload) {
    var select = document.getElementById('encodingSelect');
    var hint = document.getElementById('encodingHint');

    if (!select) {
      return;
    }

    var options = (payload && payload.textEncodingOptions) || [];
    var current = (payload && payload.textEncoding) || '';

    if (options.length === 0 || !current) {
      select.hidden = true;

      if (hint) {
        hint.hidden = true;
      }

      return;
    }

    select.innerHTML = '';

    options.forEach(function (name) {
      var option = document.createElement('option');

      option.value = name;
      option.textContent = name;
      select.appendChild(option);
    });

    select.value = current;
    select.hidden = false;

    var preview = (payload && payload.textPreview) || '';
    var confident = !payload || payload.textEncodingConfident !== false;

    if (hint) {
      hint.hidden = false;
      hint.textContent = confident
        ? current
        : ('编码可能不准，请选择（预览：' + preview.slice(0, 60) + '）');
    }

    select.title = preview ? ('前 500 字预览：' + preview) : '文本编码（TXT 专用）';
  }

  // ---------- 目录（T4-4） ----------
  //
  // 目录数据是宿主解析好的（EPUB2 的 NCX / EPUB3 的 nav，解析与降级都在引擎里做），
  // 这里只管画出来、点它跳章、标出"正读到哪一条"。
  // 点条目**不能**直接改 iframe.src：章节要宿主先物化（见 requestChapter 的注释）。

  function toggleTocPanel(open) {
    var panel = document.getElementById('tocPanel');

    if (!panel) {
      return false;
    }

    var next = typeof open === 'boolean' ? open : panel.hidden;

    if (next) {
      /* 两个浮层叠在一起很难看：开目录就收起排版面板 */
      var typePanel = document.getElementById('typePanel');

      if (typePanel) {
        typePanel.hidden = true;
      }

      renderToc();
    }

    panel.hidden = !next;

    var button = document.getElementById('btnToc');

    if (button) {
      button.classList.toggle('active', next);
    }

    return next;
  }

  function closeTocPanel() {
    return toggleTocPanel(false);
  }

  /* 每一画成一行：缩进按 level（1 起，最多 3 级）；跳不了的（EPUB3 的分组标题）不挂点击。
     T6-2B：PDF 模式下同一个抽屉画 PDF 自带的 outline——认 entry.page（0 基，-1 = 分组标题），
     EPUB / TXT 认 entry.spineIndex。两边的 DOM、样式、点击行为都是同一套。 */
  /* 编辑保存后宿主推回的新目录：重画目录抽屉，并保持当前章高亮。
     spine 变了（增删章）时跳转目标可能对不上，提示重开一次。 */
  function onTocUpdated(payload) {
    if (!state.manifest) { return; }

    if (payload && payload.toc) {
      state.manifest.toc = payload.toc;
      state.tocFallback = !!payload.tocFallback;
      renderToc();
      markCurrentToc();
      notice(payload.spineChanged
        ? '目录已保存；章节数变了，重新打开这本书后跳转才准。'
        : '目录已保存，已刷新。');
    }
  }

  function renderToc() {
    var host = document.getElementById('tocList');

    if (!host) {
      return 0;
    }

    var pdfMode = isPdf();
    var entries = pdfMode
      ? ((state.pdf && state.pdf.outline) || [])
      : ((state.manifest && state.manifest.toc) || []);
    var count = 0;

    host.innerHTML = '';

    var add = function (list, depth) {
      list.forEach(function (entry) {
        var level = Math.max(1, Math.min(3, entry.level || depth));
        var page = pdfMode ? Math.floor(Number(entry.page)) : -1;
        var spine = pdfMode ? -1 : entry.spineIndex;
        var jumpable = pdfMode
          ? (isFinite(page) && page >= 0)
          : (typeof spine === 'number' && spine >= 0);
        var item = document.createElement('div');

        item.className = 'tocItem' + (jumpable ? '' : ' tocGroup');
        item.setAttribute('data-level', String(level));
        item.setAttribute('data-spine', String(pdfMode ? -1 : (jumpable ? spine : -1)));
        item.setAttribute('data-page', String(jumpable ? page : -1));
        item.setAttribute('data-fragment', entry.fragment || '');
        item.style.paddingLeft = (8 + (level - 1) * 14) + 'px';
        item.title = entry.title || '';

        if (pdfMode) {
          item.textContent = entry.title || (jumpable ? ('第 ' + (page + 1) + ' 页') : '（分组）');
        } else {
          item.textContent = entry.title || (jumpable ? ('第 ' + (spine + 1) + ' 章') : '（分组）');
        }

        if (jumpable) {
          item.addEventListener('click', function () {
            if (pdfMode) { pdfJumpFromToc(page); }
            else { jumpToTocEntry(entry); }
          });
        }

        host.appendChild(item);
        count++;

        if (entry.children && entry.children.length) {
          count += add(entry.children, depth + 1);
        }
      });
    };

    add(entries, 1);

    var hint = document.getElementById('tocHint');

    if (hint) {
      hint.textContent = count === 0
        ? '这本书没有目录'
        : (!pdfMode && state.tocFallback ? '本书目录不可用，按章节顺序显示' : '');
    }

    markCurrentToc();
    return count;
  }

  /* 标出"正读到哪条"：EPUB 是同一个 spine 有多条（带 fragment）时取第一条；
     PDF 是页码命中最近的一条（当前页落在两条目录项之间时，取前面那条）。 */
  function markCurrentToc() {
    var host = document.getElementById('tocList');

    if (!host) {
      return;
    }

    if (isPdf()) {
      markPdfToc();
      return;
    }

    var items = host.querySelectorAll('.tocItem');
    var marked = false;

    for (var i = 0; i < items.length; i++) {
      var spine = parseInt(items[i].getAttribute('data-spine') || '-1', 10);
      var isCurrent = !marked && spine >= 0 && spine === state.index;

      if (isCurrent) {
        marked = true;
      }

      items[i].classList.toggle('tocCurrent', isCurrent);
    }
  }

  /* 点目录：同章直接跳锚点；跨章要先请宿主物化（requestChapter），
     锚点存进 pendingFragment，等这一章渲染完（onFrameLoad -> apply）再落位。 */
  function jumpToTocEntry(entry) {
    if (!entry || typeof entry.spineIndex !== 'number' || entry.spineIndex < 0) {
      return false;
    }

    closeTocPanel();
    state.pendingFragment = entry.fragment || null;

    if (entry.spineIndex === state.index) {
      applyPendingFragment();
      reportLocation();
      return true;
    }

    requestChapter(entry.spineIndex);
    return true;
  }

  /* 章内锚点：滚动模式滚过去，分页模式算出锚点在第几页再翻过去。
     锚点不存在（书里本来就没有那个 id）就当没事发生：留在章首，不算错误。 */
  function applyPendingFragment() {
    var fragment = state.pendingFragment;

    state.pendingFragment = null;

    if (!fragment) {
      return false;
    }

    var doc = contentDoc();
    var target = null;

    if (doc) {
      try {
        target = doc.getElementById(fragment) || (doc.getElementsByName(fragment) || [])[0] || null;
      } catch (ignored) {
        target = null;
      }
    }

    if (!target || !target.getBoundingClientRect) {
      return false;
    }

    if (isPaged()) {
      setPage(pageOfElement(target));
      updateModeUi();
      return true;
    }

    var win = contentWin();
    var rect = target.getBoundingClientRect();
    var current = win ? (win.scrollY || win.pageYOffset || 0) : 0;

    scrollTo(current + rect.top - 4);
    return true;
  }

  /* 元素落在第几页：元素与正文流一起平移，所以"相对流左边缘的距离"翻页前后不变。 */
  function pageOfElement(element) {
    var flow = flowElement();

    if (!flow || !element) {
      return 0;
    }

    var stride = pageStride();

    if (!(stride > 0)) {
      return 0;
    }

    var offset = element.getBoundingClientRect().left - flow.getBoundingClientRect().left;
    var page = Math.floor(Math.max(0, offset) / stride);

    return Math.max(0, Math.min(measurePageCount() - 1, page));
  }

  // ---------- 与宿主协商的消息 ----------

  window.chrome.webview.addEventListener('message', function (event) {
    var message = event.data;

    if (!message || message.v !== PROTOCOL_VERSION) {
      reportError('阅读器协议版本不匹配，请更新程序。');
      return;
    }

    var payload = message.payload || {};

    switch (message.type) {
      /* T5-3：宿主发来的人话提示（查找替换的取消 / 失败 / 结果）。 */
      case 'reader.notice':
        notice(payload.message || '');
        break;

      /* T5-3：宿主已经跳到有命中的章，等这一章渲染完再高亮第一处。 */
      /* 全书计数回来了（匹配范围永远是全书）。 */
      case 'reader.findReplace.counted':
        state.bookTotal = payload.ok ? (payload.total || 0) : null;
        updateFindCount();
        break;

      case 'reader.findReplace.highlight':
        state.pendingFind = {
          text: payload.find || '',
          useRegex: !!payload.useRegex,
          matchCase: !!payload.matchCase
        };
        break;

      case 'reader.load':
        onLoad(payload);
        break;

      /* 目录抽屉的「编辑目录」保存成功：宿主重读并推回新目录，抽屉立刻重画。 */
      case 'reader.toc':
        onTocUpdated(payload);
        break;

      /* 编辑保存后书名/作者也变了（EPUB 正文标题是章节名，不受影响）。 */
      case 'reader.bookInfo':
        state.bookTitle = payload.title || state.bookTitle;
        state.bookAuthor = payload.author || state.bookAuthor;
        break;

      /* T6-5：宿主写库之后回推最新列表（加/删之后都靠它刷新面板与高亮）。 */
      case 'reader.annotations':
        onAnnotations(payload);
        break;

      /* T6-5：导出完成，宿主给出落盘路径。 */
      case 'reader.exported':
        onAnnotationsExported(payload);
        break;

      /* T6-2B：自绘引擎的页渲染回包。单页的 url（虚拟主机地址或 data: URL 都行）
         直接赋给 <img>.src；chars 是归一化到 0..1 的字符框，用来建透明文字层。 */
      case 'reader.pdfPageReady':
        onPdfPageReady(payload);
        break;

      case 'reader.pdfPageFailed':
        onPdfPageFailed(payload);
        break;

      /* T6-2B：PDF 全书搜索的结果。检索在宿主侧做（同一份 pdfium 文本层），
         我们负责跳到命中页 + 在 .pdfChar 上画黄底。 */
      case 'reader.pdfFindResults':
        onPdfFindResults(payload);
        break;

      case 'reader.gotoChapter':
        openChapter(payload.index, payload.scrollTop || 0);
        break;

      /* T5-2W 第三步：宿主刚在工作区里改好这一章（可视编辑保存）重新拉一次 HTML 并回到原位。 */
      case 'reader.reloadChapter':
        reloadChapter(payload.index, payload.percentInChapter, payload.token);
        break;

      case 'reader.setTheme':
        /* themeId = 整套主题（T4-2 用这个）；下面三个颜色是兼容老协议的单独覆盖 */
        if (payload.themeId) { applyTheme(payload.themeId, false); }
        if (payload.background) { state.theme.background = payload.background; }
        if (payload.foreground) { state.theme.foreground = payload.foreground; }
        if (payload.link) { state.theme.link = payload.link; }
        applyContentCss();

        /* T6-2B：单独覆盖底色（老协议）不会走 applyTheme，PDF 这边要自己跟一下。 */
        if (!payload.themeId && isPdf()) { applyPdfTheme(); }
        break;

      case 'reader.setMode':
        /* 宿主（ReaderWindow）下达的模式：notify=false，否则会回声成 modeChanged */
        setMode(payload.mode, false);
        break;

case 'reader.setTypography':
        /* 改排版会改变分页结果：先记下"当前在本章的相对位置"，重排后再跳回去（误差 <= 1 屏）。 */
        var keptPercent = capturePercentInChapter();

        if (payload.fontSize) { state.typography.fontSize = payload.fontSize; }
        if (payload.lineHeight) { state.typography.lineHeight = payload.lineHeight; }
        if (payload.fontFamily) { state.typography.fontFamily = wrapFontFamily(payload.fontFamily); }
        if (payload.marginH !== undefined) { state.typography.marginH = payload.marginH; }
        if (payload.marginV !== undefined) { state.typography.marginV = payload.marginV; }
        if (payload.marginPercent !== undefined) { state.typography.marginPercent = payload.marginPercent; }
        if (payload.paraSpacing !== undefined) { state.typography.paraSpacing = payload.paraSpacing; }
        if (payload.textIndent !== undefined) { state.typography.textIndent = payload.textIndent; }
        if (payload.textAlign) { state.typography.textAlign = payload.textAlign; }
        if (payload.columns !== undefined) { state.typography.columns = payload.columns; }

        applyContentCss();

        if (isPaged()) {
          onViewportResize();
          applyPercentInChapter(keptPercent);
        }

        updateTypographyUi();
/* 老的单个字段下发方式继续支持（宿主只改一项时用） */
        break;

      default:
        // 阶段 1 只实现上面几条，其余命令静默忽略（宿主不依赖回应）
        break;
    }
  });

  function onLoad(payload) {
    /* T7-3：先排工具栏、再做排版/分页——后者依赖工具栏占了多少高度。
       顺序反了会出现"页框比一页矮一行"，落点全部偏移（冒烟 Y14/Y15/Y23/Y10/Y42 实测踩到）。
       这里直接用 payload 判格式，避免依赖后面才赋值的 state.kind。 */
    if (!(payload && (payload.kind === 'pdf' || payload.kind === 'comic'))) {
      ensureEpubToolbarRows();
    }

    // T4-2 判据 4：重开会话时把上次用的主题接上（宿主从 Config.json 里读出来下发）
if (payload && payload.fonts && payload.fonts.length) {
      var fontSelect = document.getElementById('tyFontFamily');

      if (fontSelect) {
        fontSelect.innerHTML = '';
        payload.fonts.forEach(function (name) {
          var opt = document.createElement('option');
          opt.value = name;
          opt.textContent = name;
          fontSelect.appendChild(opt);
        });
      }
    }

    if (payload && payload.typography) {
      var ty = payload.typography;

      if (ty.fontSize) { state.typography.fontSize = ty.fontSize; }
      if (ty.lineHeight) { state.typography.lineHeight = ty.lineHeight; }
      if (ty.fontFamily) { state.typography.fontFamily = wrapFontFamily(ty.fontFamily); }
      ['Top', 'Bottom', 'Left', 'Right'].forEach(function (side) {
        var key = 'margin' + side;
        var v = Number(ty[key]);

        if (isFinite(v)) { state.typography[key] = marginPctOf({ }, side) === undefined ? 5 : Math.min(20, Math.max(0, v)); }
      });
      if (ty.paraSpacing !== undefined) { state.typography.paraSpacing = ty.paraSpacing; }
      if (ty.textIndent !== undefined) { state.typography.textIndent = ty.textIndent; }
      if (ty.columns !== undefined) { state.typography.columns = ty.columns; }
      if (ty.columnGap !== undefined) { state.typography.columnGap = columnGapPxOf({ columnGap: ty.columnGap }); }
      if (ty.columnRule !== undefined) { state.typography.columnRule = columnRuleOn({ columnRule: ty.columnRule }); }
      applyContentCss();
      updateTypographyUi();
    }

    if (payload && payload.customTheme) {
      setCustomColors(payload.customTheme.bg, payload.customTheme.fg, payload.customTheme.link, false);
    }

    if (payload && payload.theme) { applyTheme(payload.theme, false); }
    state.manifest = payload;
    state.pendingFragment = null;

    /* T4-5：TXT 才有编码选择器（判据 2：识别不准要能手动指定） */
    applyTextEncodingUi(payload);

    updateStatusBar();
    state.index = 0;
    state.pendingLocator = null;
    state.pendingFingerprint = null;
    state.pendingPage = null;

    /* 模式是宿主从 Config.json 读出来的（T4-1 判据：分页模式要保存到设置）。
       这里只接值，不落盘：落盘是宿主的职责。 */
    state.mode = payload.mode === 'paged' ? 'paged' : 'scroll';

    /* 上次读到哪了（宿主从库里读出来，随 load 一起送过来）。
       章节序号以定位里的为准，startIndex 只作兜底：书被换过时章数可能已经变了。 */
    var progress = payload.progress || null;

    if (progress && progress.locator) {
      state.pendingLocator = progress.locator;
    }

    if (progress && progress.fingerprint) {
      state.pendingFingerprint = progress.fingerprint;
    }

    var start = payload.startIndex || 0;

    if (state.pendingLocator && typeof state.pendingLocator.chapterIndex === 'number') {
      start = state.pendingLocator.chapterIndex;
    }

    if (state.manifest && state.manifest.spine && state.manifest.spine.length > 0) {
      start = Math.max(0, Math.min(state.manifest.spine.length - 1, start));
    }

    /* T4-4：目录随 load 一起下发（EPUB2 的 NCX / EPUB3 的 nav 都在宿主侧解析好）。
       降级目录（原书没有目录 / 目录坏了）照常显示，只在标题旁提示一句。 */
    state.tocFallback = !!(payload && payload.tocFallback);

    /* T6-2B：先归一化格式再画目录——PDF 的目录项认 entry.page，EPUB 认 entry.spineIndex，
       renderToc 得先知道自己在给谁画（顺序错了 PDF 目录会整条画不出来）。 */
    state.kind = payload && payload.kind === 'pdf'
      ? 'pdf'
      : (payload && payload.kind === 'comic'
        ? 'comic'
        : (payload && payload.kind === 'txt' ? 'txt' : 'epub'));

    /* T7-3：EPUB / TXT 的工具栏固定在 onLoad 开头排（那里直接用 payload 判格式）。 */

    /* T6-4：从全文搜索结果跳进来时，用户要的是"这一章"，不是"上次读到哪"。
       保存的定位会把 start 拉回去，所以清掉它；查找词挂到 pendingFind 上，
       章节渲染完 applyPendingFind 会把所有命中画出来（PDF 走下面的 runPdfFind）。 */
    if (payload && payload.findText) {
      state.pendingLocator = null;
      state.pendingFingerprint = null;
      state.pendingPage = null;
      state.findText = String(payload.findText);
      start = Math.max(0, Math.min(
        (state.manifest && state.manifest.spine && state.manifest.spine.length > 0)
          ? state.manifest.spine.length - 1
          : start,
        payload.startIndex || 0));
    }

    /* T6-5：书签与笔记随 load 一起下发（宿主已经按加入时间排好序）。 */
    state.bookmarks = payload && payload.annotations && payload.annotations.bookmarks
      ? payload.annotations.bookmarks
      : [];
    state.notes = payload && payload.annotations && payload.annotations.notes
      ? payload.annotations.notes
      : [];

    renderToc();
    closeTocPanel();

    if (isPdf()) {
      enterPdfMode(payload);

      if (payload.findText) { runPdfFind(payload.findText); }

      return;
    }

    if (payload && payload.findText) {
      state.pendingFind = {
        text: String(payload.findText),
        useRegex: false,
        matchCase: false
      };
    }

    openChapter(start, 0);
  }

  /* ==================== T6-2B：PDF 自绘引擎 ====================
     与 T6-2 的分工正好相反：内核不再碰 PDF，页面由宿主用 pdfium 渲染成 JPEG 送进来，
     排版 / 滚动 / 缩放 / 旋转 / 双页 / 文字层 / 搜索高亮全部是下面这些代码。

     与宿主的协议（字段名冻结，改一边就得改另一边）：
       → reader.pdfPage        { requestId, page, longEdge, rotation, theme }
       ← reader.pdfPageReady   { requestId, page, url, width, height, isImagePage, text, chars }
                                  url：虚拟主机地址或 data: URL，**两种都要能直接用**；
                                  chars：{c,x,y,w,h}，x/y/w/h 归一化到 0..1（相对该页，
                                         与 rotation 无关——宿主已按 rotation 渲染好）。
       ← reader.pdfPageFailed  { requestId, page, code, message }
       → reader.pdfPageChanged { page, totalPages }        （只有用户动作才上报）
       → reader.pdfFind        { query }
       ← reader.pdfFindResults { query, hits: [{ page, start, length }], total }
                                  start/length 是该页文本里的 UTF-16 字符下标。

     页缓存：宿主自己保留 20 页，我们这边也最多留 20 页的 DOM（LRU），
     超出就释放 img.src（重滚回来再请求一次，宿主那侧命中缓存所以很快）。 */

  var PDF_MIN_LONG_EDGE = 800;
  var PDF_MAX_LONG_EDGE = 3000;
  var PDF_PAGE_CACHE = 20;
  /* 连续滚动时提前请求多少页（来自宿主下发的 pdf.preloadPages，默认 10，范围 1–1000）。
     0 不再表示"不预载"：T7-4 起设置页的最低值就是 1。 */
  var PDF_PRELOAD_DEFAULT = 10;
  /* 一次预取最多请求多少页：值再大也分批，避免一次给宿主塞上千个渲染请求。 */
  var PDF_PREFETCH_BURST = 24;
  var PDF_CHAR_LIMIT = 5000;
  var PDF_DEFAULT_PAGE = { w: 595, h: 842 };   /* 还没量到第一页时按 A4 占位 */
  var PDF_PX_PER_PT = 96 / 72;                 /* PDF 100%：1 点 = 1/72 英寸，CSS 像素是 1/96 英寸 */
  var PDF_ZOOM_MIN = 0.25;
  var PDF_ZOOM_MAX = 4;
  var PDF_PAGE_GAP = 14;

  var pdfDom = { wrap: null, rows: [], rowMode: '' };

  /* 视口尺寸缓存：每页的显示尺寸都依赖视口宽高，但连续模式下有几百页，
     每页都读一次 wrap.clientWidth 会反复触发同步布局（200 页的书会明显变慢）。
     updatePdfLayout 开头刷新一次，其余时候用缓存值。 */
  var pdfViewport = { w: 0, h: 0 };

  function pdfRefreshViewport() {
    var wrap = pdfPagesEl();

    pdfViewport.w = wrap ? Math.max(1, wrap.clientWidth) : 1;
    pdfViewport.h = wrap ? Math.max(1, wrap.clientHeight) : 1;
  }
  var pdfScrollTimer = null;
  var pdfSuppressScroll = false;
  var pdfLastReportedPage = -1;
  /* 预载窗口的调度定时器（见 pdfSchedulePrefetch）。 */
  var pdfPrefetchTimer = null;

  /* '#RRGGBB' / '#RGB' / 'rgb(r,g,b)' → [r,g,b]；认不出来返回 null。
     PDF 的主题判断（pdfThemeName）用的就是它：按底色亮度决定请宿主渲染成 normal 还是 dark。 */
  function parseCssColor(value) {
    if (!value) { return null; }

    var text = String(value).trim();

    if (text.charAt(0) === '#') {
      var hex = text.slice(1);

      if (hex.length === 3) { hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2]; }
      if (hex.length !== 6) { return null; }

      var packed = parseInt(hex, 16);

      return isNaN(packed) ? null : [(packed >> 16) & 255, (packed >> 8) & 255, packed & 255];
    }

    var match = text.match(/rgba?\((\d+)[,\s]+(\d+)[,\s]+(\d+)/i);

    return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
  }

  /* 固定版式（PDF 与 T6-3 的漫画）共用同一套自绘引擎：页位图由宿主送进来，
     单页/双页/缩放/旋转/适应/页码全在下面这些函数里。isComic() 只用于"减法"
     （没有文字层、没有搜索、多一个日漫方向），引擎本身不需要分叉。 */
  function isPdf() {
    return state.kind === 'pdf' || state.kind === 'comic';
  }

  function isComic() {
    return state.kind === 'comic';
  }

  function pdfPagesEl() {
    return document.getElementById('pdfPages');
  }

  /* 这一页的"PDF 点尺寸"。PDF 是固定版式，全书共用一个基准（首图反推的 pageSize）；
     漫画每页比例不同：横图/小图必须按**自己**的宽高比排版，否则页框被统一比例撑住，
     图片 object-fit: contain 就会上下留白（用户实测反馈："横竖图混排出现白条"）。
     长边仍沿用 A4 长边，与 pdfSizeFromImage 的归一化保持一致。 */
  function pdfPointSize(page) {
    var p = state && state.pdf ? state.pdf.pageSize : null;
    var base = p && p.w > 0 && p.h > 0 ? p : PDF_DEFAULT_PAGE;

    if (isComic() && state.pdf) {
      var obj = typeof page === 'number' ? state.pdf.pages[page] : null;

      if (obj && obj.width > 0 && obj.height > 0) {
        var ratio = obj.width / obj.height;
        var longPt = Math.max(PDF_DEFAULT_PAGE.w, PDF_DEFAULT_PAGE.h);

        return ratio >= 1
          ? { w: longPt, h: longPt / ratio }
          : { w: longPt * ratio, h: longPt };
      }
    }

    return base;
  }

  /* 主题 → 渲染主题。**按底色亮度判断**（用户自定义的暗色主题也照顾得到）：
     暗底就请宿主反色渲染（它对文本页反色、图片页不反色），我们这边不再加任何 CSS 滤镜。 */
  function pdfThemeName() {
    var rgb = parseCssColor((state.theme && state.theme.background) || '#FFFFFF');
    var luma = rgb ? (rgb[0] * 299 + rgb[1] * 587 + rgb[2] * 114) / 1000 : 255;

    return luma < 128 ? 'dark' : 'normal';
  }

  /* 旋转 90/270 时宽高互换；返回的是"未缩放"的 CSS 像素尺寸。 */
  function pdfCSSSize(pt, rotation) {
    var w = pt.w / PDF_PX_PER_PT;
    var h = pt.h / PDF_PX_PER_PT;

    return (rotation === 90 || rotation === 270) ? { w: h, h: w } : { w: w, h: h };
  }

  /* 缩放系数：fitWidth = 视口宽 / 页宽；fitPage = min(视口宽/页宽, 视口高/页高)；
     custom = zoom × 96/72（PDF 的 100% 就是这么换算到 CSS 像素的）。
     双页模式下"每页可用的宽度"是视口的一半减掉页间缝。 */
  function pdfZoomFactor(page) {
    var p = state.pdf;

    if (!p) { return 1; }

    var size = pdfCSSSize(pdfPointSize(page), p.rotation) || { w: 1, h: 1 };
    var vw = pdfViewport.w || 1;
    var vh = pdfViewport.h || 1;

    if (p.spread) { vw = Math.max(1, (vw - PDF_PAGE_GAP) / 2); }

    var fitWidth = vw / Math.max(1, size.w);
    var fitPage = Math.min(vw / Math.max(1, size.w), vh / Math.max(1, size.h));

    if (p.zoomMode === 'fitWidth') { return Math.max(0.05, fitWidth); }
    if (p.zoomMode === 'fitPage') { return Math.max(0.05, Math.min(fitWidth, fitPage)); }

    return Math.max(PDF_ZOOM_MIN, Math.min(PDF_ZOOM_MAX, Number(p.zoom) || 1)) * PDF_PX_PER_PT;
  }

  function pdfDisplaySize(page) {
    var size = pdfCSSSize(pdfPointSize(page), state.pdf.rotation);
    var factor = pdfZoomFactor(page);

    return { w: Math.max(1, size.w * factor), h: Math.max(1, size.h * factor) };
  }

  /* 第一页图片尺寸 + 当前的旋转 → "PDF 点尺寸"（未旋转）。图片是宿主按 rotation 渲染过的，
     所以宽高比要先反旋回来；绝对尺度沿用 A4 的长边（我们不需要 PDF 的原始点尺寸，
     只需要一个跨页一致的基准，用来算缩放与请求长边）。 */
  function pdfSizeFromImage(width, height, rotation) {
    var w = Number(width);
    var h = Number(height);

    if (!(w > 0) || !(h > 0)) { return null; }

    if (rotation === 90 || rotation === 270) {
      var swap = w;
      w = h;
      h = swap;
    }

    var longEdgePt = Math.max(PDF_DEFAULT_PAGE.w, PDF_DEFAULT_PAGE.h);
    var scale = longEdgePt / Math.max(w, h);

    return { w: w * scale, h: h * scale };
  }

  /* 这一页该请宿主渲染多长的最长边。量化到 100 px：缩放/窗口变化带来的零头不会
     每次都触发一轮新请求（协议里的 requestId 复用以 (page,longEdge,rotation,theme) 为键）。 */
  function pdfLongEdgeFor(page) {
    var size = pdfDisplaySize(page);
    var scale = state.pdf.deviceScale || 1;
    var px = Math.max(size.w, size.h) * scale;
    var quantized = Math.round(px / 100) * 100;

    return Math.max(PDF_MIN_LONG_EDGE, Math.min(PDF_MAX_LONG_EDGE, quantized));
  }

  function pdfClampInt(value, fallback) {
    var n = Math.floor(Number(value));

    return isFinite(n) && n >= 0 ? n : fallback;
  }

  function pdfClampPage(page) {
    var p = state.pdf;
    var value = Math.floor(Number(page));

    if (!p || !isFinite(value)) { return p ? p.page : 0; }

    value = Math.max(0, value);

    if (p.totalPages > 0) { value = Math.min(value, p.totalPages - 1); }

    return value;
  }

  function pdfMeasureSample(p) {
    var chars = (p && p.chars) || [];
    var text = '';

    for (var i = 0; i < chars.length && text.length < 16; i++) {
      var c = chars[i] && chars[i].c ? String(chars[i].c) : '';

      if (c && c !== ' ' && c !== '\n' && c !== '\r' && c !== '\t') { text += c; }
    }

    return text || 'M';
  }

  /* 一页的 CSS 显示尺寸给死之后，用 canvas 量一次"这个字符集用 font-size:1px 有多宽"，
     再把每个 .pdfChar 的 scaleX 压进字符框（见 renderPdfTextLayer）。 */
  function measurePdfCharFactor(p) {
    if (p.charFactor) { return p.charFactor; }

    var sample = pdfMeasureSample(p);
    var factor = { sample: sample, unitWidth: 0, unitHeight: 0 };

    try {
      var canvas = document.createElement('canvas');
      var ctx = canvas.getContext('2d');

      if (ctx) {
        ctx.font = '1px sans-serif';
        factor.unitWidth = ctx.measureText(sample).width / sample.length;
        factor.unitHeight = 1;
      }
    } catch (ignored) {
      factor.unitWidth = 0.55;
      factor.unitHeight = 1;
    }

    if (!(factor.unitWidth > 0)) { factor.unitWidth = 0.55; }

    p.charFactor = factor;
    return factor;
  }

  /* ---------- 页对象与请求 ---------- */

  function pdfPageObj(page) {
    if (!state.pdf.pages[page]) {
      state.pdf.pages[page] = {
        requestId: null, url: null, width: 0, height: 0, isImagePage: false,
        chars: null, text: '', state: 'idle', key: '', message: '',
        element: null, textEl: null, imgEl: null, failEl: null, charEls: null, charFactor: null
      };
    }

    return state.pdf.pages[page];
  }

  function pdfKeyFor(page) {
    return page + ':' + pdfLongEdgeFor(page) + ':' + state.pdf.rotation + ':' + state.pdf.theme;
  }

  function pdfNeedPage(page) {
    var p = state.pdf;

    if (!p || page < 0 || (p.totalPages > 0 && page >= p.totalPages)) { return; }

    var obj = pdfPageObj(page);
    var key = pdfKeyFor(page);

    if (obj.state === 'pending' && obj.key === key) { return; }

    /* ready 且参数没变：不重发请求，但**要把图补到元素上**。
       回包可能比页元素先到（小图渲染几毫秒就回来，rAF 还没把这一行建出来），
       那时 onPdfPageReady 只能先把 url 记在 obj 上；元素建好之后，只有这里能把它接上。
       少了这一步，这一页会永远停在破图（T6-3 实测：3 页的纯色漫画第 2/3 页、重开 200 页
       那本的第 1 页，都是这条路径）。 */
    if (obj.state === 'ready' && obj.key === key && obj.url) {
      pdfAttachImage(page, obj);
      return;
    }

    if (obj.state === 'failed' && obj.key === key) { return; }

    p.requestSeq = (p.requestSeq || 0) + 1;
    obj.requestId = 'r' + p.requestSeq;
    obj.key = key;
    obj.state = 'pending';
    p.requestCount = (p.requestCount || 0) + 1;

    post('reader.pdfPage', {
      requestId: obj.requestId,
      page: page,
      longEdge: pdfLongEdgeFor(page),
      rotation: p.rotation,
      theme: p.theme
    });
  }

  /* 把已就绪的页位图接到这一页的 <img> 上。幂等：元素/图片还没建好时什么都不做，
     等下一次 updatePdfVisible → pdfNeedPage 再来一遍（见那里的 ready 分支）。 */
  function pdfAttachImage(page, obj) {
    var img = obj.imgEl;
    var el = obj.element;

    if (!img || !el || !obj.url) { return false; }

    if (!img.__etsPdfBound) {
      img.__etsPdfBound = true;

      img.addEventListener('load', function () {
        /* 图片一到位，这一页的真实高度才确定。只补高度差（见 pdfWithScrollCompensation），
           **不把滚动位置对回旧锚点**——否则用户正在拖滚动条时会被拉回拖动前的位置。 */
        pdfWithScrollCompensation(function () { updatePdfLayout(); });
        updatePdfVisible();
      });

      /* T6-2B：加载失败必须能自愈。宿主的页位图是内存 LRU，token 被挤掉时
         那一条会回 404；窗口正在关闭/请求被时序打断时也会失败。**不重试的话这一页
         就永远停在破图**——用户看到的现象就是"PDF 里的图片显示不出来"。
         限次（2 次），免得坏地址把宿主刷爆。 */
      img.addEventListener('error', function () {
        if ((obj.retries || 0) >= 2) { return; }

        obj.retries = (obj.retries || 0) + 1;
        obj.url = null;
        obj.state = 'idle';
        obj.key = '';
        pdfNeedPage(Number(img.__etsPage));
      });
    }

    img.__etsPage = page;

    if (img.getAttribute('src') !== obj.url) { img.src = obj.url; }

    return true;
  }

  function pdfEvictIfNeeded() {
    var p = state.pdf;
    var live = [];
    var page;

    for (page in p.pages) {
      if (p.pages.hasOwnProperty(page)) { live.push(Number(page)); }
    }

    /* 缓存上限跟着预载窗口走：预载 20 页就得留得住 20 页，否则刚预载完就被自己 evict。 */
    var limit = Math.max(PDF_PAGE_CACHE, Number(p.cacheLimit) || 0);

    if (live.length <= limit) { return; }

    var seen = {};
    var j;

    seen[p.page] = true;

    for (j = 1; j <= 3; j++) {
      seen[p.page - j] = true;
      seen[p.page + j] = true;
    }

    /* 由远及近地砍：先丢掉离当前页最远的页（LRU 感），只释放 img.src——
       宿主那侧还有 20 页缓存，滚回来再请求一次很快。 */
    live.sort(function (a, b) {
      return Math.abs(b - p.page) - Math.abs(a - p.page);
    });

    for (j = 0; j < live.length && live.length > limit; j++) {
      page = live[j];

      if (seen[page]) { continue; }

      var obj = p.pages[page];

      if (!obj) { continue; }

      if (obj.imgEl) { obj.imgEl.removeAttribute('src'); }

      if (obj.textEl && obj.textEl.parentNode) { obj.textEl.parentNode.removeChild(obj.textEl); }

      if (obj.failEl && obj.failEl.parentNode) { obj.failEl.parentNode.removeChild(obj.failEl); }

      /* 页元素必须从行里摘掉：只把引用置空的话，行里还挂着一个永远不更新的空壳，
         再把页对象删掉，那页就再也长不回来了。摘掉之后它下一轮按需重建。 */
      if (obj.element && obj.element.parentNode) { obj.element.parentNode.removeChild(obj.element); }

      delete p.pages[page];
      live.splice(j, 1);
      j--;
    }
  }

  /* ---------- 页面 DOM ---------- */

  /* 页面高度：量过的（有 .pdfPage 且量到 > 0）用实测值，没量到的按页尺寸估算。
     连续模式下未渲染的页靠这个估算"撑住"滚动高度，滚动条才不会乱跳。 */
  function pdfPageNaturalHeight(page) {
    var obj = state.pdf.pages[page];
    var measured = obj && obj.element ? obj.element.offsetHeight : 0;

    return measured > 0 ? measured : Math.round(pdfDisplaySize(page).h);
  }

  /* 取这一页的 DOM 元素，没有就建一个。
     页对象被 LRU 丢掉之后元素会一起摘下来（见 pdfEvictIfNeeded），
     "补建 + 放回行内位置"由 updatePdfLayout 统一做。 */
  function pdfEnsurePageElement(page) {
    var obj = pdfPageObj(page);

    if (!obj.element) {
      obj.element = document.createElement('div');
      obj.element.className = 'pdfPage';
    }

    return obj.element;
  }

  /* 第 page 页属于第几行。双页时第 1 页（封面）单独一行，之后 [1,2]、[3,4]…
     所以行号是 (page+1)/2 向下取整——**不能用 page/2**：第 1 页会被算到第 0 行（封面那行）。 */
  function pdfRowIndexOf(page) {
    var p = state.pdf;

    if (!p) { return 0; }
    if (!p.spread) { return page; }

    return page <= 0 ? 0 : Math.floor((page + 1) / 2);
  }

  /* 重建 DOM 结构（页数变了 / 排法变了才全量重建），并给每页算好宽高。
     只建元素与占位，不碰网络请求——请求时机由 updatePdfVisible 统一决定。 */
  function updatePdfLayout() {
    var p = state.pdf;
    var wrap = pdfPagesEl();

    if (!p || !wrap || !wrap.parentNode) { return; }

    pdfRefreshViewport();

    /* 每页的显示尺寸按**这一页自己**的比例算（漫画横竖图混排，见 pdfDisplaySize(page)）。 */
    /* rtl 也算"排法"的一部分：日漫双页的左右顺序写在行结构里（见下面的 rows.push），
       只切 rtl 而不重建行，DOM 顺序还是旧的 [1,2]（T6-3 验收实测：点了日漫模式图没换边）。 */
    /* spread 与 continuous 是**两个独立的维度**（用户实测要求：两枚按钮可以同时亮＝双页滚动）：
       spread 决定"一行几页"，continuous 决定"是一屏一页地翻还是纵向连续"。所以 :cont 也要
       进 mode 串——否则"双页滚动"与"双页非滚动"共用同一份行结构，show 变化后不会重建。 */
    var mode = (p.spread ? 'spread' : 'single')
      + (p.continuous ? ':cont' : '')
      + (p.rtl ? ':rtl' : '');

    if (!pdfDom.wrap || pdfDom.rowMode !== mode) {
      pdfDom.wrap = document.createElement('div');
      pdfDom.wrap.className = 'pdfWrap';
      pdfDom.rows = [];

      var rows = [];
      var i;

      if (p.totalPages > 0) {
        if (p.spread) {
          for (i = 0; i < p.totalPages; i += 1) {
            if (i === 0) {
              rows.push([0]);
            } else if (i + 1 < p.totalPages) {
              /* 日漫右到左：同一行里页码大的在左、小的在右（DOM 顺序即视觉顺序）。 */
              rows.push(p.rtl ? [i + 1, i] : [i, i + 1]);
              i += 1;
            } else {
              rows.push([i]);
            }
          }
        } else {
          for (i = 0; i < p.totalPages; i++) { rows.push([i]); }
        }
      }

      rows.forEach(function (rowPages) {
        var row = document.createElement('div');

        row.className = 'pdfRow';

        rowPages.forEach(function (page) {
          var el = pdfEnsurePageElement(page);

          if (el) {
            el.setAttribute('data-page', String(page));
            row.appendChild(el);
          }
        });

        pdfDom.wrap.appendChild(row);
        pdfDom.rows.push(row);
      });

    wrap.innerHTML = '';
    wrap.appendChild(pdfDom.wrap);
    pdfDom.rowMode = mode;
  }

    /* 连续滚动：全部页都显示；单页 / 双页（非连续）：只显示当前页所在的那一行。
       spread 只决定"一行几页"，不再强制打开滚动（用户实测：两枚按钮要能同时亮＝双页滚动）。 */
    var show = p.continuous ? null : p.page;
    var attached = {};

    if (pdfDom.wrap) {
      var rowsInDom = pdfDom.wrap.querySelectorAll('.pdfRow');
      var r;

      for (r = 0; r < rowsInDom.length; r++) {
        var kids = rowsInDom[r].children;
        var k;

        for (k = 0; k < kids.length; k++) {
          var attr = kids[k].getAttribute('data-page');

          if (attr !== null) { attached[attr] = true; }
        }
      }
    }

    /* 非连续时可见性按**行**判：单页一行一页，双页一行两页——只比页码的话，
       双页非滚动的那一行里会只剩当前页可见（另一页被 display:none 藏掉）。 */
    var currentRowIndex = pdfRowIndexOf(p.page);

    for (var page = 0; page < p.totalPages; page++) {
      var visible = show === null || pdfRowIndexOf(page) === currentRowIndex;

      /* 被 LRU 丢掉过的页元素要补回来，**并且放回原来的行**——
         双页模式下左右顺序错了，用户看到的就是反的。 */
      if (visible && !attached[String(page)]) {
        var slot = pdfDom.rows[pdfRowIndexOf(page)];

        if (slot) {
          var back = pdfEnsurePageElement(page);

          back.setAttribute('data-page', String(page));
          slot.appendChild(back);

          /* 插进去之后再按这一行的期望顺序重排：RTL 双页是降序（右边是较小的页码），
             只按"页码大的插前面"会把左右弄反。 */
          var ordered = Array.prototype.slice.call(slot.children).sort(function (a, b) {
            var pa = parseInt(a.getAttribute('data-page') || '-1', 10);
            var pb = parseInt(b.getAttribute('data-page') || '-1', 10);

            return state.pdf.rtl && state.pdf.spread ? (pb - pa) : (pa - pb);
          });

          ordered.forEach(function (node) { slot.appendChild(node); });
        }
      }

      var obj2 = p.pages[page] || null;
      var el2 = obj2 ? obj2.element : null;

      if (!el2) { continue; }

      el2.style.display = visible ? '' : 'none';

      if (!visible) { continue; }

      var box = pdfDisplaySize(page);

      el2.style.width = Math.round(box.w) + 'px';
      el2.style.height = Math.round(box.h) + 'px';

      if (!obj2.imgEl) {
        var img = document.createElement('img');

        img.className = 'pdfImage';
        img.alt = '第 ' + (page + 1) + ' 页';
        img.draggable = false;
        el2.appendChild(img);
        obj2.imgEl = img;
      }

      obj2.imgEl.style.width = Math.round(box.w) + 'px';
      obj2.imgEl.style.height = Math.round(box.h) + 'px';
    }

    /* 单页 / 双页（非连续）：**行本身也必须藏起来**。只把页元素设成 display:none 的话，
       那些空行还留在流里，每行都吃 `.pdfRow + .pdfRow { margin-top: 14px }`——320 页的书
       就是几千像素的空白滚动区，用户能一路往下滚到空白（用户实测反馈："单页模式还能向下滑动"）。
       同时把当前页那一行的 14px 上边距清零：适应页面算出来的页高正好等于视口高，
       再叠 14px 就又冒出一条滚动缝。连续模式（show === null）不需要做这件事——那时每一行
       都该显示，而且模式一变行会整批重建（见上面的 pdfDom.rowMode 判断），
       所以这里只在非连续模式下走一遍，别让翻页路径白白摸几百个元素。
       注意行号必须用 pdfRowIndexOf：双页模式下 show 是页码，不是行号。 */
    if (show !== null) {
      var curRow = pdfRowIndexOf(p.page);

      for (var rw = 0; rw < pdfDom.rows.length; rw++) {
        var rowEl = pdfDom.rows[rw];
        var rowVisible = rw === curRow;

        rowEl.style.display = rowVisible ? '' : 'none';
        rowEl.style.marginTop = '0';
      }
    }

    updatePdfPlaceholders();
    evictPdfFarPages();

    /* 这里**不再**自动恢复 scrollTop。原先它用 pdfCursor.top（上一次捕获的锚点）把
       滚动位置"对回去"，但那个锚点在用户拖滚动条时不会立刻更新——图片加载完触发
       本函数时，用户已经被拉回拖动前的位置（用户实测："拖进度条会跳回刚才的位置或随机位置"）。
       需要恢复的调用方自己用 pdfWithScrollCompensation（差量补偿）或 pdfRestoreAnchor
       （重排/缩放/改窗口这类用户主动换版式）。 */
  }

  function evictPdfFarPages() {
    /* LRU 只按"页对象数量"算，真正的丢弃在 pdfEvictIfNeeded 里；
       这里单独留一个函数是为了让 updatePdfLayout 的调用点读起来是一条线。 */
    pdfEvictIfNeeded();
  }

  /* ---------- 可见页与请求 ---------- */

  function visiblePdfPages() {
    var p = state.pdf;
    var wrap = pdfPagesEl();
    var out = [];

    if (!p || !wrap) { return out; }

    /* 非连续：只显示当前行——单页模式就是当前页，双页模式是当前页所在那一行的两页。 */
    if (!p.continuous) {
      if (!p.spread) { return [p.page]; }

      var row = pdfRowIndexOf(p.page);
      var first = row <= 0 ? 0 : (row * 2 - 1);
      var last = Math.min(p.totalPages - 1, first + 1);
      var rowPages = [];

      for (var k = first; k <= last; k++) { rowPages.push(k); }

      return rowPages;
    }

    var top = wrap.scrollTop;
    var bottom = top + Math.max(1, wrap.clientHeight);
    var lo = -1;
    var hi = -1;

    for (var page = 0; page < p.totalPages; page++) {
      var obj = p.pages[page];
      var el = obj && obj.element;

      if (!el) { continue; }

      var y = el.offsetTop;
      var h = el.offsetHeight || pdfPageNaturalHeight(page);

      if (y < bottom + 400 && y + h > top - 400) {
        if (lo < 0) { lo = page; }
        hi = page;
      }
    }

    if (lo < 0) { lo = p.page; hi = p.page; }

    /* 可见页 ±2：加载与翻页都留有余量，滚动时不会"看到白页才去请求"。 */
    for (var i = Math.max(0, lo - 2); i <= Math.min(p.totalPages - 1, hi + 2); i++) {
      out.push(i);
    }

    return out;
  }

  function updatePdfVisible() {
    var p = state.pdf;
    var wrap = pdfPagesEl();

    if (!p || !wrap) { return; }

    var wanted = visiblePdfPages();
    var inView = {};
    var i;

    for (i = 0; i < wanted.length; i++) {
      inView[wanted[i]] = true;
      pdfNeedPage(wanted[i]);
    }

    /* 已经被丢出缓存的页有机会重新要一次；顺手把长时间留着的页降级成 idle。 */
    for (var page in p.pages) {
      if (!p.pages.hasOwnProperty(page)) { continue; }

      var obj = p.pages[page];

      if (inView[page]) { continue; }

      if (obj.state === 'ready' && !obj.element) { obj.state = 'idle'; }
    }

    updatePdfPlaceholders();
  }

  /* "载入中"提示跟着"还有可见页在等宿主渲染"走：请求发出去时亮，最后一页有着落时灭。 */
  function refreshPdfLoading() {
    if (isPdf()) { setPdfLoading(pdfAnyPending()); }
  }

  function updatePdfPlaceholders() {
    var p = state.pdf;

    if (!p) { return; }

    for (var page in p.pages) {
      if (!p.pages.hasOwnProperty(page)) { continue; }

      var obj = p.pages[page];
      var el = obj.element;

      if (!el) { continue; }

      el.classList.toggle('pdfPagePending', obj.state === 'pending' || obj.state === 'idle');

      if (obj.state === 'failed') {
        if (!obj.failEl) {
          var fail = document.createElement('div');

          fail.className = 'pdfFail';
          fail.textContent = '这一页没渲染出来，点这里重试';
          fail.title = obj.message || ('第 ' + (Number(page) + 1) + ' 页渲染失败');

          fail.addEventListener('click', function () {
            /* 点击重试：清掉失败标记，改由 updatePdfVisible 重新请求这一页。 */
            var target = this.__etsPage;

            if (target !== undefined && state.pdf.pages[target]) {
              state.pdf.pages[target].state = 'idle';
              updatePdfVisible();
            }
          });

          fail.__etsPage = Number(page);
          el.appendChild(fail);
          obj.failEl = fail;
        }
      } else if (obj.failEl && obj.failEl.parentNode) {
        obj.failEl.parentNode.removeChild(obj.failEl);
        obj.failEl = null;
      }
    }

    refreshPdfLoading();
  }

  /* ---------- 排版：滚动位置 ↔ 当前页 ---------- */

  /* 顶部可见页：遍历 **DOM 里的页元素**，不看 p.pages。
     p.pages 只有 LRU 保留的十几页，滚远以后那些页对象都不在了，循环全部跳过 →
     found 停在旧页码上，状态栏的页码就再也不跟着滚（用户实测：滚到 40% 还显示第 2 页）。 */
  function pdfTopVisiblePage() {
    var p = state.pdf;
    var wrap = pdfPagesEl();

    if (!p || !wrap) { return p ? p.page : 0; }

    var els = wrap.querySelectorAll('.pdfPage');

    for (var i = 0; i < els.length; i++) {
      var el = els[i];

      /* 被藏掉的行（非连续的其它行）不参与计算。 */
      if (el.offsetParent === null) { continue; }

      if (el.offsetTop + el.offsetHeight > wrap.scrollTop + 1) {
        return Number(el.getAttribute('data-page'));
      }
    }

    return p.totalPages > 0 ? p.totalPages - 1 : p.page;
  }

  function pdfCaptureAnchor() {
    var p = state.pdf;
    var wrap = pdfPagesEl();

    if (!p || !wrap) { return null; }

    /* 非连续模式没有"滚动位置"可言：锚点就是当前页。 */
    if (!p.continuous) { return { page: p.page, offset: 0 }; }

    var els = wrap.querySelectorAll('.pdfPage');

    for (var i = 0; i < els.length; i++) {
      var el = els[i];

      if (el.offsetParent === null) { continue; }

      if (el.offsetTop + el.offsetHeight > wrap.scrollTop + 1) {
        return { page: Number(el.getAttribute('data-page')), offset: wrap.scrollTop - el.offsetTop };
      }
    }

    return { page: 0, offset: 0 };
  }

  function pdfRestoreAnchor(anchor) {
    var wrap = pdfPagesEl();

    if (!anchor || !wrap) { return; }

    var obj = state.pdf.pages[anchor.page];

    if (!obj || !obj.element) { return; }

    pdfSuppressScroll = true;
    wrap.scrollTop = Math.max(0, obj.element.offsetTop + (anchor.offset || 0));
    window.setTimeout(function () { pdfSuppressScroll = false; }, 0);
  }

  /* 某页在滚动容器里的顶部偏移（元素已被 LRU 摘掉时返回 -1）。 */
  function pdfPageTopInWrap(page) {
    var obj = state.pdf && state.pdf.pages[page];

    return obj && obj.element ? obj.element.offsetTop : -1;
  }

  /* 差量补偿：重排会让上方页的高度变化，scrollTop 不变就等于把用户"顶走"了。
     这里只补**锚点页新旧 offsetTop 的高度差**，绝不把 scrollTop 改回某个旧值——
     所以用户正在拖滚动条、或者正往下滚的时候调用它也是安全的（这正是用户实测报的
     "拖进度条 / 往下滑会跳回原位置"的根因：以前用过期锚点做绝对恢复）。 */
  function pdfWithScrollCompensation(mutate) {
    var wrap = pdfPagesEl();
    var anchor = pdfCaptureAnchor();
    var before = -1;

    if (wrap && anchor) { before = pdfPageTopInWrap(anchor.page); }

    mutate();

    if (!wrap || !anchor || before < 0) { return; }

    var after = pdfPageTopInWrap(anchor.page);

    if (after < 0 || after === before) { return; }

    pdfSuppressScroll = true;
    wrap.scrollTop = Math.max(0, wrap.scrollTop + (after - before));
    window.setTimeout(function () { pdfSuppressScroll = false; }, 0);
  }

  /* 滚动时算出顶部可见页：页码 / 进度 / 目录高亮都跟着它走。
     节流 100 ms；同一个页不重复上报（设计文档 §8.1：只有用户动作才记进度）。 */
  function onPdfScroll() {
    if (pdfScrollTimer) { return; }

    pdfScrollTimer = window.setTimeout(function () {
      pdfScrollTimer = null;

      if (!isPdf() || pdfSuppressScroll) { return; }

      var p = state.pdf;

      if (!p) { return; }

      if (p.continuous) {
        var found = pdfTopVisiblePage();

        if (found !== p.page) {
          p.page = found;
          updatePdfUi();
          markCurrentToc();
          postPdfPageChanged();
        }
      }

      updatePdfVisible();
      pdfSchedulePrefetch();
    }, 80);
  }

  function onPdfResize() {
    if (!isPdf() || !state.pdf) { return; }

    /* 窗口尺寸变了 → 缩放系数变 → 显示尺寸变 → 旧图作废。
       先记下锚点页，重排后把它的顶部对回原处，避免"一改窗口就跳到别的页"。 */
    var anchor = pdfCaptureAnchor();

    updatePdfLayout();
    pdfRestoreAnchor(anchor);
    updatePdfVisible();
    updatePdfUi();
    pdfUpdateToolbarRowsForWidth();

    if (pdfFindState.hits && pdfFindState.hits.length) { applyPdfFindHighlight(); }
  }

  /* ---------- 文字层 ---------- */

  function renderPdfTextLayer(page) {
    /* T6-3：漫画页没有文字层，连空覆盖层也不建——它只会挡在图片上面。 */
    if (isComic()) { return; }

    var p = state.pdf;
    var obj = p.pages[page];
    var el = obj && obj.element;

    if (!obj || !el) { return; }

    if (obj.textEl && obj.textEl.parentNode) { obj.textEl.parentNode.removeChild(obj.textEl); }

    obj.textEl = null;
    obj.charEls = null;

    var text = document.createElement('div');

    text.className = 'pdfText';

    /* 扫描件没有文字层：仍然放一个空的覆盖层，保持每页 DOM 结构一致。 */
    if (!obj.chars || !obj.chars.length || obj.chars.length > PDF_CHAR_LIMIT) {
      el.appendChild(text);
      obj.textEl = text;
      return;
    }

    var size = pdfDisplaySize(page);
    var factor = measurePdfCharFactor(obj);
    var frag = document.createDocumentFragment();
    var list = [];
    var index = 0;

    for (var i = 0; i < obj.chars.length; i++) {
      var box = obj.chars[i] || {};
      var raw = box.c === undefined || box.c === null ? '' : String(box.c);
      var ch = raw.length ? raw.charAt(0) : ' ';

      /* 宿主给的下标是"该页文本里的字符下标"（搜索命中用的就是这套下标），
         所以文字层必须**逐位对齐**：字符框缺位时补空格，不能压缩下标。 */
      while (index < i) {
        var filler = document.createElement('span');

        filler.className = 'pdfChar';
        filler.textContent = ' ';
        frag.appendChild(filler);
        index += 1;
      }

      var span = document.createElement('span');
      var x = Number(box.x) || 0;
      var y = Number(box.y) || 0;
      var w = Number(box.w) || 0;
      var h = Number(box.h) || 0;
      var hpx = Math.max(1, h * size.h);

      /* font-size 由字符框高度定，横向再用 scaleX 压进字符框宽：
         不同字体度量下都能保证"选中的范围"和眼睛看到的位置重合。 */
      var fontSize = h > 0 ? hpx : Math.max(1, hpx * 0.8);
      var target = w > 0 ? w * size.w : fontSize * 0.5;
      var natural = Math.max(0.01, factor.unitWidth * fontSize);
      var scaleX = target > 0 ? Math.max(0.05, Math.min(4, target / natural)) : 1;

      span.className = 'pdfChar';
      span.textContent = ch;
      span.style.left = (x * 100) + '%';
      span.style.top = (y * 100) + '%';
      span.style.width = (w * 100) + '%';
      span.style.height = (h * 100) + '%';
      span.style.fontSize = fontSize.toFixed(2) + 'px';
      span.style.transform = 'scaleX(' + scaleX.toFixed(4) + ')';
      span.setAttribute('data-i', String(i));
      frag.appendChild(span);
      list.push(span);
      index = i + 1;
    }

    /* 末尾补空格，让"字符数 = textContent.length"这件事在最后一屏也成立。 */
    while (index < (obj.text || '').length) {
      var tail = document.createElement('span');

      tail.className = 'pdfChar';
      tail.textContent = ' ';
      frag.appendChild(tail);
      index += 1;
    }

    text.appendChild(frag);
    el.appendChild(text);
    obj.textEl = text;
    obj.charEls = list;
  }

  /* ---------- 渲染协议：请求 / 回包 ---------- */

  function onPdfPageReady(payload) {
    var p = state.pdf;

    if (!p || !payload) { return; }

    var page = pdfClampInt(payload.page, -1);
    var obj = p.pages[page];

    /* 迟到的回包：这一页已经被丢掉或参数已经变了（缩放/旋转/主题切换），直接忽略。 */
    if (!obj || !payload.requestId || obj.requestId !== payload.requestId) { return; }

    /* 这一页的图片尺寸是不是第一次到手（漫画每页比例不同，要据此重排这一页的页框）。 */
    var hadNoSize = !(obj.width > 0 && obj.height > 0);

    /* 第一次拿到任何一页的图片尺寸，就用它把"PDF 点尺寸"定下来（不一定是第 1 页：
       宿主可以让我们直接打开第 50 页）。宽高比取这一页的，绝对尺度沿用 A4 的长边，
       后面所有页的缩放/长边计算都以此为准。

       **这一页的回包绝不能在这里丢掉**：它是唯一能让当前页立刻出图的响应。丢掉它、
       而 longEdge 量化（pdfLongEdgeFor）后 key 又恰好没变（页面比例接近 A4 时必然如此）
       的话，pdfNeedPage 会按"同 key 且 pending"去重、拒绝重发，这一页就永远停在
       pending、img.src 永远为空——现象正是"打开 PDF 第一页一直是破图"。
       所以这里只重排，然后继续往下走完设 url / 设 src 的正常流程。 */
    if (!p.pageSize && payload.width > 0 && payload.height > 0) {
      p.pageSize = pdfSizeFromImage(payload.width, payload.height, p.rotation);
      pdfWithScrollCompensation(function () { updatePdfLayout(); });
      renderPdfVisible();
    }

    obj.url = payload.url || '';
    obj.width = Number(payload.width) || 0;
    obj.height = Number(payload.height) || 0;
    obj.isImagePage = !!payload.isImagePage;
    obj.chars = payload.chars && payload.chars.length ? payload.chars : null;
    obj.text = payload.text === undefined || payload.text === null ? '' : String(payload.text);
    obj.state = obj.url ? 'ready' : 'failed';
    obj.key = pdfKeyFor(page);

    /* 漫画：每页按自己的宽高比排版，所以这一页的尺寸第一次到手时要重排一次，
       否则它一直沿用"打开时的估算比例"，横图/小图又会被挤出上下白条。 */
    if (isComic() && hadNoSize && obj.width > 0 && obj.height > 0) {
      pdfWithScrollCompensation(function () { updatePdfLayout(); });
      renderPdfVisible();
    }

    if (obj.state === 'failed') {
      obj.message = '宿主没有给出这一页的图片地址';
      updatePdfPlaceholders();
      return;
    }

    /* 元素/图片还没建好时只把 url 留在 obj 上：下次 updatePdfVisible → pdfNeedPage
       的 ready 分支会把它接上（小图渲染几毫秒就回包，来不及等 rAF 建元素）。 */
    if (!pdfAttachImage(page, obj)) { return; }

    renderPdfTextLayer(page);

    if (pdfFindState.hits && pdfFindState.hits.length) { applyPdfFindHighlight(); }

    /* T6-5：文字层重建后要重新画这一页的笔记高亮（字符框是新建的，旧标记全没了）。 */
    applyPdfNoteHighlights(page);

    updatePdfPlaceholders();
  }

  function onPdfPageFailed(payload) {
    var p = state.pdf;

    if (!p || !payload) { return; }

    var page = pdfClampInt(payload.page, -1);
    var obj = p.pages[page];

    if (!obj || !payload.requestId || obj.requestId !== payload.requestId) { return; }

    obj.state = 'failed';
    obj.key = pdfKeyFor(page);
    obj.message = payload.message ? String(payload.message) : ('错误码 ' + (payload.code || '?'));

    updatePdfPlaceholders();
  }

  /* ---------- 预载窗口 ---------- */

  /* 连续滚动时提前多少页（最小 1；非连续模式一屏就一行，不需要预取）。 */
  function pdfPreloadWindow() {
    var p = state.pdf;

    if (!p || !p.continuous) { return 0; }

    return Math.max(1, Math.min(1000, Number(p.preloadPages) || 0));
  }

  function pdfPrefetchAhead() {
    var p = state.pdf;
    var ahead = pdfPreloadWindow();

    if (!p || ahead <= 0) { return; }

    /* 值再大也分批：一次预取最多请求 PDF_PREFETCH_BURST 页，
       避免用户把预载设成 1000 时一口气给宿主塞上千个渲染请求。
       滚一屏预取一批，下一批随下一次滚动再来。 */
    var burst = Math.min(ahead, PDF_PREFETCH_BURST);

    for (var k = 1; k <= burst; k++) { pdfNeedPage(p.page + k); }

    pdfNeedPage(p.page - 1);
  }

  /* 一次滚动里只排一次预取（80 ms 的滚动节流已经排过 updatePdfVisible）。 */
  function pdfSchedulePrefetch() {
    if (pdfPrefetchTimer) { return; }

    pdfPrefetchTimer = window.setTimeout(function () {
      pdfPrefetchTimer = null;

      if (!isPdf() || !state.pdf) { return; }

      pdfPrefetchAhead();
    }, 300);
  }

  function renderPdfVisible() {
    var p = state.pdf;

    if (!p || !p.totalPages) { return; }

    var wanted = visiblePdfPages();
    var i;

    for (i = 0; i < wanted.length; i++) { pdfNeedPage(wanted[i]); }

    /* 预载窗口：默认 10 页，可在主程序「设置 → 阅读」组里改（1–1000 页，写 Config.json）。
       只发请求、不排版——宿主渲染完就进内存页表，往下滑时直接命中，不再"白屏等半天"。
       延后 300 ms 才发，让当前可见页先占住宿主的串行渲染队列（PDF 是 pdfium 现渲的）。 */
    pdfSchedulePrefetch();

    updatePdfPlaceholders();

    /* 渲染完文字层后把命中高亮补上（先到的页也得有黄底）。 */
    if (pdfFindState.hits && pdfFindState.hits.length) { applyPdfFindHighlight(); }
  }

  /* ---------- 跳页 / 缩放 / 旋转 / 布局 ---------- */

  function pdfGoto(page, reason) {
    var p = state.pdf;

    if (!p) { return -1; }

    var target = pdfClampPage(page);

    p.page = target;

    updatePdfLayout();

    var wrap = pdfPagesEl();
    var obj = p.pages[target];

    if (wrap) {
      pdfSuppressScroll = true;

      /* 非连续（单页 / 双页）：只显示当前行，滚动位置永远是顶；连续滚动才需要对齐目标页。 */
      if (!p.continuous) {
        wrap.scrollTop = 0;
      } else if (obj && obj.element) {
        /* 目标页顶部留 PDF_PAGE_GAP 的余量，翻页后不会贴着工具栏。 */
        wrap.scrollTop = Math.max(0, obj.element.offsetTop - PDF_PAGE_GAP);
      } else {
        wrap.scrollTop = 0;
      }

      window.setTimeout(function () { pdfSuppressScroll = false; }, 0);
    }

    updatePdfVisible();
    updatePdfUi();
    markCurrentToc();

    /* reason='user' 才记进度（restore / view 不算用户翻页，见设计文档 §8.1）。 */
    if (reason === 'user') { postPdfPageChanged(); }

    return target;
  }

  /* 只报"用户动作"引起的页码变化，并且去重——滚动时同一个页不重复上报。 */
  function postPdfPageChanged() {
    var p = state.pdf;

    if (!p) { return; }

    if (pdfLastReportedPage === p.page) { return; }

    pdfLastReportedPage = p.page;
    post('reader.pdfPageChanged', { page: p.page, totalPages: p.totalPages });
  }

  function pdfZoomStep(step) {
    var p = state.pdf;

    if (!p) { return; }

    var current = p.zoomMode === 'custom' ? p.zoom : (pdfZoomFactor(p.page) / PDF_PX_PER_PT);
    var next = current * (step > 0 ? 1.25 : 0.8);

    p.zoom = Math.max(PDF_ZOOM_MIN, Math.min(PDF_ZOOM_MAX, Math.round(next * 100) / 100));
    p.zoomMode = 'custom';

    pdfRelayout();
  }

  /* 换缩放模式 / 旋转 / 布局：旧图与新参数不匹配，所有页的当前图作废，重新请求可见页。 */
  function pdfRelayout() {
    var p = state.pdf;

    if (!p) { return; }

    var anchor = pdfCaptureAnchor();

    updatePdfLayout();
    pdfRestoreAnchor(anchor);
    updatePdfVisible();
    updatePdfUi();
    updatePdfToolbarState();
  }

  function pdfSetView(view) {
    var p = state.pdf;

    if (!p || !view) { return; }

    /* 兼容 epub 风格的 fit 值（'Fit' / 'FitH'），以及 layout/rotate 的合并写法。 */
    if (view.zoom !== undefined && view.zoom !== null) {
      p.zoomMode = 'custom';
      p.zoom = Math.max(PDF_ZOOM_MIN, Math.min(PDF_ZOOM_MAX, Number(view.zoom) || 1));
    }

    if (view.fit !== undefined && view.fit !== null) {
      p.zoomMode = String(view.fit).toLowerCase() === 'fit' ? 'fitPage' : 'fitWidth';
    }

    if (view.rotation !== undefined) {
      var r = Math.floor(Number(view.rotation)) % 360;

      p.rotation = r < 0 ? r + 360 : (r === 0 || r === 90 || r === 180 || r === 270 ? r : p.rotation);
    }

    if (view.continuous !== undefined) {
      p.continuous = !!view.continuous;
    }

    if (view.spread !== undefined) {
      p.spread = !!view.spread;
    }

    if (view.rtl !== undefined) {
      p.rtl = !!view.rtl;
    }

    pdfRelayout();
  }

  function pdfRotate() {
    var p = state.pdf;

    if (!p) { return; }

    p.rotation = (p.rotation + 90) % 360;
    pdfRelayout();
  }

  /* 两枚按钮管的是**两个独立维度**（用户实测要求）：
       「单页/滚动」= continuous（纵向连续 or 一屏一屏地翻）
       「单页/双页」= spread（一行一页 or 一行两页）
     两者互不强制清零，于是有四种组合：单页 / 单页滚动 / 双页 / 双页滚动。
     以前这里会互相清掉对方，导致"双页滚动"进不去。 */
  function pdfToggleContinuous() {
    var p = state.pdf;

    if (!p) { return; }

    p.continuous = !p.continuous;

    pdfRelayout();
  }

  function pdfToggleSpread() {
    var p = state.pdf;

    if (!p) { return; }

    p.spread = !p.spread;

    pdfRelayout();
  }

  /* 翻页步长：单页 ±1；双页按"行"跳（封面第 1 页单独一行，之后一行两页）。 */
  function pdfStepPage(delta) {
    var p = state.pdf;

    if (!p) { return -1; }

    if (!p.spread) { return pdfGoto(p.page + delta, 'user'); }

    var row = pdfRowIndexOf(p.page);
    var targetRow = row + delta;
    var rows = Math.ceil(Math.max(0, p.totalPages - 1) / 2) + 1;

    if (targetRow < 0 || targetRow >= rows) { return p.page; }

    return pdfGoto(targetRow === 0 ? 0 : (targetRow * 2 - 1), 'user');
  }

  /* T6-3 日漫方向：只翻页方向与双页左右顺序，不重载任何页（页位图与方向无关）。 */
  function pdfToggleRtl() {
    var p = state.pdf;

    if (!p) { return; }

    p.rtl = !p.rtl;
    pdfRelayout();
  }

  /* ---------- 工具栏与状态条 ---------- */

  /* PDF / 漫画的工具栏固定成两行（用户实测要求，PDF 与漫画共用这一套）：
       第一行：上一页 …（目录 / 书签 / 笔记）… 页码 / 缩放 … 自定义主题 [三个色框] … 下一页
               ← 上一页永远最左、下一页永远最右，自定义主题的三个色框跟在「自定义」右边
       第二行：页控件 / 缩放 / 模式 / 全屏 / 外部打开（自定义主题的色框不在这里，见下）
     原先靠 flex 自然折行：窄窗口时"下一页"被挤到第二行右端、主题跑到第一行右端，
     用户看到的正是"上一页在最左、下一页却在下一行"。**只改 DOM 位置、id 一个不改**，
     所以 EPUB 的隐藏规则和所有按 id 查询的冒烟断言都不受影响（EPUB 从不调这里）。 */
  function ensurePdfToolbarRows() {
    var bar = document.getElementById('toolbar');

    if (!bar || document.getElementById('pdfToolRowTop')) { return; }

    var top = document.createElement('div');
    var bottom = document.createElement('div');

    top.id = 'pdfToolRowTop';
    bottom.id = 'pdfToolRowBottom';
    top.className = 'pdfToolRow';
    bottom.className = 'pdfToolRow';

    function park(id, into) {
      var el = document.getElementById(id);

      if (el) { into.appendChild(el); }
    }

    /* 第一行：上一页 …（页码 / 总页数 / 缩小 / 放大）… 下一页
       用户要求：页码框与加减按钮放在**原先「正在打开」那个文字的位置**，不要放书名。 */
    park('pdfPrev', top);
    park('btnToc', top);
    park('btnBookmark', top);
    park('btnAnnotations', top);
    park('pdfPageInput', top);
    park('pdfPageTotal', top);
    park('pdfZoomOut', top);
    park('pdfZoomIn', top);
    /* 主题下拉：放在加号右边、固定在第一行（用户要求），不再挂到第二行最右。 */
    park('themeSelect', top);
    /* 自定义主题的三个取色器（用户 2026-10-21 截图反馈：位置不对）：
       必须紧跟在「自定义」下拉框右边、并留在第一行。原先它没有 park，
       作为 #toolbar 的直接子元素：窄窗口时独占一行显示在 PDF 两行**之上**，
       宽窗口（行容器 display:contents）时又被 flex order 排到「上一页」**左边**。
       放在 themeSelect 之后、pdfNext 之前：窄窗两行 = 上一页 … 自定义 [色框] … 下一页；
       宽窗一行 = 色框在自定义右边，PDF 按钮组（order:1）之后才是下一页（order:3）。 */
    park('customColors', top);
    park('pdfNext', top);

    /* 第二行：适应页面 / 适应宽度 / 旋转 / 模式组 / 搜索 / 预载 / 全屏 / 外部打开 */
    park('pdfBar', bottom);

    bar.appendChild(top);
    bar.appendChild(bottom);

    pdfUpdateToolbarRowsForWidth();
  }

  /* 一行 / 两行：按**实际测量**决定，不写死窗口宽度。
     先切到"一行"（display:contents），量工具栏高度——如果还是一行那么高说明真放得下；
     一旦折行（高度翻倍）立刻退回窄窗口的两行结构，保证按钮永远不出画面。 */
  function pdfUpdateToolbarRowsForWidth() {
    var bar = document.getElementById('toolbar');
    var prev = document.getElementById('pdfPrev');

    if (!bar || !prev || !isPdf()) { return false; }

    document.body.classList.add('ets-pdf-wide');

    var oneRowHeight = Math.max(1, prev.getBoundingClientRect().height);
    var totalHeight = bar.getBoundingClientRect().height;

    /* 一行的工具栏高度 = 按钮高 + 上下 padding（约 16px）+ 边框。折行后会多出整整一行
       按钮高。用"按钮高 + 26px"做分界，比按倍数估稳（实测 28px 按钮 → 一行 50px、两行 75px）。 */
    var wide = totalHeight <= oneRowHeight + 26;

    pdfDom.oneRowHeight = Math.round(oneRowHeight);
    pdfDom.totalHeight = Math.round(totalHeight);

    document.body.classList.toggle('ets-pdf-wide', wide);

    return wide;
  }

  /* EPUB / TXT 的工具栏：**首行两端钉死**（用户 2026-10-19 要求）——
       上一页永远在第一行最左、下一页永远在第一行最右（滚动模式下这两枚显示"上一章/下一章"），
       无论窗口多窄、也无论当前是一行还是两行，这两枚都不许掉到第二行。
       两行时：第一行 = 上一页 …（目录/书签/笔记/章节标题）… 下一页；第二行 = 其余控件。
       一行时：全部控件排成一行，上一页/下一页仍是最左/最右（宽窄由 updateEpubToolbarRowsForWidth 实测决定）。
     只挪 DOM 位置、所有 id 不变（冒烟与样式都按 id 查）。PDF / 漫画走上面那套 pdfToolRow。 */
  function ensureEpubToolbarRows() {
    var bar = document.getElementById('toolbar');

    if (!bar || isPdf() || document.getElementById('epubToolRowTop')) { return; }

    var top = document.createElement('div');
    var bottom = document.createElement('div');

    top.id = 'epubToolRowTop';
    bottom.id = 'epubToolRowBottom';
    top.className = 'epubToolRow';
    bottom.className = 'epubToolRow';

    function park(id, into) {
      var el = document.getElementById(id);

      if (el) { into.appendChild(el); }
    }

    /* 第一行：上一页 —— 目录 / 书签 / 笔记 —— 章节标题 —— 下一页 */
    park('btnPrev', top);
    park('btnToc', top);
    park('btnBookmark', top);
    park('btnAnnotations', top);
    park('heading', top);
    park('btnNext', top);

    /* 第二行：主题 / 编码 / 排版面板 / Aa / 分页 / 编辑本章 / 查找替换 */
    park('themeSelect', bottom);
    park('encodingSelect', bottom);
    park('encodingHint', bottom);
    park('typePanel', bottom);
    park('customColors', bottom);
    park('btnType', bottom);
    park('btnMode', bottom);
    park('btnEditChapter', bottom);
    park('btnFindReplace', bottom);

    bar.appendChild(top);
    bar.appendChild(bottom);
    document.body.classList.add('ets-epub-rows');

    /* 先按当前窗口宽度决定一行还是两行（宽到放得下就并回一行）。 */
    updateEpubToolbarRowsForWidth();

    /* 兜底：工具栏高度真的变了（一行/两行切换、系统字号变化），分页页框按新高度重算。
       延迟一点再算——此刻 iframe 还没按新工具栏高度重排，立刻算会拿到旧高度（页框比一页矮/高一行）。 */
    watchToolbarHeight();
  }

  /* 一行 / 两行的分界**阈值**（用户 2026-10-19 要求"精确算出阈值"）：按实测宽度算，不写死窗口宽度。
     阈值 = 左右内边距 + 所有参与排版的控件自然宽度之和 + 行间距 × (控件数 - 1) + 章节标题最小宽度。
     · #typePanel 是绝对定位浮层、[hidden] 控件不占位 → 一律剔除；
     · 章节标题是唯一可收缩的控件（flex:1 + 省略号），按"最小可见宽度"计入，
       所以 available >= 阈值 恰好就是"一行放得下全部控件、且标题还看得清"的那条线。 */
  var EPUB_HEADING_MIN_WIDTH = 140;

  function epubOneRowThreshold() {
    var bar = document.getElementById('toolbar');
    var top = document.getElementById('epubToolRowTop');
    var bottom = document.getElementById('epubToolRowBottom');

    if (!bar || !top || !bottom) { return 0; }

    var style = getComputedStyle(bar);
    var gap = parseFloat(style.columnGap);

    if (!(gap > 0)) { gap = 12; }

    var total = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
    var count = 0;

    [top, bottom].forEach(function (row) {
      Array.prototype.forEach.call(row.children, function (el) {
        var cs = getComputedStyle(el);

        if (cs.display === 'none' || cs.position === 'absolute' || cs.position === 'fixed') { return; }

        total += el.id === 'heading' ? EPUB_HEADING_MIN_WIDTH : el.getBoundingClientRect().width;
        count++;
      });
    });

    return count ? Math.ceil(total + gap * (count - 1)) : 0;
  }

  /* 宽到能一行放下就并回一行（用户要求"随窗口大小切换一行/两行"）；
     否则固定两行。只切一个 class，不重建 DOM、不动 id。 */
  var epubToolbarWide = null;

  function updateEpubToolbarRowsForWidth() {
    var bar = document.getElementById('toolbar');

    if (isPdf() || !bar || !document.getElementById('epubToolRowTop')) { return false; }

    var threshold = epubOneRowThreshold();

    if (!threshold) { return false; }

    var wide = Math.round(bar.getBoundingClientRect().width) >= threshold;

    if (wide !== epubToolbarWide) {
      epubToolbarWide = wide;
      document.body.classList.toggle('ets-epub-wide', wide);
    }

    return wide;
  }

  /* 工具栏从一行变两行 / 反之，可用高度就变了：分页页框必须按新高度重算。
     必须**延后**再算：ResizeObserver 回调时 iframe 往往还没按新高度重排，
     立刻 applyPagedCss 会把页框算成"比一页矮一行"（实测页框 523 vs 一页 551）。 */
  function watchToolbarHeight() {
    var bar = document.getElementById('toolbar');

    if (!bar || typeof ResizeObserver !== 'function' || bar.__etsHeightWatched) { return; }

    bar.__etsHeightWatched = true;

    var last = 0;
    var timer = null;

    new ResizeObserver(function () {
      var height = Math.round(bar.getBoundingClientRect().height);

      if (height === last || height <= 0) { return; }

      last = height;

      if (timer) { clearTimeout(timer); }

      timer = setTimeout(function () {
        timer = null;

        if (isPaged()) { applyPagedCss(); }
      }, 120);
    }).observe(bar);
  }

  function updatePdfToolbarState() {
    var p = state.pdf;

    if (!p) { return; }

    var layout = document.getElementById('pdfLayout');
    var spread = document.getElementById('pdfSpread');
    var fitPage = document.getElementById('pdfFitPage');
    var fitWidth = document.getElementById('pdfFitWidth');
    var input = document.getElementById('pdfPageInput');
    var total = document.getElementById('pdfPageTotal');

    if (layout) {
      layout.classList.toggle('active', !!p.continuous);
      /* 用户实测要求：文案固定写"这一枚管哪两个模式"，当前状态用颜色表示
         （白＝单页、绿＝滚动），不再随状态改写文案（原先写"目标模式"，
         两枚按钮的文字会互相滚动，反而看不出当前是哪个模式）。 */
      layout.textContent = '单页/滚动';
      layout.title = p.continuous
        ? '滚动模式（点击回到单页模式）'
        : '单页模式（点击切到滚动模式，Ctrl+M）';
    }

    if (spread) {
      spread.classList.toggle('active', !!p.spread);
      spread.textContent = '单页/双页';
      spread.title = p.spread
        ? '双页模式（点击回到单页模式）'
        : '单页模式（点击切到双页模式，并排两页）';
    }

    var rtl = document.getElementById('pdfRtl');

    if (rtl) {
      rtl.classList.toggle('active', !!p.rtl);
      /* 与其它模式按钮同一套"目标模式"语义：按钮写的是点下去会变成什么。 */
      rtl.textContent = p.rtl ? '从左到右' : '日漫模式';
      rtl.title = p.rtl
        ? '恢复从左到右（普通漫画）'
        : '日漫模式：从右到左（翻页方向与双页顺序都反过来）';
    }

    if (fitPage) { fitPage.classList.toggle('active', p.zoomMode === 'fitPage'); }
    if (fitWidth) { fitWidth.classList.toggle('active', p.zoomMode === 'fitWidth'); }

    if (input && document.activeElement !== input) { input.value = String(p.page + 1); }
    if (total) { total.textContent = p.totalPages > 0 ? ('/ ' + p.totalPages) : '/ --'; }
  }

  function updatePdfUi() {
    var p = state.pdf;

    if (!p) { return; }

    var percent = document.getElementById('statusPercent');
    var status = document.getElementById('statusChapter');

    if (status) {
      status.textContent = p.totalPages > 0
        ? ('第 ' + (p.page + 1) + ' / ' + p.totalPages + ' 页')
        : ('第 ' + (p.page + 1) + ' 页');
    }

    if (percent) {
      percent.textContent = p.totalPages > 0
        ? ('全书 ' + Math.round(p.page / p.totalPages * 100) + '%')
        : '全书 --';
    }

    updatePdfToolbarState();
  }

  function setPdfLoading(on) {
    var hint = document.getElementById('statusHint');

    if (hint) { hint.textContent = on ? '正在载入页面…' : ''; }
  }

  /* ---------- 提示 / 主题 / 打开方式 ---------- */

  function pdfNotice(text) {
    var wrap = pdfPagesEl();
    var box = document.getElementById('pdfEmpty');

    if (!wrap) {
      notice(text);
      return;
    }

    if (!box) {
      box = document.createElement('div');
      box.id = 'pdfEmpty';
      /* 不复用 #notice：它是状态条上的降级条，样式与位置都不适合当空白页提示。 */
      box.style.position = 'absolute';
      box.style.left = '50%';
      box.style.top = '42%';
      box.style.transform = 'translate(-50%, -50%)';
      box.style.padding = '10px 16px';
      box.style.borderRadius = '6px';
      box.style.background = 'rgba(0, 0, 0, 0.62)';
      box.style.color = '#FFFFFF';
      box.style.zIndex = '20';
      wrap.appendChild(box);
    }

    box.textContent = text;
  }

  function applyPdfTheme() {
    var p = state.pdf;
    var root = document.documentElement;
    var bg = (state.theme && state.theme.background) || (p ? p.themeBg : null);

    if (root && bg) { root.style.setProperty('--ets-bg', bg); }

    if (!p) { return; }

    p.themeBg = bg || p.themeBg;

    var wanted = pdfThemeName();

    if (wanted !== p.theme) {
      /* 主题开关翻转：渲染主题变了，所有页的当前图作废，重新请求可见页。 */
      p.theme = wanted;
      pdfRelayout();
    }
  }

  function pdfOpenExternal() {
    post('reader.openExternal', {});
  }

  /* ---------- 目录（PDF outline 进同一个抽屉） ---------- */

  /* 点 PDF 目录项：flash 是"这条跳不动"的反馈（outline 里 page=-1 是分组标题，不会走到这里）。 */
  function pdfJumpFromToc(page) {
    if (page < 0) {
      notice('这一条没有对应的页码，跳不过去。');
      return false;
    }

    closeTocPanel();
    pdfGoto(page, 'user');

    return true;
  }

  function markPdfToc() {
    var host = document.getElementById('tocList');

    if (!host || !state.pdf) { return; }

    var items = host.querySelectorAll('.tocItem');

    if (!items.length) { return; }

    var best = -1;
    var bestPage = -1;

    for (var i = 0; i < items.length; i++) {
      var page = parseInt(items[i].getAttribute('data-page') || '-1', 10);

      if (page < 0) { continue; }

      if (page <= state.pdf.page && page > bestPage) {
        bestPage = page;
        best = i;
      }
    }

    /* 当前页在第一条目录项之前：标记第一条。 */
    if (best < 0) {
      for (var j = 0; j < items.length; j++) {
        if (parseInt(items[j].getAttribute('data-page') || '-1', 10) >= 0) {
          best = j;
          break;
        }
      }
    }

    for (var k = 0; k < items.length; k++) {
      items[k].classList.toggle('tocCurrent', k === best);
    }
  }

  /* ---------- 搜索（复用 EPUB 的查找栏，命中在文字层上画黄底） ---------- */

  var pdfFindState = { query: '', hits: [], chars: [], index: -1, current: null };

  function pdfResetFind() {
    pdfFindState.query = '';
    pdfFindState.hits = [];
    pdfFindState.chars = [];
    pdfFindState.index = -1;
    pdfFindState.current = null;
  }

  function runPdfFind(text) {
    var query = String(text === undefined || text === null ? '' : text);

    pdfFindState.query = query;

    if (!query) {
      pdfResetFind();
      updatePdfFindCount();
      clearPdfHitClasses();
      return 0;
    }

    /* 检索本身在宿主侧做（同一份 pdfium 文本层），我们只负责"跳到命中 + 画黄底"。 */
    post('reader.pdfFind', { query: query });
    return 0;
  }

  function onPdfFindResults(payload) {
    if (!isPdf() || !payload) { return; }

    var query = payload.query === undefined || payload.query === null
      ? String(state.findText || '')
      : String(payload.query);

    pdfFindState.query = query;
    state.findText = query;

    var raw = payload.hits && payload.hits.length ? payload.hits : [];
    var hits = [];
    var i;

    for (i = 0; i < raw.length; i++) {
      var hit = raw[i] || {};
      var page = pdfClampInt(hit.page, -1);

      if (page < 0) { continue; }

      hits.push({
        page: page,
        start: pdfClampInt(hit.start, 0),
        length: Math.max(1, pdfClampInt(hit.length, 1))
      });
    }

    pdfFindState.hits = hits;
    pdfFindState.index = hits.length ? 0 : -1;

    if (payload.total !== undefined) { state.bookTotal = pdfClampInt(payload.total, hits.length); }

    state.bookCountText = query;

    /* 第 1 个命中可能在别的页：跳过去再把黄底画上。 */
    if (hits.length) { pdfFocusHit(0); }
    else { clearPdfHitClasses(); }

    updatePdfFindCount();
  }

  function clearPdfHitClasses() {
    var nodes = document.querySelectorAll('#pdfPages .pdfChar.hit, #pdfPages .pdfChar.hitCurrent');
    var i;

    for (i = 0; i < nodes.length; i++) {
      nodes[i].classList.remove('hit');
      nodes[i].classList.remove('hitCurrent');
    }
  }

  /* 把命中范围映射到文字层的字符盒上。start/length 是**页文本里的字符下标**，
     与我们建 .pdfChar 时用的下标一一对应（缺位补空格保证了这一点）。 */
  function applyPdfFindHighlight() {
    var hits = pdfFindState.hits;
    var current = pdfFindState.index;
    var i;
    var j;

    clearPdfHitClasses();

    if (!hits || !hits.length) { return; }

    for (i = 0; i < hits.length; i++) {
      var hit = hits[i];
      var obj = state.pdf.pages[hit.page];

      if (!obj || !obj.charEls || !obj.textEl) { continue; }

      var end = Math.min(hit.start + hit.length, obj.text ? obj.text.length : obj.charEls.length);

      for (j = hit.start; j < end; j++) {
        var span = obj.charEls[j];

        if (!span) { continue; }

        span.classList.add('hit');

        if (i === current) { span.classList.add('hitCurrent'); }
      }
    }

    /* 当前命中翻进了新的一页：把那一页滚到眼前（文字层是绝对定位的，
       只要页在视口里高亮就看得见）。 */
    var focus = hits[current];

    if (focus && state.pdf.page !== focus.page) { pdfGoto(focus.page, 'user'); }
  }

  function pdfFocusHit(index) {
    var hits = pdfFindState.hits;

    if (!hits || !hits.length) {
      pdfFindState.index = -1;
      updatePdfFindCount();
      return -1;
    }

    pdfFindState.index = ((index % hits.length) + hits.length) % hits.length;

    var hit = hits[pdfFindState.index];

    if (hit.page !== state.pdf.page) { pdfGoto(hit.page, 'user'); }

    applyPdfFindHighlight();

    /* 文字层已经建好时把命中处滚到视口中央（比"页顶对齐"更准）。 */
    var obj = state.pdf.pages[hit.page];
    var wrap = pdfPagesEl();
    var span = obj && obj.charEls ? obj.charEls[hit.start] : null;

    if (wrap && span && span.getBoundingClientRect) {
      var rect = span.getBoundingClientRect();
      var wrapRect = wrap.getBoundingClientRect();

      wrap.scrollTop = Math.max(0, wrap.scrollTop + (rect.top - wrapRect.top) - wrap.clientHeight * 0.38);
    }

    updatePdfFindCount();

    return pdfFindState.index;
  }

  function pdfFindStep(forward) {
    var hits = pdfFindState.hits;

    if (!hits || !hits.length) {
      updatePdfFindCount();
      return -1;
    }

    return pdfFocusHit(pdfFindState.index + (forward ? 1 : -1));
  }

  function updatePdfFindCount() {
    var label = document.getElementById('findCount');

    if (!label) { return; }

    var hits = pdfFindState.hits;
    var query = String(state.findText || pdfFindState.query || '');

    if (!query) {
      label.textContent = '';
      return;
    }

    if (!hits || !hits.length) {
      label.textContent = '全书无匹配';
      return;
    }

    label.textContent = '第 ' + (pdfFindState.index < 0 ? 0 : pdfFindState.index + 1) + ' / ' + hits.length + ' 个';
  }

  /* 关闭查找栏：把黄底清掉、状态复位（并把"已查询"这个标记清干净，
     免得下次 Ctrl+F 又去问宿主一遍）。 */
  function closePdfFind() {
    pdfResetFind();
    clearPdfHitClasses();
    updatePdfFindCount();
    post('reader.pdfFind', { query: '' });
  }

  /* ---------- 键盘 ---------- */

  /* PDF 模式下的键盘分流：返回 true 表示"这个键我们吃了"。
     Ctrl+F / Ctrl+M / +/-/0、PageUp/Down/←/→/空格/Home/End 归我们，
     其余（含 Esc / F11）交回 onKeyDown 那张 EPUB 同款表。 */
  function onPdfKeyDown(event) {
    if (!isPdf() || !state.pdf) { return false; }

    var key = event.key || '';

    if (event.ctrlKey && !event.altKey && !event.metaKey) {
      if (key === 'f' || key === 'F') {
        event.preventDefault();
        openFindBar();
        return true;
      }

      if (key === 'm' || key === 'M') {
        event.preventDefault();
        pdfToggleContinuous();
        return true;
      }

      if (key === '=' || key === '+') {
        event.preventDefault();
        pdfZoomStep(1);
        return true;
      }

      if (key === '-' || key === '_') {
        event.preventDefault();
        pdfZoomStep(-1);
        return true;
      }

      if (key === '0') {
        event.preventDefault();
        state.pdf.zoomMode = 'custom';
        state.pdf.zoom = 1;
        pdfRelayout();
        return true;
      }

      return false;
    }

    if (event.altKey || event.metaKey) { return false; }
    if (isTypingTarget(event.target)) { return false; }

    /* 日漫右到左：左右方向键反过来。PageUp/PageDown 不反（它本来就是"上/下"）。 */
    var backKey = state.pdf.rtl ? 'ArrowRight' : 'ArrowLeft';
    var forwardKey = state.pdf.rtl ? 'ArrowLeft' : 'ArrowRight';

    if (key === 'PageUp' || key === backKey) {
      event.preventDefault();
      pdfStepPage(-1);
      return true;
    }

    if (key === 'PageDown' || key === forwardKey) {
      event.preventDefault();
      pdfStepPage(1);
      return true;
    }

    if (key === ' ' || key === 'Spacebar') {
      event.preventDefault();
      pdfStepPage(event.shiftKey ? -1 : 1);
      return true;
    }

    if (key === 'Home') {
      event.preventDefault();
      pdfGoto(0, 'user');
      return true;
    }

    if (key === 'End') {
      event.preventDefault();
      pdfGoto(state.pdf.totalPages > 0 ? state.pdf.totalPages - 1 : 0, 'user');
      return true;
    }

    return false;
  }

  /* ---------- 进入 PDF 模式与工具栏装配 ---------- */

  function enterPdfMode(payload) {
    var pdf = (payload && payload.pdf) || {};
    var total = pdfClampInt(pdf.totalPages, 0);
    var start = pdfClampInt(pdf.startPage, 0);
    var comic = !!(payload && payload.kind === 'comic');

    if (total > 0) { start = Math.min(start, total - 1); }

    state.kind = comic ? 'comic' : 'pdf';

    /* 漫画默认单页（计划 T6-3 的三种模式：单页 / 双页 / 日漫）；PDF 默认连续滚动。
       body 上的 .ets-comic 只用于 CSS 减法（藏"滚动模式"与"搜索"，露出"日漫模式"）。 */
    document.body.classList.toggle('ets-comic', comic);

    state.pdf = {
      totalPages: total,
      page: start,
      zoomMode: 'fitWidth',
      zoom: 1,
      rotation: 0,
      continuous: !comic,
      spread: false,
      rtl: false,
      deviceScale: window.devicePixelRatio || 1,
      pageSize: null,
      pages: {},
      theme: pdfThemeName(),
      themeBg: (state.theme && state.theme.background) || null,
      find: { query: '', hits: [], index: -1 },
      outline: (pdf.outline && pdf.outline.length) ? pdf.outline : null,
      /* 预载窗口：连续滚动时提前请求多少页（用户在主程序「设置 → 阅读」里改，范围 1–1000）。 */
      preloadPages: pdfClampInt(pdf.preloadPages, PDF_PRELOAD_DEFAULT),
      cacheLimit: 0,
      requestSeq: 0,
      requestCount: 0
    };

    state.pdf.preloadPages = Math.max(1, Math.min(1000, state.pdf.preloadPages));
    /* 前端页缓存要装得下"预载窗口 + 已看过的几页"，否则刚预载完就被自己 evict 掉。
       宿主页位图 LRU 最多 64 页，前端留 200 是余量，别让 1000 真的占满内存。 */
    state.pdf.cacheLimit = Math.max(PDF_PAGE_CACHE, Math.min(state.pdf.preloadPages + 8, 200));

    /* 从宿主上次存的页码恢复：这不算"用户翻页"，所以去重标记先对齐到起始页，
       否则用户一动滚动就上报一次和恢复值相同的进度（设计文档 §8.1）。 */
    pdfLastReportedPage = start;

    document.body.classList.add('ets-pdf');

    /* 标题块在 PDF/漫画模式下一律不显示（用户要求：那个位置放页码控件，不要书名，
       更不要 HTML 里的占位文案「正在打开」）。隐藏由 body.ets-pdf #heading 负责，这里顺手清文案。 */
    var titleEl = document.getElementById('chapterTitle');

    if (titleEl) { titleEl.textContent = ''; }

    /* 工具栏排成两行：上一页 … 下一页 / 页控件 … 主题（见 ensurePdfToolbarRows）。 */
    ensurePdfToolbarRows();

    /* T6-2 里置灰的四个按钮从这一刻起真的可用：清掉 disabled 与那句 Tooltip。 */
    ['pdfRotate', 'pdfLayout', 'pdfSpread', 'btnToc'].forEach(function (id) {
      var el = document.getElementById(id);

      if (!el) { return; }

      el.disabled = false;

      if (id === 'btnToc' && !state.pdf.outline) { el.title = '目录（这本书没有目录）'; }
    });

    var wrap = pdfPagesEl();

    if (wrap) { wrap.hidden = false; }

    applyPdfTheme();
    renderToc();
    closeTocPanel();

    if (total <= 0) {
      /* 打不开的原因（加密 / 不支持的算法 / 文件坏了）由宿主下发，直接告诉用户；
         没给原因才退到通用文案。 */
      pdfNotice((pdf && pdf.unavailable) || '这本书没有可以显示的页面。');
      updatePdfUi();
      setPdfLoading(false);
      return;
    }

    updatePdfLayout();

    /* 用户 2026-10-21：从上次保存的页码恢复（同格式精确到页）。
       只把 state.pdf.page 设成 start 是不够的：连续滚动模式下 #pdfPages 的
       scrollTop 还停在 0，用户看到的仍是第 1 页（页面总数/页码控件却已是第 N 页），
       而且一动滚动 onPdfScroll 就会把进度改回第 1 页。这里必须真的滚过去。 */
    pdfScrollToSavedPage(start);

    updatePdfVisible();
    updatePdfUi();
    pdfUpdateToolbarRowsForWidth();
    markCurrentToc();

    /* 重进这本书时，上一次留下的状态条提示（如果有）要清掉。 */
    setPdfLoading(false);

    /* "载入中"不能靠定时器猜：可见页请求发出去之后，谁先回包谁就把提示撤掉。
       400 ms 的延迟只是为了不给"本机缓存命中"这种快到看不见的加载闪一条提示。 */
    window.setTimeout(function () {
      if (isPdf()) { setPdfLoading(pdfAnyPending()); }
    }, 400);
  }

  /* 打开时按保存的页码把滚动容器定位过去（用户 2026-10-21）。
     与 pdfGoto 的连续分支同一口径：目标页顶部对齐容器顶部，留 PDF_PAGE_GAP 的余量。
     此刻还没量到真实页尺寸，offsetTop 是按 A4 估的；等第一张页位图回包后
     updatePdfLayout 会用 pdfWithScrollCompensation 把这一页锚住，位置不会漂。
     非连续模式（漫画 / 单页翻页）本来就只显示当前页那一行，不需要滚。 */
  function pdfScrollToSavedPage(page) {
    var p = state.pdf;
    var wrap = pdfPagesEl();

    if (!p || !wrap || !p.continuous || page <= 0) { return; }

    var obj = p.pages[page];

    if (!obj || !obj.element) { return; }

    pdfSuppressScroll = true;
    wrap.scrollTop = Math.max(0, obj.element.offsetTop - PDF_PAGE_GAP);
    window.setTimeout(function () { pdfSuppressScroll = false; }, 0);
  }

  /* 还有可见页在等宿主渲染吗（"载入中"提示跟着它开关）。 */
  function pdfAnyPending() {
    var p = state.pdf;

    if (!p) { return false; }

    var visible = visiblePdfPages();
    var i;

    for (i = 0; i < visible.length; i++) {
      var obj = p.pages[visible[i]];

      if (obj && obj.state === 'pending') { return true; }
    }

    return false;
  }

  /* PDF 工具栏的按钮装配（EPUB 模式下不显示，装了也无害）。 */
  /* 目录抽屉的「编辑目录」：交给宿主打开书籍编辑器（章节树 + 书籍信息 + 封面）。 */
  function wireTocEdit() {
    var edit = document.getElementById('tocEdit');

    if (edit) {
      edit.addEventListener('click', function () { post('reader.editToc', {}); });
    }
  }

  function wirePdfBar() {
    var pageInput = document.getElementById('pdfPageInput');
    var wrap = pdfPagesEl();

    if (pageInput) {
      pageInput.addEventListener('keydown', function (event) {
        if (event.key !== 'Enter') { return; }

        event.preventDefault();
        /* 别让渲染进程的全局键盘处理再吃一遍（EPUB 那套翻页/查找在这里都不该生效） */
        event.stopPropagation();
        pdfGoto(Number(pageInput.value) - 1, 'user');
        pageInput.blur();
      });

      pageInput.addEventListener('blur', function () { updatePdfToolbarState(); });
    }

    if (wrap) {
      wrap.addEventListener('scroll', onPdfScroll);
      wrap.addEventListener('wheel', onPdfScroll, { passive: true });
    }

    bindPdf('pdfPrev', function () { pdfStepPage(-1); });
    bindPdf('pdfNext', function () { pdfStepPage(1); });
    bindPdf('pdfRtl', pdfToggleRtl);
    bindPdf('pdfZoomIn', function () { pdfZoomStep(1); });
    bindPdf('pdfZoomOut', function () { pdfZoomStep(-1); });
    bindPdf('pdfFitPage', function () { pdfSetView({ fit: 'Fit' }); });
    bindPdf('pdfFitWidth', function () { pdfSetView({ fit: 'FitH' }); });
    bindPdf('pdfRotate', function () { pdfRotate(); });
    bindPdf('pdfLayout', function () { pdfToggleContinuous(); });
    bindPdf('pdfSpread', function () { pdfToggleSpread(); });
    bindPdf('pdfSearch', function () { openFindBar(); });
    bindPdf('pdfFullscreen', function () { toggleFullscreen(); });
    bindPdf('pdfOpenExternal', function () { pdfOpenExternal(); });
  }

  function bindPdf(id, handler) {
    var el = document.getElementById(id);

    if (el) { el.addEventListener('click', handler); }
  }

  /* 给冒烟用的只读探针：验收脚本靠它断言，不用截图猜。
     四个"从置灰变可用"的按钮也在这里读 disabled。 */
  function pdfStateForProbe() {
    var p = state.pdf;
    var rendered = [];
    var failed = [];
    var imageCount = 0;
    var page;

    if (p) {
      for (page in p.pages) {
        if (!p.pages.hasOwnProperty(page)) { continue; }

        var obj = p.pages[page];
        var el = obj.element;

        if (el && el.offsetParent !== null) {
          rendered.push(Number(page));

          if (obj.isImagePage) { imageCount += 1; }
        }

        if (obj.state === 'failed') { failed.push(Number(page)); }
      }
    }

    var current = p && p.pages[p.page] ? p.pages[p.page] : null;

    /* 预载窗口内的页里，哪些已经拿到图（冒烟据此断言"预载生效"）。 */
    var prefetched = [];

    if (p) {
      for (var ahead = 1; ahead <= Math.max(2, p.preloadPages || 0); ahead++) {
        var objAhead = p.pages[p.page + ahead];

        if (objAhead && objAhead.state === 'ready' && objAhead.url) { prefetched.push(p.page + ahead); }
      }
    }

    return {
      kind: state.kind,
      page: p ? p.page : -1,
      totalPages: p ? p.totalPages : -1,
      zoomMode: p ? p.zoomMode : null,
      zoom: p ? p.zoom : null,
      rotation: p ? p.rotation : 0,
      continuous: p ? !!p.continuous : false,
      spread: p ? !!p.spread : false,
      preloadPages: p ? (p.preloadPages || 0) : 0,
      cacheLimit: p ? (p.cacheLimit || 0) : 0,
      toolbarWide: document.body.classList.contains('ets-pdf-wide'),
      toolbarOneRowHeight: pdfDom.oneRowHeight || 0,
      toolbarTotalHeight: pdfDom.totalHeight || 0,
      rowMode: pdfDom.rowMode || '',
      rowCount: (pdfDom.rows || []).length,
      rowSizes: (pdfDom.rows || []).slice(0, 4).map(function (r) { return r.children.length; }),
      /* 当前页所在那一行里有哪些页（按 DOM 顺序）。双页非滚动时用来确认"两页都在行里"。 */
      rowPages: (function () {
        if (!p) { return []; }

        var rowEl = pdfDom.rows[pdfRowIndexOf(p.page)];

        if (!rowEl) { return []; }

        var out = [];

        for (var k = 0; k < rowEl.children.length; k++) {
          var a = rowEl.children[k].getAttribute('data-page');

          if (a !== null) { out.push(Number(a)); }
        }

        return out;
      })(),
      headingHidden: (function () {
        var h = document.getElementById('heading');

        return h ? window.getComputedStyle(h).display === 'none' : false;
      })(),
      scrollTop: (function () {
        var w = pdfPagesEl();

        return w ? Math.round(w.scrollTop) : 0;
      })(),
      /* 用户 2026-10-21：滚动容器**顶部真正露出来的那一页**（0 基）。
         和 page 的区别：page 是"状态里记的当前页"，scrollTop 没跟上时它可以是 5、
         用户看到的却还是第 1 页。恢复进度的验收必须断言这一个。 */
      topVisiblePage: (function () {
        try {
          return pdfTopVisiblePage();
        } catch (ignored) {
          return -1;
        }
      })(),
      comic: isComic(),
      rtl: p ? !!p.rtl : false,
      prefetchedPages: prefetched,
      theme: p ? p.theme : null,
      renderedPages: rendered,
      requestCount: p ? (p.requestCount || 0) : 0,
      failedPages: failed,
      charCount: current && current.chars ? current.chars.length : 0,
      hitCount: pdfFindState.hits ? pdfFindState.hits.length : 0,
      imageCount: imageCount,
      bodyHasPdfClass: document.body.classList.contains('ets-pdf'),
      greyed: ['pdfRotate', 'pdfLayout', 'pdfSpread', 'btnToc'].map(function (id) {
        var el = document.getElementById(id);

        return { id: id, disabled: !!(el && el.disabled), title: el ? el.title : '' };
      })
    };
  }

  function pdfSetViewForProbe(view) {
    pdfSetView(view || {});
    return pdfStateForProbe();
  }

  function pdfGotoForProbe(page) {
    pdfGoto(page, 'user');
    return pdfStateForProbe();
  }

  function pdfHasBar() {
    var el = document.getElementById('pdfBar');

    if (!el) { return false; }

    var cs = window.getComputedStyle ? window.getComputedStyle(el) : null;

    return !!(cs && cs.display !== 'none' && cs.visibility !== 'hidden');
  }

  function pdfZoomForProbe(step) {
    pdfZoomStep(step);
    return pdfStateForProbe();
  }

  function pdfToggleForProbe(which) {
    if (which === 'spread') { pdfToggleSpread(); }
    else if (which === 'layout') { pdfToggleContinuous(); }
    else if (which === 'rotate') { pdfRotate(); }
    else if (which === 'rtl') { pdfToggleRtl(); }

    return pdfStateForProbe();
  }

  function pdfFindForProbe(text) {
    runPdfFind(text);
    return pdfFindState;
  }

  function pdfFindStepForProbe(forward) {
    return pdfFindStep(!!forward);
  }

  function pdfRefreshForProbe() {
    updatePdfLayout();
    updatePdfVisible();
    return pdfStateForProbe();
  }

  /* 把视图恢复成默认（适应宽度 / 连续或漫画单页 / 不旋转 / 从左到右）。冒烟复位时用。 */
  function pdfResetView() {
    var p = state.pdf;

    if (!p) { return; }

    p.rotation = 0;
    p.zoomMode = 'fitWidth';
    p.zoom = 1;
    p.continuous = !isComic();
    p.spread = false;
    p.rtl = false;
    p.page = pdfClampPage(p.page);

    pdfRelayout();
  }

  // ---------- 章节 ----------

  function openChapter(index, scrollTop) {
    if (!state.manifest || !state.manifest.spine || state.manifest.spine.length === 0) {
      notice('这本书里没有可显示的章节。');
      return;
    }

    if (index < 0 || index >= state.manifest.spine.length) {
      return;
    }

    var chapter = state.manifest.spine[index];
    state.index = index;
    state.pendingScrollTop = scrollTop || 0;

    var title = chapter.title || ('第 ' + (index + 1) + ' 章');

    document.getElementById('chapterTitle').textContent = title;
    /* 第二行的「第 12/14 章　第 1/4 页」由 syncChapterMeta 统一更新（里面的"当前第几"是输入框）。
       页码要等 iframe 排好版才准，那一半在 onFrameLoad -> setPage -> updateModeUi 里再刷一次。 */
    syncChapterMeta();

    updateButtons();

    post('reader.chapterChanged', { index: index, title: title });

    markCurrentToc();

    updateStatusBar();

    /* T4-5：换编码会重建缓存，章节路径却不变，靠清单里的令牌绕开 WebView2 的 HTTP 缓存 */
    var token = state.manifest && state.manifest.contentToken;

    frame.src = token
      ? (chapter.href + (chapter.href.indexOf('?') >= 0 ? '&' : '?') + 'v=' + token)
      : chapter.href;
  }

  /* 翻页必须经由宿主，不能直接改 iframe.src。
   *
   * 章节是"翻到哪章物化哪章"的：文件要等宿主去生成。这里若直接
   * frame.src = 'chapters/0002.html'，浏览器要的是一个还不存在的文件，
   * Chromium 会渲染出"它可能已被移动、编辑或删除"那张错误页
   * （2026-10-02 用户实测踩到：第 1 章正常，翻到第 2 章就变成错误页）。
   *
   * 所以这里只发意图，宿主收到后先物化章节、再回发 reader.gotoChapter。
   * "导航之前文件一定存在"这个不变式因此只由宿主维护一处。
   */
  /* T5-2W 第三步：保存后重新加载当前章并保持阅读位置。
     位置用"章内比例"传（capturePercentInChapter / applyPercentInChapter 同一口径）；
     换一个 contentToken 是为了绕开 WebView2 对同一个 chapters/NNNN.html 地址的 HTTP 缓存
     （章节路径不变，不换令牌会拿到旧内容）。 */
  function reloadChapter(index, percent, token) {
    if (!state.manifest || !state.manifest.spine || state.manifest.spine.length === 0) {
      return;
    }

    if (typeof index !== 'number' || index < 0 || index >= state.manifest.spine.length) {
      return;
    }

    if (token) {
      state.manifest.contentToken = token;
    }

    var value = typeof percent === 'number' && isFinite(percent)
      ? Math.min(1, Math.max(0, percent))
      : null;

    state.pendingPage = null;
    state.pendingLocator = value === null ? null : {
      chapterIndex: index,
      chapterHref: state.manifest.spine[index].href,
      percentInChapter: value,
      /* 故意不给像素值：正文改过之后"按比例回位"比"按像素回位"稳（restoreLocator 的第二级） */
      scrollTop: null,
      scrollHeight: null
    };
    state.pendingFingerprint = null;

    openChapter(index, 0);
  }

  function requestChapter(index) {
    if (!state.manifest || !state.manifest.spine) {
      return;
    }

    if (index < 0 || index >= state.manifest.spine.length) {
      return;
    }

    if (index === state.index) {
      return;
    }

    post('reader.chapterRequest', { index: index });
  }

  function gotoRelative(delta) {
    requestChapter(state.index + delta);
  }

  function updateButtons() {
    var total = state.manifest && state.manifest.spine ? state.manifest.spine.length : 0;

    /* 「编辑本章」的监听**不在这里绑**：updateButtons 每次开章都会跑，以前这里每次 addEventListener，
       于是开过 N 章之后点一下会同时发 N 条 reader.editChapter，叠出 N 个"可视编辑章节"窗口
       （用户 2026-10-11 截图）。它和其它按钮一样，只在一次性初始化里绑一次。 */
    document.getElementById('btnPrev').disabled = state.index <= 0;
    document.getElementById('btnNext').disabled = state.index >= total - 1;
  }

  // ---------- iframe ----------

  function onFrameLoad() {
    attachToFrame();
    applyContentCss();
    applyPagedCss();

    var loc = null;
    var fp = null;

    // 只有"这一章正是上次读的那一章"才恢复；翻到别的章要老老实实从章首开始
    if (state.pendingLocator && state.pendingLocator.chapterIndex === state.index) {
      loc = state.pendingLocator;
      fp = state.pendingFingerprint;
    }

    var scrollTop = state.pendingScrollTop;

    state.pendingLocator = null;
    state.pendingFingerprint = null;
    state.pendingScrollTop = 0;

    function apply() {
      /* 分页模式下"像素级定位"没有意义（横向排版的 scrollTop 恒为 0），
         只认两个信号：待定的页号（翻章时指定，'last' = 末页），
         以及上次读到的"章内比例"。 */
      if (isPaged()) {
        var pagedTarget = state.pendingPage;
        state.pendingPage = null;

        if (pagedTarget === 'last') {
          setPage(measurePageCount() - 1);
        } else if (typeof pagedTarget === 'number') {
          setPage(pagedTarget);
        } else if (loc && typeof loc.percentInChapter === 'number' && loc.percentInChapter > 0) {
          applyPercentInChapter(loc.percentInChapter);
        } else {
          setPage(0);
        }

        updateModeUi();

        /* T4-4：目录项带锚点时，先落页再滚到锚点 */
        if (applyPendingFragment()) {
          reportLocation();
        }

        applyPendingFind();
        applyAnnotations();
        return;
      }

      state.pendingPage = null;

      var restored = null;

      if (loc) {
        restored = restoreLocator(loc, fp);
      }

      if (!restored && scrollTop > 0) {
        scrollTo(scrollTop);
      }

      /* T4-4：目录项带锚点时，滚到章内锚点（优先于"上次读到哪"） */
      if (applyPendingFragment()) {
        reportLocation();
      }

      applyPendingFind();
      applyAnnotations();
    }

    if (loc) {
      /* 等图片稳定再恢复。实测踩到的真问题：iframe 的 load 事件之后图片
         还在解码，章节高度会继续长，于是"排版变了没有"的判断被误判成变了，
         像素级恢复被降级成百分比恢复，落偏一百多像素。 */
      whenImagesSettled(frame.contentDocument, function () {
        apply();
        reportLocation();
      });
    } else {
      apply();
    }

    post('reader.rendered', { firstPaintMs: Math.round(performance.now()) });
  }

  /* 等章节里的图片加载完再回调。最多等 900ms：
     图片一直不来也不能把"打开一本书"卡住。 */
  function whenImagesSettled(doc, callback) {
    var imgs = doc && doc.images ? Array.prototype.slice.call(doc.images) : [];
    var pending = imgs.filter(function (img) { return !img.complete; });

    if (pending.length === 0) {
      callback();
      return;
    }

    var left = pending.length;
    var done = false;

    function finish() {
      if (done) {
        return;
      }

      left = left - 1;

      if (left <= 0) {
        done = true;
        callback();
      }
    }

    pending.forEach(function (img) {
      img.addEventListener('load', finish);
      img.addEventListener('error', finish);
    });

    setTimeout(function () {
      if (!done) {
        done = true;
        callback();
      }
    }, 900);
  }

  /* T7-2 第 14 项：图片加载失败 -> 占位块。
     只做三件事：加类（样式在 buildContentCss 里）、补 alt、补 title。
     幂等：同一张图重复失败不会叠加属性。 */
  function markMissingImage(img) {
    if (!img || img.getAttribute('data-ets-missing') === '1') {
      return;
    }

    img.setAttribute('data-ets-missing', '1');
    img.className = (img.className ? img.className + ' ' : '') + 'ets-img-missing';

    if (!img.getAttribute('alt')) {
      img.setAttribute('alt', '图片无法显示');
    }

    img.setAttribute('title', '图片加载失败：' + (img.getAttribute('src') || ''));
  }

  /* 扫一遍当前章节里已经失败的图片（iframe load 之后调用）。 */
  function installImageFallbacks(doc) {
    var imgs = doc && doc.querySelectorAll ? doc.querySelectorAll('img') : [];

    for (var i = 0; i < imgs.length; i++) {
      var img = imgs[i];
      if (img.complete && img.naturalWidth === 0) {
        markMissingImage(img);
      }
    }
  }

  function attachToFrame() {
    var doc = frame.contentDocument;
    var win = frame.contentWindow;

    if (!doc || !win) {
      // 同源策略或文档还没建好。正常情况下不该发生：两者都在 ebook.local 下。
      reportError('无法访问章节内容（同源策略）。');
      return;
    }

    win.onerror = function (message, source, line, column, error) {
      reportError('章节内脚本错误：' + message, error && error.stack ? error.stack : null);
      return false;
    };

    /* 资源加载失败（书里引用了不存在的图片、CSS 里 @import 了不存在的文件）
       不会冒泡到 window.onerror，只能在捕获阶段接。不加这个的话
       控制台会一片红而宿主完全不知道（执行计划 T1-5 坑 2）。
       T7-2 第 14 项：图片失败时同时打占位，不让正文白屏。 */
    doc.addEventListener('error', function (event) {
      var target = event.target;
      if (!target || !target.tagName) {
        return;
      }

      if (target.tagName === 'IMG') {
        markMissingImage(target);
      }

      post('reader.resourceError', {
        tag: target.tagName,
        url: target.src || target.href || ''
      });
    }, true);

    /* iframe 的 load 事件在图片都加载完之后才来，所以此刻已经失败的图片
       收不到上面的 error 事件；这里补扫一遍"complete 但宽高为 0"的图。 */
    installImageFallbacks(doc);

    // 链接点击交给宿主决定：可能是章内锚点、跨章链接、外链
    doc.addEventListener('click', function (event) {
      var anchor = event.target && event.target.closest
        ? event.target.closest('a')
        : null;

      if (!anchor) {
        return;
      }

      event.preventDefault();

      post('reader.linkClicked', {
        href: anchor.getAttribute('href') || '',
        resolved: anchor.href || ''
      });
    }, true);

    win.addEventListener('scroll', onScroll, { passive: true });
    win.addEventListener('keydown', onKeyDown);

    /* 分页模式的滚轮翻页要能 preventDefault（滚轮在正文上时事件进的是 iframe），
       所以这里必须是 passive:false，否则浏览器忽略 preventDefault、
       页面会一边横向滚一边又翻一页。 */
    win.addEventListener('wheel', onWheel, { passive: false });

    /* 窗口大小变了列宽就变了，页号会漂：按"章内比例"重新落位 */
    win.addEventListener('resize', onViewportResize);

    /* T6-5：正文里的选字与右键必须挂在 iframe 文档上——它们不冒泡到外壳。
       每次章节加载都是新的 document，所以这里挂一次是安全的（不会累积）。 */
    doc.addEventListener('mouseup', onContentMouseUp);
    doc.addEventListener('mousedown', onContentMouseDown);
    doc.addEventListener('contextmenu', onContentContextMenu);
    doc.addEventListener('click', onContentClick);
  }

  // ---------- 位置上报 ----------

  function onScroll() {
    /* 分页模式下文档永远不该有滚动量：页框是 fixed 的，正常就滚不动；
       万一有别的路径把它滚起来（键盘、连接锚点、选区自动滚屏），这里立刻拉回 0。
       否则整页会向上跑出 iframe，页顶文字被工具栏盖住（用户 2026-10-10 截图）。 */
    if (isPaged()) {
      var pagedWin = contentWin();

      if (pagedWin && (pagedWin.scrollY || pagedWin.scrollX)) {
        pagedWin.scrollTo(0, 0);
      }

      return;
    }

    var now = Date.now();
    var elapsed = now - state.lastReportMs;

    /* 节流 300ms（设计文档 8.1.2）。但节流不能"丢"事件：
       连续滚动停止之后必须补报一次最后的位置，否则用户真正的阅读位置
       永远报不出去，存下来的是滚动中途的某个位置（实测：停在 422px，
       库里存的是 317px，重开就落偏一百多像素）。 */
    if (elapsed < 300) {
      if (!state.trailingTimer) {
        state.trailingTimer = setTimeout(function () {
          state.trailingTimer = null;
          state.lastReportMs = Date.now();
          reportLocation();
        }, 300 - elapsed);
      }

      return;
    }

    state.lastReportMs = now;
    reportLocation();
  }

  function reportLocation() {
    var win = frame && frame.contentWindow;
    var doc = frame && frame.contentDocument;

    if (!win || !doc) {
      return;
    }

    // 状态条跟着位置走（章号 + 全书百分比）
    updateStatusBar();

    if (isPaged()) {
      /* 分页模式的位置用"页号 / 总页数"表达。
         percent 的含义保持与滚动模式一致（视口末端在整章里的比例），
         这样宿主、既有判据、以及"关窗重开回到原位置"都不用改；
         pageIndex / pageCount / mode 是给冒烟与将来用的附加字段。 */
      var count = measurePageCount();
      var page = Math.max(0, Math.min(count - 1, currentPageIndex()));
      state.pageIndex = page;

      var pagedPercent = Math.min(1, Math.max(0, (page + 1) / count));
      var roundedPaged = Math.round(pagedPercent * 1000) / 1000;

      updateModeUi();

      post('reader.locatorChanged', {
        chapterIndex: state.index,
        chapterHref: chapterHrefOf(state.index),
        scrollTop: 0,
        scrollHeight: currentScrollHeight(),
        percent: roundedPaged,
        percentInChapter: roundedPaged,
        pageIndex: page,
        pageCount: count,
        mode: 'paged',
        fingerprint: collectFingerprint()
      });
      return;
    }

    var scrollTop = Math.round(win.scrollY || 0);
    var viewportHeight = Math.round(win.innerHeight || 0);
    var scrollHeight = currentScrollHeight();
    var percent = Math.min(1, Math.max(0, (scrollTop + viewportHeight) / scrollHeight));
    var rounded = Math.round(percent * 1000) / 1000;

    /* percent 这个字段名保持不动：T1-5 的冒烟判据已经在用它。
       percentInChapter 与它同义，是给 T1-6 的三级定位读的。 */
    post('reader.locatorChanged', {
      chapterIndex: state.index,
      chapterHref: chapterHrefOf(state.index),
      scrollTop: scrollTop,
      scrollHeight: scrollHeight,
      percent: rounded,
      percentInChapter: rounded,
      fingerprint: collectFingerprint()
    });
  }

  // ---------- 三级定位（设计文档 8.4） ----------

  function currentScrollHeight() {
    var doc = frame && frame.contentDocument;

    if (!doc) {
      return 1;
    }

    return Math.max(1, doc.documentElement ? doc.documentElement.scrollHeight : 1);
  }

  function maxScrollTop() {
    var win = frame && frame.contentWindow;
    var viewport = win ? (win.innerHeight || 0) : 0;

    return Math.max(0, currentScrollHeight() - viewport);
  }

  function scrollTo(top) {
    var win = frame && frame.contentWindow;

    if (!win) {
      return;
    }

    try {
      win.scrollTo(0, Math.max(0, top));
    } catch (ignored) {
      // 跨文档滚动偶发失败不影响阅读，忽略
    }
  }

  function chapterHrefOf(index) {
    var spine = state.manifest && state.manifest.spine;

    if (!spine || index < 0 || index >= spine.length) {
      return null;
    }

    return spine[index].href || null;
  }

  function clamp01(value) {
    return Math.max(0, Math.min(1, value));
  }

  /* 当前视口里第一个可见的块。用 getBoundingClientRect 而不是 offsetTop：
     offsetTop 是相对最近的定位祖先的，书里的 CSS 一旦设了 position 就会算错。 */
  function firstVisibleBlock(doc) {
    var nodes = doc.querySelectorAll('p, h1, h2, h3, li, div');
    var i, rect;

    for (i = 0; i < nodes.length; i++) {
      rect = nodes[i].getBoundingClientRect();

      if (rect.bottom > 0 && rect.height > 0) {
        return nodes[i];
      }
    }

    return null;
  }

  /* 归一化：去掉空白与零宽字符，再做 NFKC（设计文档 8.4）。
     不归一化的话，指纹在重新排版后基本对不上。 */
  function normalizeText(text) {
    if (!text) {
      return '';
    }

    var s = String(text).replace(/[\s\u200B-\u200F\uFEFF]+/g, '');

    try {
      s = s.normalize('NFKC');
    } catch (ignored) {
      // 老引擎不支持就算了，不影响其它两级
    }

    return s;
  }

  function collectFingerprint() {
    var doc = frame && frame.contentDocument;

    if (!doc) {
      return null;
    }

    var el = firstVisibleBlock(doc);

    if (!el) {
      return null;
    }

    var text = normalizeText(el.textContent);

    if (!text) {
      return null;
    }

    return { pre: text.slice(0, 30), mid: text.slice(0, 20), post: text.slice(-30) };
  }

  function searchFingerprint(fp) {
    var doc = frame && frame.contentDocument;

    if (!doc || !fp || !fp.pre) {
      return null;
    }

    var pre = normalizeText(fp.pre);

    if (!pre) {
      return null;
    }

    var nodes = doc.querySelectorAll('p, h1, h2, h3, li, div');
    var fuzzy = pre.slice(0, 12);
    var i, text, rect;

    /* 先精确匹配整段前 30 字 */
    for (i = 0; i < nodes.length; i++) {
      text = normalizeText(nodes[i].textContent);

      if (text && text.indexOf(pre) >= 0) {
        rect = nodes[i].getBoundingClientRect();
        return rect.top + (frame.contentWindow.scrollY || 0) - 8;
      }
    }

    /* 对不上再退化成前 12 字模糊匹配（正文被改过一两个字的场合） */
    if (fuzzy.length >= 6) {
      for (i = 0; i < nodes.length; i++) {
        text = normalizeText(nodes[i].textContent);

        if (text && text.indexOf(fuzzy) >= 0) {
          rect = nodes[i].getBoundingClientRect();
          return rect.top + (frame.contentWindow.scrollY || 0) - 8;
        }
      }
    }

    return null;
  }
  /* 按设计文档 8.4 的顺序逐级回退：像素 -> 章内比例 -> 文本指纹 -> 章首。
     返回命中的级别名（scroll / percent / fingerprint / degraded），全都没命中返回 null。
     识别不出"内容变了"这种事的后果只是位置不准，绝不能因此让打开书失败。 */
  function restoreLocator(loc, fp) {
    var win = frame && frame.contentWindow;

    if (!win || !loc) {
      return null;
    }

    var max = maxScrollTop();
    var hasScroll = loc.scrollTop !== null && loc.scrollTop !== undefined;
    var hasPercent = loc.percentInChapter !== null && loc.percentInChapter !== undefined;

    /* 第一级：像素。只有排版没变时才敢用：用 scrollHeight 的变化幅度判断
       （超过 10% 就当排版变过，交给第二级）。 */
    if (hasScroll) {
      if (loc.scrollTop > max + 1) {
        /* 记的位置比这一章还高：内容被改过（用户编辑过书或换了排版）。
           这不是错误，降级到章首并如实告诉用户，不能默默跳走。 */
        notice('内容可能有变化，已回到本章开头。');
        scrollTo(0);
        return 'degraded';
      }

      var sameLayout = !loc.scrollHeight || loc.scrollHeight <= 0 ||
        Math.abs(currentScrollHeight() - loc.scrollHeight) / loc.scrollHeight <= 0.1;

      if (sameLayout) {
        scrollTo(loc.scrollTop);
        return 'scroll';
      }
    }

    /* 第二级：章内比例。换字号 / 换字体 / 换窗口宽度后仍然大致对得上。 */
    if (hasPercent) {
      var ratio = clamp01(loc.percentInChapter);
      scrollTo(max * ratio);

      var got = max > 0 ? (win.scrollY || 0) / max : 0;

      if (Math.abs(got - ratio) < 0.05) {
        return 'percent';
      }
    }

    /* 第三级：文本指纹。最稳，代价最高（要遍历块元素）。 */
    if (fp) {
      var hit = searchFingerprint(fp);

      if (hit !== null && hit !== undefined) {
        scrollTo(hit);
        return 'fingerprint';
      }
    }

    /* 什么都对不上：回到章首，并说明原因。 */
    if (hasScroll || hasPercent) {
      notice('内容可能有变化，已回到本章开头。');
    }

    scrollTo(0);
    return null;
  }
  // ---------- 分页阅读（T4-1）：一页就是独立的一页 ----------

  /* 结构（分页模式下）：
       html（裁剪到视口）
          body（margin/padding 清零）
              #ets-page-frame     **页框**：固定定位(fixed)在(左边距,上边距)，宽高 = 一页，
                                     overflow:hidden  一页就是它，谁也别想漏进来
                  #ets-flow       正文流：多列，列宽 = 页框宽，列高 = 页框高
       翻页 = 把 #ets-flow 横向平移**正好一页宽**（140ms），其它页被页框裁掉。
    要点（都是上一版翻车的地方）：
      - 裁剪区必须**正好等于一页**（页框），不能是"视口"或"列带"：
        否则左边会漏上一页的字尾、右边会漏下一页的字段（用户 2026-10-08 截图反馈）。
      - 列宽必须**精确等于页框宽**（像素而不是 100vw：100vw 会把内边距算进去），
        这样平移量 = 列宽，页与页严丝合缝。
      - 列高 = 页框高，放不下的行会整行进下一列，页框再把超出的部分裁掉
         不会出现"最后一行只显示一半"。
      - 四个边距都来自排版设置（marginH/marginV），页框是"视口 - 两边距"，
        所以每页的左右上下的留白都一样。 */

  var PAGED_STYLE_ID = 'ets-paged-css';
  var PAGE_FRAME_ID = 'ets-page-frame';
  var PAGE_FLOW_ID = 'ets-flow';

  function isPaged() {
    return state.mode === 'paged';
  }

  function contentWin() {
    return frame && frame.contentWindow;
  }

  function contentDoc() {
    return frame && frame.contentDocument;
  }

  function frameElement() {
    var doc = contentDoc();
    return doc ? doc.getElementById(PAGE_FRAME_ID) : null;
  }

  function flowElement() {
    var doc = contentDoc();
    return doc ? doc.getElementById(PAGE_FLOW_ID) : null;
  }

  /* 页边距：直接取排版设置里的 marginH / marginV（滚动模式下它们就是正文的内边距），
     所以两种模式的留白一致。 */
/* 字体：判据 5  系统里没这个字体也不能崩，所以永远带上回退链。 */
  function wrapFontFamily(name) {
    var v = (name || '').trim();

    if (!v) {
      return '"Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif';
    }

    return v.indexOf(',') >= 0 ? v : ('"' + v.replace(/"/g, '') + '", "Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif');
  }

  /* 排版设置取值：全部收敛到合法范围（滑块给的是字符串，也要能挡住非法值）。 */
  function textAlignOf(y) {
    var v = (y || {}).textAlign;
    return v === 'left' || v === 'center' ? v : 'justify';
  }

  function paraSpacingOf(y) {
    var v = Number((y || {}).paraSpacing);
    return isFinite(v) ? Math.min(2, Math.max(0, v)) : 0.8;
  }

  function textIndentOf(y) {
    var v = Number((y || {}).textIndent);
    return isFinite(v) ? Math.min(4, Math.max(0, v)) : 2;
  }

  function columnsOf(y) {
    var v = Math.round(Number((y || {}).columns));
    return isFinite(v) ? Math.min(3, Math.max(1, v)) : 1;
  }

  function marginPercentOf(y) {
    var v = Number((y || {}).marginPercent);
    return isFinite(v) ? Math.min(20, Math.max(0, v)) : 5;
  }

  /* 分栏间距（px，0-256）。 */
  function columnGapPxOf(y) {
    var v = Number((y || {}).columnGap);
    return isFinite(v) ? Math.min(256, Math.max(0, Math.round(v))) : 24;
  }

  /* 是否显示分栏竖线（多栏才用得上）。 */
  function columnRuleOn(y) {
    var v = (y || {}).columnRule;
    return v === true || v === 'true' || v === 1 || v === '1';
  }

  /* 给颜色加透明度（分栏竖线用 35% 的字色）。拿不到就退回原色。 */
  function colorWithAlpha(color, alpha) {
    var text = String(color || '').trim();
    var m = /^#([0-9a-fA-F]{6})$/.exec(text);

    if (!m) {
      return text || 'rgba(128,128,128,0.35)';
    }

    var n = parseInt(m[1], 16);
    return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + alpha + ')';
  }

  /* 四边距各算各的（兼容老配置里只有一个 marginPercent 的情况）。 */
  function marginPctOf(y, side) {
    var raw = (y || {})['margin' + side];

    if (raw === undefined || raw === null || raw === '') {
      return marginPercentOf(y);
    }

    var v = Number(raw);
    return isFinite(v) ? Math.min(20, Math.max(0, v)) : 5;
  }

  /* 视口宽度（百分比基准）：这是 reader.js 顶层能拿到的对象，别用别的函数的形参。 */
  function viewportBase() {
    var base = 0;

    try {
      base = document.documentElement ? (document.documentElement.clientWidth || 0) : 0;
    } catch (e) {
      base = 0;
    }

    if (base <= 0) {
      try { base = window.innerWidth || 0; } catch (e) { base = 0; }
    }

    return base > 0 ? base : 1024;
  }

  /* 四边距（像素）：上 右 下 左 */
  function contentMargins() {
    var y = state.typography || {};
    var base = viewportBase();

    return {
      top: Math.round(base * marginPctOf(y, 'Top') / 100),
      right: Math.round(base * marginPctOf(y, 'Right') / 100),
      bottom: Math.round(base * marginPctOf(y, 'Bottom') / 100),
      left: Math.round(base * marginPctOf(y, 'Left') / 100)
    };
  }

  /* 正文内边距的 CSS 值：上 右 下 左 */
  function contentMarginsCss() {
    var m = contentMargins();
    return m.top + 'px ' + m.right + 'px ' + m.bottom + 'px ' + m.left + 'px';
  }

  /* 页边距：百分比 -> 像素（按视口宽度算，和正文 CSS 的百分比内边距同一口径；
     老配置只存了 marginH/marginV 像素值时继续兼容）。 */
  function contentMarginsPx() {
    var y = state.typography || {};

    if (y.marginPercent === undefined && y.marginH !== undefined) {
      return Math.max(0, Math.round(y.marginH));
    }

    /* 注意：这里不能用 win（它只在别的函数作用域里有），用外壳文档的视口宽度 */
    var base = 0;

    try {
      base = document.documentElement ? (document.documentElement.clientWidth || 0) : 0;
    } catch (e) {
      base = 0;
    }

    if (base <= 0) {
      try { base = window.innerWidth || 0; } catch (e) { base = 0; }
    }

    return base <= 0 ? 32 : Math.round(base * marginPercentOf(y) / 100);
  }

  function pageMargins() {
    var typography = state.typography || {};
    var m = contentMargins();
    var y = typography;
    var vw = viewportBase();
    var cols = Math.max(1, columnsOf(y));

    return {
      top: m.top,
      right: m.right,
      bottom: m.bottom,
      left: m.left,
      h: m.left,
      v: m.top
    };
  }

  /* 一页的几何：页框位置与尺寸（= 视口减去四个边距）。 */
  function pageMetrics() {
    var y = state.typography || {};
    var cols = Math.max(1, columnsOf(y));
    var vw = viewportBase();
    var win = contentWin();

    if (!win) {
      return { left: 0, top: 0, width: 1, height: 1 };
    }

    var margins = pageMargins();

    return {
      left: margins.left,
      top: margins.top,
      width: Math.max(80, Math.round((win.innerWidth || 0) - (margins.left + margins.right))),
      height: Math.max(80, Math.round((win.innerHeight || 0) - (margins.top + margins.bottom))),
      /* 分页模式的"一页 N 列"（用户要求）：列宽 = (页宽 - (N-1)*间距) / N，
         一页的推进量 stride = N * (列宽 + 间距)（列宽含间距排布，必须按 stride 推进才对得齐）。 */
      columns: cols,
      gap: cols > 1 ? columnGapPxOf(y) : 0,
      columnWidth: Math.max(40, Math.floor((vw - (margins.left + margins.right) - (cols - 1) * (cols > 1 ? columnGapPxOf(y) : 0)) / cols)),
      stride: 0
    };
  }

  function applyPagedCss() {
    var doc = contentDoc();

    if (!doc || !doc.head || !doc.body) {
      return;
    }

    var existing = doc.getElementById(PAGED_STYLE_ID);

    if (!isPaged()) {
      if (existing && existing.parentNode) {
        existing.parentNode.removeChild(existing);
      }

      teardownPageFrames(doc);
      return;
    }

    var metrics = pageMetrics();
    /* 页框必须用 fixed，不能用 absolute：absolute 是"定位在文档流里"的，
       iframe 文档一旦被人滚起来（键盘、链接锚点、选区自动滚屏、宿主脚本），整页就跟着上移，
       页顶文字被工具栏区域盖住（用户 2026-10-10 截图：《少女集中营》第 12/88 页首行被切）。
       fixed 的包含块是 iframe 视口，文档滚动带不走它；而且 fixed 不产生可滚动溢出，
       文档的 scrollHeight 就等于视口高，滚动量恒为 0。 */
    var css = 'html{height:100%!important;overflow:hidden!important;}'
      + 'body{margin:0!important;padding:0!important;height:100%!important;overflow:hidden!important;}'
      + '#' + PAGE_FRAME_ID + '{position:fixed;left:' + metrics.left + 'px;top:' + metrics.top + 'px;'
      + 'width:' + metrics.width + 'px;height:' + metrics.height + 'px;overflow:hidden;}'
      + '#' + PAGE_FLOW_ID + '{height:100%;column-width:' + metrics.columnWidth + 'px;column-gap:' + metrics.gap + 'px;'
      + 'column-fill:auto;column-rule:' + (metrics.columns > 1 && columnRuleOn(state.typography)
        ? ('1px solid ' + colorWithAlpha(state.theme.foreground, 0.35)) : 'none') + ';'
      + 'transform:translateX(0);transition:transform 140ms ease-out;}'
      + 'img,svg,figure,table,pre{break-inside:avoid;page-break-inside:avoid;}'
      // 超高图片/表格的规则（2026-10-08 用户口径）：放不下就整块挪到下一页自己占一页，
      // 不与文字混排；图片再按页框高度缩放，保证"完整显示"（表格无法缩放，超出部分会裁掉）。
      + '#' + PAGE_FLOW_ID + ' img,' + '#' + PAGE_FLOW_ID + ' svg,' + '#' + PAGE_FLOW_ID + ' figure,'
      + '#' + PAGE_FLOW_ID + ' table{max-width:' + metrics.columnWidth + 'px !important;'
      + 'max-height:' + metrics.height + 'px !important;}'
      + '#' + PAGE_FLOW_ID + ' img,' + '#' + PAGE_FLOW_ID + ' svg{object-fit:contain !important;}';

    if (existing) {
      existing.textContent = css;
    } else {
      var style = doc.createElement('style');
      style.id = PAGED_STYLE_ID;
      style.textContent = css;
      doc.head.appendChild(style);
    }

    ensurePageFrames(doc);
    setPage(state.pageIndex || 0);
  }

  /* 把正文搬进页框里的流；退出分页模式时原样搬回来（顺序不变）。 */
  function ensurePageFrames(doc) {
    if (doc.getElementById(PAGE_FRAME_ID)) {
      return;
    }

    var page = doc.createElement('div');
    page.id = PAGE_FRAME_ID;

    var flow = doc.createElement('div');
    flow.id = PAGE_FLOW_ID;

    while (doc.body.firstChild) {
      flow.appendChild(doc.body.firstChild);
    }

    page.appendChild(flow);
    doc.body.appendChild(page);
  }

  function teardownPageFrames(doc) {
    var page = doc.getElementById(PAGE_FRAME_ID);

    if (!page) {
      return;
    }

    var flow = doc.getElementById(PAGE_FLOW_ID);

    if (flow) {
      while (flow.firstChild) {
        doc.body.insertBefore(flow.firstChild, page);
      }
    }

    if (page.parentNode) {
      page.parentNode.removeChild(page);
    }
  }

  /* 一页的推进量（像素）：单栏时就是一页宽；一页 N 列时是 N*(列宽+间距)。 */
  /* 节流重排：拖动滑块时 input 会连发几十次，每次都重排整章会把界面卡死（"滑块拖一点就卡住"）。 */
  var pageRelayoutTimer = null;

  function schedulePageRelayout() {
    if (pageRelayoutTimer) {
      clearTimeout(pageRelayoutTimer);
    }

    pageRelayoutTimer = setTimeout(function () {
      pageRelayoutTimer = null;

      try {
        if (isPaged()) {
          applyPagedCss();
          setPage(currentPageIndex());
        }
      } catch (e) {
        reportError('重排分页失败：' + (e && e.message ? e.message : e));
      }
    }, 120);
  }

  function pageStride() {
    var metrics = pageMetrics();
    var stride = metrics.columns * (metrics.columnWidth + metrics.gap);
    return stride > 0 ? stride : metrics.width;
  }

  /* 正文流的总宽（页数 = 总宽 / 一页的推进量）。 */
  function stripWidth() {
    var flow = flowElement();
    var metrics = pageMetrics();

    if (!flow) {
      return metrics.width;
    }

    return Math.max(metrics.width, flow.scrollWidth || metrics.width);
  }

  /* 页数现算，不缓存：字体/图片/窗口尺寸都会改列数。 */
  function measurePageCount() {
    var started = performance.now();
    var metrics = pageMetrics();

    state.pageCount = Math.max(1, Math.round(stripWidth() / pageStride()));
    state.lastPaginateMs = Math.round((performance.now() - started) * 100) / 100;
    return state.pageCount;
  }

  function currentPageIndex() {
    return Math.max(0, Math.min(state.pageCount - 1, state.pageIndex || 0));
  }

  /* 显示第 N 页：流横向平移 n 页宽（页框负责裁剪，所以只有这一页可见）。 */
  function setPage(page) {
    var metrics = pageMetrics();
    var count = measurePageCount();
    var target = Math.max(0, Math.min(count - 1, page));
    var flow = flowElement();

    state.pageIndex = target;
    state.pageCount = count;
    state.lastOffsetX = target * pageStride();

    if (flow) {
      flow.style.transform = 'translateX(' + (-state.lastOffsetX) + 'px)';
    }

    updateModeUi();
    return target;
  }

  function turnPage(delta) {
    if (!isPaged()) {
      return false;
    }

    measurePageCount();

    var next = currentPageIndex() + delta;

    if (next < 0 || next > state.pageCount - 1) {
      return false;
    }

    setPage(next);
    reportLocation();
    return true;
  }

  /* 分页模式下所有输入都进这里：先翻页；翻不动才翻章。
     第一章第一页再往前 -> 停在原地（T4-1 判据 3）。 */
  function pageOrChapter(delta) {
    if (!isPaged()) {
      gotoRelative(delta);
      return;
    }

    if (turnPage(delta)) {
      return;
    }

    if (delta > 0) {
      gotoRelative(1);
      return;
    }

    if (state.index > 0) {
      /* 从本章第一页往回翻 = 上一章的最后一页（末页要等新章排好列才知道）。 */
      state.pendingPage = 'last';
      gotoRelative(-1);
      return;
    }

    notice('已经是第一页了。');
  }

  function capturePercentInChapter() {
    var win = contentWin();

    if (!win) {
      return 0;
    }

    if (isPaged()) {
      var count = measurePageCount();
      return count > 0 ? (currentPageIndex() + 1) / count : 0;
    }

    var top = win.scrollY || 0;
    var height = win.innerHeight || 1;
    return Math.min(1, Math.max(0, (top + height) / currentScrollHeight()));
  }

  function applyPercentInChapter(percent) {
    var win = contentWin();

    if (!win) {
      return;
    }

    var value = Math.min(1, Math.max(0, percent || 0));

    if (isPaged()) {
      var count = measurePageCount();
      setPage(Math.round(value * count) - 1);
      return;
    }

    scrollTo((value * currentScrollHeight()) - (win.innerHeight || 0));
  }

  function setMode(mode, notify) {
    var next = mode === 'paged' ? 'paged' : 'scroll';

    if (next === state.mode) {
      updateModeUi();
      return;
    }

    var percent = capturePercentInChapter();

    state.mode = next;
    state.pendingPage = null;
    state.pageIndex = 0;

    /* 正文 CSS 是"按模式生成"的（分页模式那份不含分栏规则），切模式必须重建，
       否则从分页切到滚动会沿用旧 CSS -> 分栏不生效（用户反馈的正是这条路径）。 */
    applyContentCss();
    applyPagedCss();
    applyPercentInChapter(percent);
    updateModeUi();
    reportLocation();

    if (notify !== false) {
      post('reader.modeChanged', { mode: next });
    }
  }

  function onWheel(event) {
    if (!isPaged()) {
      return;
    }

    var delta = (event.deltaY || 0) + (event.deltaX || 0);

    if (!delta) {
      return;
    }

    event.preventDefault();
    pageOrChapter(delta > 0 ? 1 : -1);
  }

  var resizeTimer = null;

  function onViewportResize() {
    /* T6-2B：PDF 的"适应宽度 / 适应页面"跟着视口走，窗口一变就得重排（自绘引擎自己的路，
       下面那段 EPUB 分页逻辑一行都不碰）。 */
    if (isPdf()) {
      onPdfResize();
      return;
    }

    /* EPUB / TXT：先定"一行还是两行"。它改的是工具栏高度 → 分页可用高度，
       所以必须排在重排之前，否则页框会按旧高度算（页框与一页差一行）。 */
    var rowsBefore = epubToolbarWide;

    updateEpubToolbarRowsForWidth();

    var rowsChanged = epubToolbarWide !== rowsBefore;

    if (!isPaged()) {
      return;
    }

    /* 窗口一变：页框宽高、列宽列高全变（每行字数、每页行数跟着变），
       重新排一次并按章内比例回到原处。
       一行/两行切换只改高度、不改宽度：按比例落位会白跳一页（比例是旧页数算的），
       所以这种情况下留在原页号，由 applyPagedCss 自己落位。 */
    var percent = rowsChanged ? -1 : capturePercentInChapter();

    if (resizeTimer) {
      clearTimeout(resizeTimer);
    }

    resizeTimer = setTimeout(function () {
      resizeTimer = null;
      applyPagedCss();

      if (percent >= 0) {
        applyPercentInChapter(percent);
      }

      updateModeUi();
    }, 150);
  }

  function updateModeUi() {
    document.body.setAttribute('data-reader-mode', state.mode);

    var btnMode = document.getElementById('btnMode');
    var btnPrev = document.getElementById('btnPrev');
    var btnNext = document.getElementById('btnNext');

    if (btnMode) {
      btnMode.textContent = isPaged() ? '滚动' : '分页';
      btnMode.setAttribute('title', isPaged() ? '切换回滚动阅读 (Ctrl+M)' : '切换到分页阅读 (Ctrl+M)');
    }

    if (btnPrev) {
      btnPrev.textContent = isPaged() ? '上一页' : '上一章';
    }

    if (btnNext) {
      btnNext.textContent = isPaged() ? '下一页' : '下一章';
    }

    syncChapterMeta();
  }

  // ---------- 章节标题第二行：章节号 / 页码（可输入跳转） ----------

  /* 用户 2026-10-21 要求：这一行写成「第 12/14 章　第 1/4 页」，
     斜杠前面那个"当前第几"是输入框 —— 输完按 Enter 或点别处就跳过去。
     章号在滚动 / 分页模式都显示；页码只在分页模式显示（滚动模式没有"页"）。 */
  var META_WIDTH_ATTR = 'data-ets-width-for';

  function metaJumpInput(id) {
    var el = document.getElementById(id);

    return el && el.tagName === 'INPUT' ? el : null;
  }

  /* 输入框宽度 = "字体里最宽的那个数字" × 位数 + 左右边框/内边距 4px + 2px 余量。
     规则（用户 2026-10-21 追加）：
       · 最小按 3 位数给（一位数的 14 章书里也能顺手把 "123" 编完，不至于边打边被挤）；
       · 总数超过 3 位时按实际位数加宽（1000+ 章、10000+ 章的书数字要完整显示）；
       · 不按当前值算，否则 9 -> 10 时框会抖一下；
       · 每次跳章 / 翻页 / 切模式都会重算一次，宽度跟着"这一本书有多少章/多少页"的位数走。 */
  var digitCanvas = null;

  function measureMetaJumpWidth(input, digits) {
    var glyphs = Math.max(1, digits);
    var fontSize = 11;
    var fontFamily = 'sans-serif';

    try {
      var cs = getComputedStyle(input);
      fontSize = parseFloat(cs.fontSize) || 11;
      fontFamily = cs.fontFamily || fontFamily;
    } catch (e) { /* 老内核：退回 11px sans-serif */ }

    if (!digitCanvas && document.createElement) {
      digitCanvas = document.createElement('canvas');
    }

    var ctx = digitCanvas && digitCanvas.getContext ? digitCanvas.getContext('2d') : null;

    if (!ctx) {
      return Math.ceil(glyphs * fontSize * 0.62) + 2;
    }

    ctx.font = fontSize + 'px ' + fontFamily;

    var widest = 0;

    for (var d = 0; d <= 9; d++) {
      widest = Math.max(widest, ctx.measureText(String(d)).width);
    }

    /* +4：左右各 1px 边框 + 各 1px 内边距；再 +2 余量，保证数字不会被边框切到。 */
    return Math.ceil(widest * glyphs) + 6;
  }

  /* 框里的数字有没有被裁：canvas 量当前值的实际宽，和框内可用宽比
     （clientWidth 已扣掉左右边框，再扣左右各 1px 内边距）。读不出 canvas 时按"没裁"处理。 */
  function metaValueFits(input) {
    if (!input) { return false; }

    if (!digitCanvas && document.createElement) {
      digitCanvas = document.createElement('canvas');
    }

    var ctx = digitCanvas && digitCanvas.getContext ? digitCanvas.getContext('2d') : null;

    if (!ctx) { return true; }

    try {
      var cs = getComputedStyle(input);
      ctx.font = cs.fontSize + ' ' + cs.fontFamily;
      return ctx.measureText(String(input.value || '')).width <= input.clientWidth - 2;
    } catch (e) {
      return true;
    }
  }

  /* 一位数 / 两位数 / 三位数 / 四五位数都按同一套公式：至少 3 位，超过 3 位按实际位数。 */
  function metaJumpDigits(total) {
    return Math.max(3, String(Math.max(1, total || 1)).length);
  }

  function sizeMetaJump(input, total) {
    if (!input) { return; }

    var digits = metaJumpDigits(total);

    if (input.getAttribute(META_WIDTH_ATTR) === String(digits)) { return; }

    input.setAttribute(META_WIDTH_ATTR, String(digits));
    input.style.width = measureMetaJumpWidth(input, digits) + 'px';
  }

  function syncChapterMeta() {
    var totalChapters = state.manifest && state.manifest.spine ? state.manifest.spine.length : 0;
    var totalPages = isPaged() ? measurePageCount() : 0;
    var chapterInput = metaJumpInput('chapterJumpInput');
    var pageInput = metaJumpInput('pageJumpInput');
    var chapterTotal = document.getElementById('chapterTotal');
    var pageTotal = document.getElementById('pageTotal');
    var pageSeg = document.getElementById('chapterMetaPage');

    if (chapterTotal) { chapterTotal.textContent = String(totalChapters); }
    if (pageTotal) { pageTotal.textContent = String(totalPages); }
    if (pageSeg) { pageSeg.hidden = !isPaged(); }

    /* 正在打字的那一格不覆盖值，否则输到一半被当前页号冲掉 */
    if (chapterInput && document.activeElement !== chapterInput) {
      chapterInput.value = String(totalChapters === 0 ? 0 : state.index + 1);
    }

    if (pageInput && document.activeElement !== pageInput) {
      pageInput.value = String(isPaged() ? currentPageIndex() + 1 : 0);
    }

    sizeMetaJump(chapterInput, totalChapters);
    sizeMetaJump(pageInput, totalPages);
  }

  /* 提交一格输入：合法就跳，不合法就退回当前值。
     超范围夹到 1..总数（和 PDF 页码框的手输规则一致：0 -> 1、999 -> 最后一页）。 */
  function commitMetaJump(kind) {
    var isPage = kind === 'page';
    var input = metaJumpInput(isPage ? 'pageJumpInput' : 'chapterJumpInput');

    if (!input) { return; }

    var want = parseInt(String(input.value || '').replace(/[^\d]/g, ''), 10);
    var total = isPage
      ? (isPaged() ? measurePageCount() : 0)
      : (state.manifest && state.manifest.spine ? state.manifest.spine.length : 0);

    if (!isFinite(want) || want < 1 || total <= 0) {
      syncChapterMeta();
      return;
    }

    want = Math.min(want, total);

    if (isPage) {
      setPage(want - 1);
      reportLocation();
      return;
    }

    var index = want - 1;

    if (index === state.index) {
      /* 输的是当前章：跳这一章第 1 页（分页=第 1 页，滚动=章首） */
      chapterEdge(false);
      return;
    }

    /* 换章必须经由宿主物化（见 requestChapter 的注释）。
       pendingPage = 0 保证分页模式下落在新章第 1 页；清掉 pendingLocator，免得又按上次位置回位。 */
    state.pendingPage = 0;
    state.pendingLocator = null;
    state.pendingFingerprint = null;
    requestChapter(index);
  }

  /* 一次性绑定：Enter / Esc / 失焦。回车直接提交（不依赖"先 shift 焦点再 blur"，
     冒烟在非激活窗口里 blur() 可能什么都不触发），提交完才 blur；
     紧跟其后的那次 blur 用标记跳过，避免同一次回车跳两遍。
     Esc 走"取消标记"：syncChapterMeta 不会覆盖正在聚焦的框，所以必须先标取消再 blur，
     blur 时恢复当前值且**不提交**（否则按 Esc 会变成"跳回本章第 1 页"）。 */
  function wireMetaJump() {
    ['chapterJumpInput', 'pageJumpInput'].forEach(function (id) {
      var input = metaJumpInput(id);

      if (!input || input.__etsWired) { return; }

      input.__etsWired = true;

      input.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') {
          event.preventDefault();
          input.__etsSkipBlur = true;
          commitMetaJump(id === 'pageJumpInput' ? 'page' : 'chapter');
          /* 标记只活到本次事件循环末尾：blur 立刻触发就跳过，
             没触发（窗口没焦点）也不会影响下一次真正的失焦提交。 */
          setTimeout(function () { input.__etsSkipBlur = false; }, 0);
          input.blur();
        } else if (event.key === 'Escape') {
          event.preventDefault();
          input.__etsCancelled = true;
          input.blur();
        }
      });

      input.addEventListener('blur', function () {
        if (input.__etsSkipBlur) {
          input.__etsSkipBlur = false;
          syncChapterMeta();
          return;
        }

        if (input.__etsCancelled) {
          input.__etsCancelled = false;
          syncChapterMeta();
          return;
        }

        commitMetaJump(id === 'pageJumpInput' ? 'page' : 'chapter');
      });

      /* 点进来就全选：直接打数字覆盖，不用先删掉原来的 */
      input.addEventListener('focus', function () { input.select(); });
    });
  }

  /* 当前这一页的行数与"最深行底"（都相对页框顶部，单位 px）。
     用来证明：一页的行数随窗口高度变、且页底不会出现半行被切。 */
  function pageLineStats() {
    var doc = contentDoc();
    var flow = flowElement();
    var metrics = pageMetrics();

    if (!doc || !doc.body || !flow) {
      return { lines: 0, deepest: 0, deepestX: 0, deepestTop: 0, deepestText: '', clientHeight: 0, scrollHeight: 0 };
    }

    var offset = state.lastOffsetX || 0;
    var flowRect = flow.getBoundingClientRect();
    var tops = {};
    var deepest = 0;
    var deepestX = 0;
    var deepestTop = 0;
    var deepestText = '';
    /* 每一条 rect 的高度与底，配合"行高过滤"用：
       长文本节点的 rect 可能是跨列的大合并矩形（实测高 5336px），超高图片/表格也是同一类干扰，
       都不能拿来判断"文字行有没有越界"：先取中位行高，再把 > 中位*2.5 的 rect 当"块"排除。 */
    var heights = [];
    var bottoms = [];
    var range = doc.createRange();
    var walker = doc.createTreeWalker(flow, NodeFilter.SHOW_TEXT, null);
    var node;

    while ((node = walker.nextNode())) {
      if (!node.nodeValue || !node.nodeValue.trim()) {
        continue;
      }

      try {
        range.selectNodeContents(node);
      } catch (ignored) {
        continue;
      }

      var rects = range.getClientRects();

      for (var i = 0; i < rects.length; i++) {
        var rect = rects[i];

        if (rect.height <= 0.5) {
          continue;
        }

        // 列坐标：流被平移了，flowRect 也一起动，所以差不含位移
        var x = rect.left - flowRect.left;

        if (x >= offset - 1 && x < offset + metrics.width) {
          tops[Math.round(rect.top - flowRect.top)] = 1;
          heights.push(rect.height);
          bottoms.push(rect.bottom - flowRect.top);

          var bottom = rect.bottom - flowRect.top;

          if (bottom > deepest) {
            deepest = bottom;
            deepestX = Math.round(x);
            deepestTop = Math.round(rect.top - flowRect.top);
            deepestText = String(node.nodeValue || '').replace(/\s+/g, ' ').trim().slice(0, 24);
          }
        }
      }
    }

    var median = 0;

    if (heights.length > 0) {
      var sorted = heights.slice().sort(function (a, b) { return a - b; });
      median = sorted[Math.floor(sorted.length / 2)];
    }

    var lineLimit = median > 0 ? median * 2.5 : 1e9;
    var lineBottom = 0;
    var oversize = 0;

    for (var b = 0; b < bottoms.length; b++) {
      if (heights[b] <= lineLimit) {
        if (bottoms[b] > lineBottom) { lineBottom = bottoms[b]; }
      } else {
        oversize++;
      }
    }

    return {
      lines: Object.keys(tops).length,
      deepest: Math.round(deepest),
      deepestX: deepestX,
      deepestTop: deepestTop,
      deepestText: deepestText,
      /* 真正的"文字行最深底"（已排除超高块与合并矩形） */
      lineBottom: Math.round(lineBottom),
      medianLineHeight: Math.round(median),
      oversizeRects: oversize,
      clientHeight: Math.round(flow.clientHeight || 0),
      scrollHeight: Math.round(flow.scrollHeight || 0)
    };
  }

  // ---------- 键盘 ----------

  /* T4-6 快捷键总表（外壳与 iframe 内各绑一份，所以"焦点在正文里"也生效）。
     Win11 的系统快捷键走 Win 键（Win+左右、Win+Tab 等），本表一个都不碰；
     Ctrl+F / Ctrl+= / Ctrl+- / Ctrl+0 只在阅读器窗口内生效，不外扩。

     T6-2B：PDF 模式先走 onPdfKeyDown（翻页 / 缩放 / Ctrl+F / Ctrl+M）。
     自绘之后没有内核插件了，PDF 的键盘不再"放给内核"，而是并入这同一张表——
     它吃掉的键必须是设计文档列过的那几个，其余（Esc / F11 / T 开目录）照旧落到下面。 */
  function onKeyDown(event) {
    if (isPdf() && onPdfKeyDown(event)) {
      return;
    }

    var key = event.key || '';

    /* 带 Ctrl 的组合：必须在"带修饰键就返回"之前判断 */
    if (event.ctrlKey && !event.altKey && !event.metaKey) {
      /* T6-5：Ctrl+B 在当前阅读位置加书签（EPUB 记章内位置，PDF/漫画记页码）。 */
      if (key === 'b' || key === 'B') {
        event.preventDefault();
        addBookmark();
        return;
      }

      /* T6-5：Ctrl+Shift+N = 给当前选区打开笔记面板（弹条被输入法/缩放挡住时的备用入口）。 */
      if (event.shiftKey && (key === 'n' || key === 'N')) {
        event.preventDefault();
        openNotePanelDraft();
        return;
      }

      if (key === 'm' || key === 'M') {
        event.preventDefault();
        setMode(isPaged() ? 'scroll' : 'paged');
        return;
      }

      if (key === 'ArrowLeft' || key === 'ArrowRight') {
        event.preventDefault();
        gotoRelative(key === 'ArrowLeft' ? -1 : 1);
        return;
      }

      if (key === 'g' || key === 'G') {
        event.preventDefault();
        toggleGotoPanel();
        return;
      }

      if (key === 'f' || key === 'F') {
        event.preventDefault();
        openFindBar();
        return;
      }

      if (key === '=' || key === '+') {
        event.preventDefault();
        changeFontSize(1);
        return;
      }

      if (key === '-' || key === '_') {
        event.preventDefault();
        changeFontSize(-1);
        return;
      }

      if (key === '0') {
        event.preventDefault();
        resetFontSize();
        return;
      }

      return;
    }

    if (event.altKey || event.metaKey) {
      return;
    }

    /* 焦点在输入框里：只认 Enter / Esc，别把打字吃掉 */
    if (isTypingTarget(event.target)) {
      if (key === 'Escape') {
        event.preventDefault();
        escapeStep();
        return;
      }

      if (key === 'Enter') {
        event.preventDefault();

        if (event.target.id === 'findInput') {
          var wanted = event.target.value || '';

          if (!state.findText || state.findText !== wanted) {
            runFind(wanted);
          } else {
            findStep(!event.shiftKey);
          }
        } else if (event.target.id === 'gotoRange') {
          toggleGotoPanel(false);
          gotoBookPercent(Number(event.target.value));
        } else if (event.target.id === 'frFind') {
          frHighlight();
          frStep(!event.shiftKey);
        } else if (event.target.id === 'frReplace') {
          frReplaceOne();
        } else {
          findStep(!event.shiftKey);
        }
      }

      return;
    }

    if (key === 'PageUp' || key === 'ArrowLeft') {
      event.preventDefault();
      pageOrChapter(-1);
      return;
    }

    if (key === 'PageDown' || key === 'ArrowRight') {
      event.preventDefault();
      pageOrChapter(1);
      return;
    }

    if (key === ' ' || key === 'Spacebar') {
      event.preventDefault();
      pageOrChapter(event.shiftKey ? -1 : 1);
      return;
    }

    if (key === 'Home' || key === 'End') {
      event.preventDefault();
      chapterEdge(key === 'End');
      return;
    }

    if (key === 'F11') {
      event.preventDefault();
      toggleFullscreen();
      return;
    }

    if (key === 'Escape') {
      event.preventDefault();
      escapeStep();
      return;
    }

    if (key === 't' || key === 'T') {
      event.preventDefault();
      toggleTocPanel();
    }
  }

  // ---------- T4-6 交互：状态条 / 全屏 / 跳转 / 章内查找 ----------

  var FONT_SIZE_MIN = 12;
  var FONT_SIZE_MAX = 36;
  var FONT_SIZE_DEFAULT = 17;

  /* 焦点在输入框/下拉里时，单字母快捷键要让路（否则在查找框里打 t 会开目录） */
  function isTypingTarget(target) {
    if (!target || !target.tagName) {
      return false;
    }

    var tag = String(target.tagName).toLowerCase();

    return tag === 'input' || tag === 'textarea' || tag === 'select';
  }

  /* 全书百分比：章序号 + 章内比例。状态条与 Ctrl+G 都用它。 */
  function wholeBookPercent() {
    var total = state.manifest && state.manifest.spine ? state.manifest.spine.length : 0;

    if (total <= 0) {
      return 0;
    }

    return clamp01((state.index + clamp01(capturePercentInChapter())) / total) * 100;
  }

  function updateStatusBar() {
    var total = state.manifest && state.manifest.spine ? state.manifest.spine.length : 0;
    var chapter = document.getElementById('statusChapter');
    var percent = document.getElementById('statusPercent');
    var prev = document.getElementById('statusPrev');
    var next = document.getElementById('statusNext');

    if (chapter) {
      chapter.textContent = '第 ' + (total === 0 ? 0 : state.index + 1) + ' / ' + total + ' 章';
    }

    if (percent) {
      percent.textContent = '全书 ' + Math.round(wholeBookPercent()) + '%';
    }

    if (prev) {
      prev.disabled = state.index <= 0;
    }

    if (next) {
      next.disabled = total === 0 || state.index >= total - 1;
    }
  }

  /* 全屏（F11）：本页只负责"隐藏 UI + 鼠标到顶部浮出"，窗口本身由宿主机切成无边最大化。 */
  function toggleFullscreen(on) {
    var next = typeof on === 'boolean' ? on : !state.fullscreen;

    state.fullscreen = next;
    document.body.classList.toggle('ets-fullscreen', next);

    if (!next) {
      document.body.classList.remove('ets-toolbar-peek');
    }

    updateStatusBar();
    post('reader.fullscreenChanged', { on: next });
    return next;
  }

  function onShellMouseMove(event) {
    if (!state.fullscreen) {
      return;
    }

    document.body.classList.toggle('ets-toolbar-peek', (event.clientY || 0) <= 48);
  }

  function changeFontSize(delta) {
    var current = Number((state.typography || {}).fontSize) || FONT_SIZE_DEFAULT;
    var next = Math.max(FONT_SIZE_MIN, Math.min(FONT_SIZE_MAX, Math.round(current + delta)));

    applyTypography({ fontSize: next });
    return next;
  }

  function resetFontSize() {
    applyTypography({ fontSize: FONT_SIZE_DEFAULT });
    return FONT_SIZE_DEFAULT;
  }

  /* Home / End：章首 / 章尾 */
  function chapterEdge(toEnd) {
    if (isPaged()) {
      setPage(toEnd ? Math.max(0, measurePageCount() - 1) : 0);
      updateModeUi();
    } else {
      scrollTo(toEnd ? maxScrollTop() : 0);
    }

    reportLocation();
  }

  /* Ctrl+G：跳到全书百分比。跨章要先请宿主物化，再按"章内比例"落位。 */
  function gotoBookPercent(percent) {
    var total = state.manifest && state.manifest.spine ? state.manifest.spine.length : 0;

    if (total <= 0) {
      return false;
    }

    var want = Number(percent);
    var exact = (isFinite(want) ? Math.max(0, Math.min(100, want)) : 0) / 100 * total;
    var chapter = Math.max(0, Math.min(total - 1, Math.floor(exact)));
    var inner = clamp01(exact - chapter);

    state.pendingPage = null;

    if (chapter === state.index) {
      applyPercentInChapter(inner);
      reportLocation();
      return true;
    }

    state.pendingLocator = { chapterIndex: chapter, percentInChapter: inner };
    state.pendingFingerprint = null;
    requestChapter(chapter);
    return true;
  }

  function toggleGotoPanel(open) {
    var panel = document.getElementById('gotoPanel');

    if (!panel) {
      return false;
    }

    var next = typeof open === 'boolean' ? open : panel.hidden;

    panel.hidden = !next;

    if (next) {
      var range = document.getElementById('gotoRange');
      var value = document.getElementById('gotoValue');
      var percent = Math.round(wholeBookPercent());

      if (range) {
        range.value = String(percent);
      }

      if (value) {
        value.textContent = String(percent);
      }

      closeFindBar();
      closeTocPanel();
    }

    return next;
  }

  // ---------- 章内查找（Ctrl+F；跨书搜索是阶段 6 的事） ----------

  function openFindBar() {
    /* T6-3：漫画没有文本层，Ctrl+F 不该打开一个永远 0 结果的查找框——明确告诉用户一声。 */
    if (isComic()) {
      notice('漫画没有可搜索的文字。');
      return false;
    }

    var bar = document.getElementById('findBar');

    if (!bar) {
      return false;
    }

    bar.hidden = false;
    toggleGotoPanel(false);

    var input = document.getElementById('findInput');

    if (input) {
      input.focus();
      input.select();
    }

    return true;
  }

  function closeFindBar() {
    var bar = document.getElementById('findBar');

    if (bar) {
      bar.hidden = true;
    }

    /* T6-2B：PDF 的命中在文字层上，清法和 EPUB 的 <mark> 不一样，走自己的分支。 */
    if (isPdf()) {
      closePdfFind();
      return;
    }

    clearFind();

    /* 计数也要清掉：不然关掉面板后它还挂着上一章的 "1 / 89"，看着像"还有命中"。 */
    updateFindCount();
  }

  /* 把本章里所有匹配包进 <mark>，返回匹配数。 */
  /* 在（可选）正则/大小写设置下生成匹配器；返回 null 表示正则不合法。 */
  function buildFindMatcher(text, useRegex, matchCase) {
    if (useRegex) {
      var re;

      try {
        re = new RegExp(String(text), 'g' + (matchCase ? '' : 'i'));
      } catch (error) {
        return null;
      }

      return function (value) {
        var hits = [];
        var matched;

        re.lastIndex = 0;

        while ((matched = re.exec(value)) !== null) {
          if (matched[0].length === 0) {
            re.lastIndex++;
            continue;
          }

          hits.push({ index: matched.index, length: matched[0].length });

          if (re.lastIndex <= matched.index) {
            re.lastIndex = matched.index + 1;
          }
        }

        return hits;
      };
    }

    var needle = String(text);

    return function (value) {
      var hay = matchCase ? value : value.toLowerCase();
      var want = matchCase ? needle : needle.toLowerCase();
      var hits = [];
      var index = 0;

      while (want.length > 0) {
        var at = hay.indexOf(want, index);

        if (at < 0) {
          break;
        }

        hits.push({ index: at, length: want.length });
        index = at + want.length;
      }

      return hits;
    };
  }

  function highlightMatches(text, useRegex, matchCase) {
    var doc = contentDoc();

    clearFind();

    if (!doc || !doc.body || !text) {
      return 0;
    }

    var matcher = buildFindMatcher(text, !!useRegex, !!matchCase);

    if (!matcher) {
      state.findText = String(text);
      updateFindCount();
      return -1;
    }

    var walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT, null);
    var nodes = [];

    while (walker.nextNode()) {
      nodes.push(walker.currentNode);
    }
    var walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT, null);
    var nodes = [];

    while (walker.nextNode()) {
      nodes.push(walker.currentNode);
    }

    var count = 0;

    nodes.forEach(function (node) {
      var value = node.nodeValue || '';
      var hits = matcher(value);

      if (!hits.length) {
        return;
      }

      var fragment = doc.createDocumentFragment();
      var cursor = 0;

      hits.forEach(function (hit) {
        if (hit.index < cursor) {
          return;
        }

        fragment.appendChild(doc.createTextNode(value.slice(cursor, hit.index)));

        var mark = doc.createElement('mark');

        mark.className = 'ets-find';
        mark.textContent = value.slice(hit.index, hit.index + hit.length);
        fragment.appendChild(mark);

        cursor = hit.index + hit.length;
        count++;
      });

      fragment.appendChild(doc.createTextNode(value.slice(cursor)));
      node.parentNode.replaceChild(fragment, node);
    });

    state.findMatches = doc.querySelectorAll('mark.ets-find');
    state.findIndex = -1;
    state.findText = String(text);
    updateFindCount();

    return count;
  }

  function clearFind() {
    var doc = contentDoc();

    state.findMatches = null;
    state.findIndex = -1;
    state.findText = '';

    if (!doc || !doc.body) {
      return;
    }

    var marks = doc.querySelectorAll('mark.ets-find');

    for (var i = marks.length - 1; i >= 0; i--) {
      var parent = marks[i].parentNode;

      if (parent) {
        parent.replaceChild(doc.createTextNode(marks[i].textContent || ''), marks[i]);
      }
    }

    /* 相邻的纯文本节点合并一下，免得反复查找把 DOM 切得越来越碎 */
    if (doc.body.normalize) {
      doc.body.normalize();
    }
  }

  function currentMatchCount() {
    return state.findMatches ? state.findMatches.length : 0;
  }

  function updateFindCount() {
    /* T6-2B：PDF 的命中来自宿主（reader.pdfFindResults），计数口径是"第 i / n 个"。 */
    if (isPdf()) {
      updatePdfFindCount();
      return;
    }

    var total = currentMatchCount();
    var text = total === 0
      ? (state.findText ? '本章无匹配' : '')
      : ('本章 ' + (state.findIndex < 0 ? 0 : state.findIndex + 1) + ' / ' + total);

    /* 匹配范围永远是全书（用户 2026-10-12 口径）：把全书总数也显示出来。 */
    if (typeof state.bookTotal === 'number' && state.bookCountText === String(state.findText || '')) {
      var bookText = state.bookTotal > 0 ? ('全书 ' + state.bookTotal + ' 处') : '全书无匹配';

      text = text ? (text + '  ' + bookText) : bookText;
    }

    var plain = document.getElementById('findCount');

    if (plain) {
      plain.textContent = text;
    }

    var frLabel = document.getElementById('frCount');

    if (frLabel) {
      frLabel.textContent = text;
    }
    layoutFindReplaceBar();
  }

  /* 下一个/上一个匹配：分页模式翻到那一页，滚动模式滚到它。 */
  function findStep(forward) {
    /* T6-2B：PDF 的命中列表在宿主侧算好，走的也是"跨页 + 文字层黄底"那条路。 */
    if (isPdf()) {
      return pdfFindStep(forward);
    }

    var marks = state.findMatches;
    var total = marks ? marks.length : 0;

    if (total === 0) {
      updateFindCount();
      return -1;
    }

    state.findIndex = ((state.findIndex + (forward ? 1 : -1)) + total) % total;

    for (var i = 0; i < total; i++) {
      marks[i].classList.remove('ets-find-current');
    }

    var mark = marks[state.findIndex];

    mark.classList.add('ets-find-current');

    if (isPaged()) {
      setPage(pageOfElement(mark));
      updateModeUi();
    } else {
      var win = contentWin();
      var current = win ? (win.scrollY || 0) : 0;

      scrollTo(current + mark.getBoundingClientRect().top - 60);
    }

    updateFindCount();
    return state.findIndex;
  }

  function runFind(text) {
    /* T6-2B：PDF 关键字检索是宿主的事（pdfium 文本层），这里只把查询发出去。 */
    if (isPdf()) {
      return runPdfFind(text);
    }

    highlightMatches(text);
    findStep(true);
    return currentMatchCount();
  }

  /* ---------- T5-3 查找替换（阅读器工具栏入口） ---------- */

  function frBar() { return document.getElementById('frBar'); }
  function frValue(id) { var el = document.getElementById(id); return el ? el.value : ''; }
  function frChecked(id) { var el = document.getElementById(id); return !!(el && el.checked); }
  function frScope() { return frValue('frScope') === 'book' ? 'book' : 'chapter'; }

  function openFindReplaceBar() {
    var bar = frBar();

    if (!bar) {
      return false;
    }

    bar.hidden = false;
    closeFindBar();
    toggleGotoPanel(false);

    var input = document.getElementById('frFind');

    if (input) {
      input.focus();
      input.select();
    }

    /* 【用户 2026-10-12 反馈】重新打开面板时，必须按"当前这一章"重新检索一次：否则面板上的
       计数还是旧文档留下的数字，内部命中列表其实是空的  点下一个只会报"本章没有匹配"，
       而眼前这章明明有。 */
    if (frValue('frFind')) {
      frHighlight();
    }

    window.addEventListener('resize', layoutFindReplaceBar);
    layoutFindReplaceBar();

    return true;
  }

  function closeFindReplaceBar() {
    var bar = frBar();

    if (bar) {
      bar.hidden = true;
    }

    clearFind();

    /* 计数也要清掉：不然关掉面板后它还挂着上一章的 "1 / 89"，看着像"还有命中"。 */
    updateFindCount();
  }

  var frCountTimer = null;

  /* 问宿主"全书一共几处"（防抖 250ms）：匹配范围永远是全书，高亮只画当前章。 */
  /* 单行真正需要多宽：按实际内容量出来（不估数字）。量之前先临时按"单行"排一次。 */
  function measureFindReplaceRowWidth() {
    var bar = frBar();

    if (!bar) {
      return 0;
    }

    var wasNarrow = bar.classList.contains('fr-narrow');

    if (wasNarrow) {
      bar.classList.remove('fr-narrow');
    }

    var groups = bar.querySelectorAll('.frPair, .frGroup');
    var style = getComputedStyle(bar);
    var gap = parseFloat(style.columnGap || style.gap || '0') || 0;
    var width = (parseFloat(style.paddingLeft || '0') || 0) + (parseFloat(style.paddingRight || '0') || 0);

    for (var i = 0; i < groups.length; i++) {
      width += groups[i].getBoundingClientRect().width;
    }

    width += gap * (groups.length + 1);

    /* 面板靠右定位（CSS 的 right）：单行放得下要求 视口宽 >= 所需宽 + 该偏移。 */
    var right = parseFloat(style.right || '0') || 0;

    if (wasNarrow) {
      bar.classList.add('fr-narrow');
    }

    return width + right;
  }

  /* 用"量出来的"所需宽度决定一条还是分两行（开面板 / 改窗口 / 计数文本变长都要重算）。 */
  function layoutFindReplaceBar() {
    var bar = frBar();

    if (!bar || bar.hidden) {
      return false;
    }

    var required = measureFindReplaceRowWidth();
    var narrow = window.innerWidth < (required + 8);

    bar.classList.toggle('fr-narrow', narrow);
    return narrow;
  }

  var frRowWidth = 0;

  /* 外部（探针/冒烟）读用：当前是否分两行、以及量出来的单行所需宽度。 */
  function findReplaceBarState() {
    var bar = frBar();

    return {
      narrow: !!(bar && bar.classList.contains('fr-narrow')),
      required: measureFindReplaceRowWidth(),
      innerWidth: window.innerWidth,
      open: !!(bar && !bar.hidden)
    };
  }

  function requestBookCount() {
    var text = frValue('frFind');

    if (!text) {
      state.bookTotal = null;
      state.bookCountText = null;
      updateFindCount();
      return;
    }

    state.bookCountText = text;

    if (frCountTimer) {
      clearTimeout(frCountTimer);
    }

    frCountTimer = setTimeout(function () {
      frCountTimer = null;
      post('reader.findReplace.count', {
        find: text,
        useRegex: frChecked('frRegex'),
        matchCase: frChecked('frCase')
      });
    }, 250);
  }

  function frHighlight() {
    var text = frValue('frFind');

    if (!text) {
      clearFind();
      updateFindCount();
      return 0;
    }

    var count = highlightMatches(text, frChecked('frRegex'), frChecked('frCase'));

    if (count < 0) {
      notice('正则表达式不合法，已停止查找。');
    }

    requestBookCount();

    return count;
  }

  /* 当前"该替换的那一处"：优先用户已经选中的当前处；没选过就取离当前视图最近的那一处。
     不这么做的话，点【替换】会盲目替换本章第一处，而它可能在别的页上 
     用户会觉得"替换的不是我眼前这一处"。 */
  function nearestMatchIndex() {
    var marks = state.findMatches;

    if (!marks || marks.length === 0) {
      return -1;
    }

    if (state.findIndex >= 0 && state.findIndex < marks.length) {
      return state.findIndex;
    }

    if (isPaged()) {
      var page = currentPageIndex();

      for (var i = 0; i < marks.length; i++) {
        if (pageOfElement(marks[i]) >= page) {
          return i;
        }
      }

      return marks.length - 1;
    }

    var win = contentWin();
    var top = win ? (win.scrollY || 0) : 0;

    for (var j = 0; j < marks.length; j++) {
      if (marks[j].getBoundingClientRect().top >= -40) {
        return j;
      }
    }

    return marks.length - 1;
  }

  /* 只把某一处标成"当前处"：改样式 + 更新计数，不滚动、不翻页。 */
  function markCurrent(index) {
    var marks = state.findMatches;

    if (!marks || marks.length === 0) {
      return -1;
    }

    state.findIndex = Math.max(0, Math.min(marks.length - 1, index));

    for (var i = 0; i < marks.length; i++) {
      marks[i].classList.remove('ets-find-current');
    }

    marks[state.findIndex].classList.add('ets-find-current');
    updateFindCount();
    return state.findIndex;
  }

  function frStep(forward) {
    var text = frValue('frFind');
    var total = currentMatchCount();

    if (!text) {
      return -1;
    }

    /* 面板刚打开、或这一章刚被替换重载过：先按当前章重检一次，别动不动报"本章没有匹配"。 */
    if (total === 0) {
      total = frHighlight();

      if (total < 0) {
        return -1;
      }
    }

    /* 用户 2026-10-12 口径：**匹配范围永远是全书**（所有章节）。本章还有命中就先在本章里走；
       走到头（或本章没有命中）就请宿主"全书找下一章/上一章"，它会跳章并高亮该章第一处。 */
    if (total > 0) {
      var atEdge = forward ? state.findIndex === total - 1 : state.findIndex === 0;

      if (!atEdge) {
        return findStep(forward);
      }
    }

    post('reader.findReplace.locate', {
      find: text,
      useRegex: frChecked('frRegex'),
      matchCase: frChecked('frCase'),
      fromIndex: state.index,
      forward: !!forward
    });
    return -1;
  }
  function frPreview() {
    var text = frValue('frFind');

    if (!text) {
      notice('请输入查找内容。');
      return false;
    }

    if (frChecked('frRegex')) {
      try {
        new RegExp(text);
      } catch (error) {
        notice('正则表达式不合法：' + error.message);
        return false;
      }
    }

    post('reader.findReplace.preview', {
      find: text,
      replace: frValue('frReplace'),
      useRegex: frChecked('frRegex'),
      matchCase: frChecked('frCase'),
      scope: frScope(),
      chapterIndex: state.index
    });

    /* 全部替换之后宿主会重载这一章（iframe 整个换掉，旧的 <mark> 自然全没了）。
       先把参数记下：新文档加载完（onFrameLoad -> applyPendingFind）自动重新检索并高亮剩下的命中，
       用户不用再点一次"查找"。 */
    state.pendingFind = {
      text: text,
      useRegex: frChecked('frRegex'),
      matchCase: frChecked('frCase')
    };
    return true;
  }

  function frReplaceOne() {
    var text = frValue('frFind');

    if (!text) {
      notice('请输入查找内容。');
      return false;
    }

    if (state.findText !== text || !state.findMatches) {
      frHighlight();
    }

    if (!state.findMatches || state.findMatches.length === 0) {
      notice('本章没有可替换的匹配。');
      return false;
    }

    /* 没有"当前处"时别盲目用第一处：取离当前页/当前位置最近的那一处，只标出来不跳走，
       接着替换的就是"你眼前这一处"。 */
    if (state.findIndex < 0) {
      markCurrent(nearestMatchIndex());
    }

    if (state.findIndex < 0) {
      notice('本章没有可替换的匹配。');
      return false;
    }

    post('reader.findReplace.one', {
      find: text,
      replace: frValue('frReplace'),
      useRegex: frChecked('frRegex'),
      matchCase: frChecked('frCase'),
      scope: 'chapter',
      chapterIndex: state.index,
      ordinal: state.findIndex
    });

    /* 单处替换同样会重载这一章。index 记的是"刚被替换掉的那一处"：
       新文档加载完后自动重新检索当页剩下的命中并重新标好，当前处定在"下一处"，
       而且不滚动/不翻页（用户留在原处就能继续替换）。 */
    state.pendingFind = {
      text: text,
      useRegex: frChecked('frRegex'),
      matchCase: frChecked('frCase'),
      index: state.findIndex
    };
    return true;
  }

  function applyPendingFind() {
    var pending = state.pendingFind;

    if (pending) {
      state.pendingFind = null;

      var count = highlightMatches(pending.text, pending.useRegex, pending.matchCase);

      if (count > 0) {
        if (typeof pending.index === 'number' && pending.index >= 0) {
          /* 单处替换后：重新高亮剩下的命中，并把视图真正定到"被替换掉那一处的下一处"。
             分页模式必须真的翻到那一页（只标不翻，用户看到的还是上一页，像是"替换跑到下一页去了"）。 */
          state.findIndex = Math.min(pending.index, count - 1) - 1;
          findStep(true);
          return;
        }

        findStep(true);
      } else if (count === 0) {
        notice('这一章没有匹配。');
      }

      return;
    }

    /* 【用户 2026-10-12 反馈】换到别的章之后，面板里明明还写着查找词，却一点高亮都没有。
       只要面板开着且有查找词，就应该按"新这一章"重新检索并把命中标出来。 */
    var bar = frBar();

    if (bar && !bar.hidden && frValue('frFind')) {
      var found = frHighlight();

      if (found > 0 && state.findIndex < 0) {
        findStep(true);
      }
    }
  }

  /* Esc：按"面板 -> 全屏"的顺序收 */
  function escapeStep() {
    var find = document.getElementById('findBar');
    var fr = document.getElementById('frBar');
    var gotoTarget = document.getElementById('gotoPanel');
    var toc = document.getElementById('tocPanel');
    var notes = document.getElementById('notesPanel');
    var card = document.getElementById('noteCard');

    if (card && !card.hidden) {
      closeNoteCard();
      return 'noteCard';
    }

    if (notes && !notes.hidden) {
      closeAnnotationPanel();
      return 'notes';
    }

    hideSelectionBar();

    if (fr && !fr.hidden) {
      closeFindReplaceBar();
      return 'findReplace';
    }

    if (find && !find.hidden) {
      closeFindBar();
      return 'find';
    }

    if (gotoTarget && !gotoTarget.hidden) {
      toggleGotoPanel(false);
      return 'goto';
    }

    if (toc && !toc.hidden) {
      closeTocPanel();
      return 'toc';
    }

    if (state.fullscreen) {
      toggleFullscreen(false);
      return 'fullscreen';
    }

    return '';
  }

  /* ==================== T6-5：书签与笔记 ====================

     锚点体系（设计文档 §7.7 / 计划书 T6-5）：
       EPUB/TXT：anchor = { startPath, startOffset, endPath, endOffset, percentInChapter }
                 路径是"从 body 往下每层的 childIndex"，能表达跨元素选区
                 （window.getSelection() 的 Range 常常跨多个文本节点，没有全局字符编号可用）；
                 引文（quote）同时存下来，路径失效时用它在本章里重新找。
       PDF     ：anchor = { pageCharStart, pageCharEnd }，下标与文字层 .pdfChar 的 data-i 一致
                 （与文字层、搜索命中共用同一套字符下标，即设计文档 §6.5.2 的"页码 + 页内字符偏移"）。
       漫画    ：没有文字层，笔记入口置灰；书签按页码照常可用。

     位置失效的降级顺序（与进度恢复同一条纪律）：路径 → 引文搜索 → 章内比例 → 提示。
     任何一步失败都只提示，绝不让打开章节失败。 */

  function selectionContext() {
    if (isPdf()) {
      return {
        sel: window.getSelection(),
        doc: document,
        root: document.getElementById('pdfPages') || document.body
      };
    }

    var doc = frame && frame.contentDocument;
    var win = frame && frame.contentWindow;

    if (!doc || !win || !doc.body) {
      return null;
    }

    return { sel: win.getSelection(), doc: doc, root: doc.body };
  }

  /* 从 node 往上走到 root，记录每层的 childIndex，得到 "body/0/p/2" 这样的路径。
     刻意不用 CSS 选择器：书里的 class/id 不保证唯一，childIndex 才是 DOM 的坐标。 */
  function pathTo(node, root) {
    if (!node || !root) {
      return null;
    }

    var path = [];
    var cur = node;

    while (cur && cur !== root) {
      var parent = cur.parentNode;

      if (!parent) {
        return null;
      }

      path.unshift(Array.prototype.indexOf.call(parent.childNodes, cur));
      cur = parent;
    }

    if (cur !== root) {
      return null;
    }

    return 'body' + (path.length ? '/' + path.join('/') : '');
  }

  function nodeFromPath(doc, pathText) {
    if (!doc || !pathText) {
      return null;
    }

    var parts = String(pathText).split('/');

    if (parts[0] !== 'body') {
      return null;
    }

    var cur = doc.body;

    for (var i = 1; i < parts.length; i++) {
      var index = parseInt(parts[i], 10);

      if (!cur || !cur.childNodes || index < 0 || index >= cur.childNodes.length) {
        return null;
      }

      cur = cur.childNodes[index];
    }

    return cur;
  }

  /* PDF：选区覆盖的字符下标区间（只统计当前页的字符框，设计文档明确不做跨页 Range）。 */
  function capturePdfCharRange(range) {
    if (!state.pdf || !range) {
      return null;
    }

    var obj = pdfPageObj(state.pdf.page);
    var pageEl = obj && obj.element;

    if (!pageEl) {
      return null;
    }

    var spans = pageEl.querySelectorAll('.pdfChar[data-i]');
    var min = -1;
    var max = -1;

    for (var i = 0; i < spans.length; i++) {
      if (!range.intersectsNode(spans[i])) {
        continue;
      }

      var index = parseInt(spans[i].getAttribute('data-i') || '-1', 10);

      if (index < 0) {
        continue;
      }

      if (min < 0 || index < min) { min = index; }
      if (index > max) { max = index; }
    }

    return min < 0 ? null : { start: min, end: max };
  }

  /* 把当前选区锁成一份锚点。点弹条按钮时选区会随焦点丢失，所以动作都用这一份。 */
  function captureSelection() {
    var ctx = selectionContext();

    if (!ctx || !ctx.sel || !ctx.sel.rangeCount) {
      return null;
    }

    var sel = ctx.sel;
    var quote = String(sel.toString() || '').trim();

    if (!quote) {
      return null;
    }

    var range = sel.getRangeAt(0);

    if (ctx.root && !ctx.root.contains(range.commonAncestorContainer)) {
      return null;
    }

    var anchor = {
      startPath: pathTo(range.startContainer, ctx.root),
      startOffset: range.startOffset,
      endPath: pathTo(range.endContainer, ctx.root),
      endOffset: range.endOffset,
      percentInChapter: isPdf() ? null : capturePercentInChapter()
    };

    if (isPdf()) {
      var chars = capturePdfCharRange(range);

      if (chars) {
        anchor.pageCharStart = chars.start;
        anchor.pageCharEnd = chars.end;
      }
    }

    return { anchor: anchor, quote: quote, range: range, doc: ctx.doc };
  }

  function selectionInContent(ctx) {
    if (!ctx || !ctx.sel || !ctx.sel.rangeCount) {
      return false;
    }

    var node = ctx.sel.getRangeAt(0).commonAncestorContainer;

    return !!ctx.root && ctx.root.contains(node);
  }

  // ---------- 选中弹条 ----------

  function showSelectionBarFromCurrent() {
    var shot = captureSelection();

    if (!shot) {
      hideSelectionBar();
      return;
    }

    state.selPending = shot;
    showSelectionBar(shot);
  }

  function showSelectionBar(shot) {
    var bar = document.getElementById('selBar');
    var quote = document.getElementById('selQuote');

    if (!bar) {
      return;
    }

    if (quote) {
      quote.textContent = shot.quote;
    }

    bar.hidden = false;

    /* 定位：选区上方居中。iframe 里的坐标要加上 frame 自身的偏移。 */
    var rect = shot.range.getBoundingClientRect();
    var frameRect = (!isPdf() && frame) ? frame.getBoundingClientRect() : { left: 0, top: 0 };
    var barWidth = bar.offsetWidth || 260;
    var barHeight = bar.offsetHeight || 34;
    var centerX = frameRect.left + rect.left + rect.width / 2;
    var top = frameRect.top + rect.top - 8;

    var left = Math.max(8, Math.min(window.innerWidth - barWidth - 8, centerX - barWidth / 2));
    var y = top - barHeight;

    bar.style.left = left + 'px';
    bar.style.top = Math.max(8, y) + 'px';
  }

  function hideSelectionBar() {
    var bar = document.getElementById('selBar');

    if (bar) {
      bar.hidden = true;
    }

    /* 【刻意不清 state.selPending】点弹条按钮时选区会随焦点丢失，清掉锁定选区
       会让"加笔记 → 保存"变成"选区已失效"（用户 2026-10-18 实测踩到的 bug）。
       锁定选区只在"开始新的点击"或"动作完成"时被覆盖。 */
  }

  /* 正文里松开鼠标 = 可能刚选完字。延迟一拍让选区稳定（双击选词、三击选段也走这条）。 */
  function onContentMouseUp() {
    setTimeout(showSelectionBarFromCurrent, 0);
  }

  function onContentMouseDown() {
    /* 开始新的选择：旧的锁定选区作废。 */
    state.selPending = null;
    hideSelectionBar();
  }

  /* WebView2 关掉了内核右键菜单（ReaderWebHost.ApplySecuritySettings），
     所以右键在正文上就等于"弹出这一条"——与选中文字后的弹条完全等价。 */
  function onContentContextMenu(event) {
    var shot = captureSelection();

    if (!shot) {
      return;
    }

    event.preventDefault();
    state.selPending = shot;
    showSelectionBar(shot);
  }

  function onShellMouseDown(event) {
    var target = event.target;
    var bar = document.getElementById('selBar');
    var card = document.getElementById('noteCard');

    if (bar && bar.contains(target)) {
      return;
    }

    if (card && card.contains(target)) {
      return;
    }

    state.selPending = null;
    hideSelectionBar();
    closeNoteCard();
  }

  function onShellMouseUp() {
    var sel = window.getSelection();            /* PDF 的选区在外壳文档里 */
    var text = sel ? String(sel.toString() || '').trim() : '';

    if (!text || !isPdf()) {
      return;
    }

    var shot = captureSelection();

    if (shot) {
      state.selPending = shot;
      showSelectionBar(shot);
    }
  }

  // ---------- 弹条上的四个动作 ----------

  /* 七种标记：id 与 notes.style 一一对应（0 荧光 / 1 下划线 / 2 波浪线 / 3 删除线 /
     4 虚线 / 5 背景色 / 6 字体色）。都只往正文套一层 <mark>（PDF 是给字符框加 class），
     **不改原文件**。颜色由 notes.color 决定（0 默认，1..8 调色板）。 */
  var MARK_STYLES = [
    { id: 0, cls: 'ets-note', name: '荧光' },
    { id: 1, cls: 'ets-mark-underline', name: '下划线' },
    { id: 2, cls: 'ets-mark-wavy', name: '波浪线' },
    { id: 3, cls: 'ets-mark-strike', name: '删除线' },
    { id: 4, cls: 'ets-mark-dashed', name: '虚线' },
    { id: 5, cls: 'ets-mark-background', name: '背景色' },
    { id: 6, cls: 'ets-mark-foreground', name: '字体色' }
  ];

  /* 调色板（与 Core 的 MarkPalette 一致；0 = 默认色，不在这里）：
     七个基色 + 相邻基色的中间色 + 灰。色点显示原色，只有荧光底色会降调。 */
  var MARK_PALETTE = [
    { id: 1, name: '红', color: '#E53935' },
    { id: 2, name: '红橙', color: '#F4511E' },
    { id: 3, name: '橙', color: '#FB8C00' },
    { id: 4, name: '橙黄', color: '#F9A825' },
    { id: 5, name: '黄', color: '#FDD835' },
    { id: 6, name: '黄绿', color: '#9CCC65' },
    { id: 7, name: '绿', color: '#43A047' },
    { id: 8, name: '绿青', color: '#26A69A' },
    { id: 9, name: '青', color: '#00ACC1' },
    { id: 10, name: '青蓝', color: '#039BE5' },
    { id: 11, name: '蓝', color: '#1E88E5' },
    { id: 12, name: '蓝紫', color: '#5E35B1' },
    { id: 13, name: '紫', color: '#8E24AA' },
    { id: 14, name: '灰', color: '#757575' }
  ];

  /* 十六进制颜色 → 半透明 rgba。荧光"降调"就是**降低透明度**（用户 2026-10-18 纠正：
     不是把原色和白色混合），透出主题底色后自然变淡。 */
  function rgbaOf(hex, alpha) {
    var matched = /^#([0-9a-fA-F]{6})$/.exec(String(hex || ''));

    if (!matched) {
      return hex;
    }

    var value = parseInt(matched[1], 16);
    var r = (value >> 16) & 255;
    var g = (value >> 8) & 255;
    var b = value & 255;

    return 'rgba(' + r + ', ' + g + ', ' + b + ', ' + alpha + ')';
  }

  function colorOf(colorId) {
    for (var i = 0; i < MARK_PALETTE.length; i++) {
      if (MARK_PALETTE[i].id === colorId) {
        return MARK_PALETTE[i].color;
      }
    }

    return null;
  }

  function markClassOf(style) {
    for (var i = 0; i < MARK_STYLES.length; i++) {
      if (MARK_STYLES[i].id === style) {
        return MARK_STYLES[i].cls;
      }
    }

    return 'ets-note';
  }

  function markNameOf(style) {
    for (var i = 0; i < MARK_STYLES.length; i++) {
      if (MARK_STYLES[i].id === style) {
        return MARK_STYLES[i].name;
      }
    }

    return '荧光';
  }

  /* 把颜色写到元素上的 CSS 变量里（CSS 规则用 var(--ets-*) 取）：
       荧光 -> 底色用**半透明**（降低透明度 = 降调，透出主题底色）；
       背景色 -> 原色铺底；字体色 -> 原色改字；线类 -> 原色做线色。
       文字色一律继承正文（不写死深色），主题切换时跟着变。 */
  function applyMarkVisual(el, note) {
    var color = colorOf(note.color || 0);

    if (!el || !color) {
      return;
    }

    var style = note.style || 0;

    if (style === 0) {
      el.style.setProperty('--ets-bg', rgbaOf(color, 0.35));
    } else if (style === 5) {
      el.style.setProperty('--ets-bg', color);
    } else if (style === 6) {
      el.style.setProperty('--ets-fg', color);
    } else {
      el.style.setProperty('--ets-line', color);
    }
  }

  function postMark(style, shot, color) {
    post('reader.addNote', {
      type: 0,
      style: style,
      color: typeof color === 'number' ? color : 0,
      quote: shot.quote,
      anchor: shot.anchor,
      spineIndex: isPdf() ? null : state.index,
      pageIndex: isPdf() ? (state.pdf ? state.pdf.page : null) : null
    });
  }

  /* 选区当前落在哪个已有标记上（用于"再点一次取消/换样式"）。
     用**引文比较**而不是 DOM 相交：标记可能是多个 <mark>（跨段落各一段），
     选区又可能跨节点，DOM 相交判断在边界上不可靠（2026-10-18 实测：取消永远变成再加）。
     判据：选区文字与某条标记的引文互相包含（去空白后比较）。 */
  function findMarkedAtSelection(shot) {
    if (isPdf()) {
      var chars = capturePdfCharRange(shot.range);
      var page = state.pdf ? state.pdf.page : -1;

      if (!chars) {
        return null;
      }

      var pdfNotes = state.notes || [];

      for (var i = 0; i < pdfNotes.length; i++) {
        var pdfNote = pdfNotes[i];
        var pdfAnchor = pdfNote.anchor;

        if (pdfNote.type === 1 || pdfNote.pageIndex !== page || !pdfAnchor
          || typeof pdfAnchor.pageCharStart !== 'number') {
          continue;
        }

        var pdfEnd = typeof pdfAnchor.pageCharEnd === 'number'
          ? pdfAnchor.pageCharEnd
          : pdfAnchor.pageCharStart;

        if (chars.start <= pdfEnd && chars.end >= pdfAnchor.pageCharStart) {
          return { id: pdfNote.id, style: pdfNote.style || 0, note: pdfNote };
        }
      }

      return null;
    }

    var target = String(shot.quote || '').replace(/\s+/g, '');
    var notes = state.notes || [];

    if (!target) {
      return null;
    }

    for (var j = 0; j < notes.length; j++) {
      var note = notes[j];

      if (note.type === 1 || note.spineIndex !== state.index) {
        continue;
      }

      var quote = String(note.quote || '').replace(/\s+/g, '');

      if (!quote) {
        continue;
      }

      if (target === quote || target.indexOf(quote) >= 0 || quote.indexOf(target) >= 0) {
        return { id: note.id, style: note.style || 0, note: note };
      }
    }

    return null;
  }

  /* 点四个标记按钮中的任意一个：没标记 → 添加；已有同款 → 取消；已有别的 → 换成这一款。 */
  function applyMark(style) {
    var shot = state.selPending || captureSelection();

    if (!shot) {
      notice('没有选中的文字。');
      return;
    }

    if (isComic()) {
      notice('漫画没有可标记的文字。');
      hideSelectionBar();
      return;
    }

    var existing = findMarkedAtSelection(shot);

    if (existing) {
      post('reader.deleteAnnotation', { kind: 'note', id: existing.id });

      if ((existing.style || 0) === style) {
        notice('已取消' + markNameOf(style) + '。');
      } else {
        postMark(style, shot);
        notice('已换成' + markNameOf(style) + '。');
      }

      hideSelectionBar();
      return;
    }

    postMark(style, shot);
    hideSelectionBar();
    notice('已加' + markNameOf(style) + '：' +
      shot.quote.slice(0, 16) + (shot.quote.length > 16 ? '…' : ''));
  }

  function findNoteById(id) {
    var list = state.notes || [];

    for (var i = 0; i < list.length; i++) {
      if (String(list[i].id) === String(id)) {
        return list[i];
      }
    }

    return null;
  }

  // ---------- 点已标记的文字 / 弹条加笔记：完整笔记面板 ----------

  function noteCardPosition(card, x, y) {
    var width = card.offsetWidth || 420;
    var height = card.offsetHeight || 220;
    var left = Math.max(8, Math.min(window.innerWidth - width - 8, x - 24));
    var top = y - height - 10;

    if (top < 8) {
      top = Math.min(window.innerHeight - height - 8, y + 20);
    }

    card.style.left = left + 'px';
    card.style.top = Math.max(8, top) + 'px';
  }

  function currentPanelText() {
    var text = document.getElementById('noteCardText');

    return text ? String(text.value || '').trim() : '';
  }

  /* 画颜色行与样式行：选中哪一项由 state.panelStyle / state.panelColor 决定。 */
  function renderNotePanelControls() {
    var colorRow = document.getElementById('noteColorRow');
    var styleRow = document.getElementById('noteStyleRow');
    var styleLabel = document.getElementById('noteCardStyle');

    if (styleLabel) {
      styleLabel.textContent = markNameOf(state.panelStyle);
    }

    if (colorRow) {
      colorRow.textContent = '';

      MARK_PALETTE.forEach(function (entry) {
        var dot = document.createElement('button');

        dot.type = 'button';
        dot.className = 'etsColorDot' + (state.panelColor === entry.id ? ' etsColorActive' : '');
        dot.style.background = entry.color;
        dot.title = entry.name;
        dot.setAttribute('data-color', String(entry.id));
        dot.addEventListener('click', function () { pickNoteColor(entry.id); });
        colorRow.appendChild(dot);
      });
    }

    if (styleRow) {
      styleRow.textContent = '';

      MARK_STYLES.forEach(function (entry) {
        var button = document.createElement('button');

        button.type = 'button';
        button.textContent = entry.name;
        button.className = state.panelStyle === entry.id ? 'etsStyleActive' : '';
        button.title = '把这段文字标成' + entry.name;
        button.setAttribute('data-style', String(entry.id));
        button.addEventListener('click', function () { pickNoteStyle(entry.id); });
        styleRow.appendChild(button);
      });
    }
  }

  /* note 带 id = 改已有笔记；不带 id = 新建（draft：quote + anchor）。 */
  function openNotePanel(note, x, y) {
    var card = document.getElementById('noteCard');

    if (!card || !note) {
      return false;
    }

    state.panelNoteId = note.id || null;
    state.panelDraft = note.id ? null : note;
    state.panelStyle = typeof note.style === 'number' ? note.style : 0;
    state.panelColor = typeof note.color === 'number' ? note.color : 0;

    var time = document.getElementById('noteCardTime');
    var quote = document.getElementById('noteCardQuote');
    var text = document.getElementById('noteCardText');
    var del = document.getElementById('noteCardDelete');

    if (time) { time.textContent = note.createdAt ? fmtTime(note.createdAt) : '新笔记'; }
    if (quote) { quote.textContent = String(note.quote || ''); }
    if (text) { text.value = String(note.content || ''); }
    if (del) { del.disabled = !state.panelNoteId; }

    renderNotePanelControls();
    card.hidden = false;
    noteCardPosition(card, x, y);
    return true;
  }

  /* 弹条上的「加笔记」：拿当前选区开一个空面板（保存时才落库）。 */
  function openNotePanelDraft() {
    var shot = state.selPending || captureSelection();

    if (!shot) {
      notice('没有选中的文字。');
      return;
    }

    if (isComic()) {
      notice('漫画没有可加笔记的文字。');
      hideSelectionBar();
      return;
    }

    var x = 200;
    var y = 300;
    var rect = shot.range.getBoundingClientRect();
    var frameRect = (!isPdf() && frame) ? frame.getBoundingClientRect() : { left: 0, top: 0 };

    x = frameRect.left + rect.left;
    y = frameRect.top + rect.top;

    hideSelectionBar();
    openNotePanel({ quote: shot.quote, anchor: shot.anchor }, x, y);
  }

  function closeNoteCard() {
    var card = document.getElementById('noteCard');

    if (card) {
      card.hidden = true;
    }

    state.panelNoteId = null;
    state.panelDraft = null;
  }

  /* 样式/颜色点一下立即生效（已有笔记直接提交；新建的等保存一起提交）。 */
  function pickNoteStyle(id) {
    state.panelStyle = id;
    renderNotePanelControls();
    commitNotePanelIfExisting();
  }

  function pickNoteColor(id) {
    state.panelColor = state.panelColor === id ? 0 : id;
    renderNotePanelControls();
    commitNotePanelIfExisting();
  }

  function commitNotePanelIfExisting() {
    if (!state.panelNoteId) {
      return;
    }

    post('reader.updateNote', {
      id: state.panelNoteId,
      content: currentPanelText(),
      style: state.panelStyle,
      color: state.panelColor
    });
  }

  function saveNoteCard() {
    var text = currentPanelText();

    if (state.panelNoteId) {
      post('reader.updateNote', {
        id: state.panelNoteId,
        content: text,
        style: state.panelStyle,
        color: state.panelColor
      });

      closeNoteCard();
      notice('笔记已保存。');
      return;
    }

    var draft = state.panelDraft;

    if (!draft) {
      closeNoteCard();
      return;
    }

    post('reader.addNote', {
      type: text ? 1 : 0,
      style: state.panelStyle,
      color: state.panelColor,
      quote: draft.quote,
      content: text || null,
      anchor: draft.anchor,
      spineIndex: isPdf() ? null : state.index,
      pageIndex: isPdf() ? (state.pdf ? state.pdf.page : null) : null
    });

    closeNoteCard();

    notice(text
      ? '已加笔记。'
      : '已加' + markNameOf(state.panelStyle) + '。');
  }

  function deleteNoteCard() {
    if (!state.panelNoteId) {
      return;
    }

    post('reader.deleteAnnotation', { kind: 'note', id: state.panelNoteId });
    closeNoteCard();
    notice('已删除。');
  }

  /* 点正文：落在某个标记上、且当前没有选区（不是拖选）→ 在上方弹出这条笔记。
     PDF 按字符框的 data-i 反查所属笔记（与标记高亮同一套下标）。 */
  function onContentClick(event) {
    var ctx = selectionContext();

    if (!ctx || !ctx.doc) {
      return;
    }

    var selected = ctx.sel ? String(ctx.sel.toString() || '').trim() : '';

    if (selected) {
      return;   // 拖动/双击选中的是文字，交给弹条
    }

    if (isPdf()) {
      var charNode = event.target && event.target.closest
        ? event.target.closest('.pdfChar[data-i]')
        : null;

      if (!charNode || !state.pdf) {
        return;
      }

      var index = parseInt(charNode.getAttribute('data-i') || '-1', 10);
      var page = state.pdf.page;
      var notes = state.notes || [];

      for (var i = 0; i < notes.length; i++) {
        var note = notes[i];

        if (note.pageIndex !== page || !note.anchor
          || typeof note.anchor.pageCharStart !== 'number') {
          continue;
        }

        var end = typeof note.anchor.pageCharEnd === 'number'
          ? note.anchor.pageCharEnd
          : note.anchor.pageCharStart;

        if (index >= note.anchor.pageCharStart && index <= end) {
          hideSelectionBar();
          openNotePanel(note, event.clientX, event.clientY);
          return;
        }
      }

      return;
    }

    var mark = event.target && event.target.closest
      ? event.target.closest('mark[data-note-id]')
      : null;

    if (!mark) {
      return;
    }

    var found = findNoteById(mark.getAttribute('data-note-id'));

    if (!found) {
      return;
    }

    var frameRect = frame ? frame.getBoundingClientRect() : { left: 0, top: 0 };
    var rect = mark.getBoundingClientRect();

    hideSelectionBar();
    openNotePanel(found, frameRect.left + rect.left, frameRect.top + rect.top);
  }

  /* 复制：跨 document 的选区直接 execCommand('copy') 不可靠，
     所以把文字放进一个临时 textarea 再复制（WebView2 里实测有效）。 */
  function copySelection() {
    var shot = state.selPending || captureSelection();

    if (!shot) {
      notice('没有选中的文字。');
      return;
    }

    var area = document.createElement('textarea');

    area.value = shot.quote;
    area.setAttribute('readonly', 'readonly');
    area.style.position = 'fixed';
    area.style.left = '-9999px';
    document.body.appendChild(area);
    area.select();

    var ok = false;

    try {
      ok = document.execCommand('copy');
    } catch (ignored) {
      ok = false;
    }

    document.body.removeChild(area);

    notice(ok ? '已复制到剪贴板。' : '复制失败：请用 Ctrl+C。');
    hideSelectionBar();
  }

  function findSelection() {
    var shot = state.selPending || captureSelection();

    if (!shot) {
      notice('没有选中的文字。');
      return;
    }

    hideSelectionBar();
    openFindBar();

    var input = document.getElementById('findInput');

    if (!input) {
      return;
    }

    input.value = shot.quote;
    runFind(shot.quote);
  }

  // ---------- 加书签 ----------

  function chapterTitleNow() {
    var el = document.getElementById('chapterTitle');

    return el ? String(el.textContent || '') : '';
  }

  function currentLocator() {
    var win = frame && frame.contentWindow;
    var scrollTop = (!isPaged() && win) ? Math.round(win.scrollY || 0) : 0;

    return {
      chapterIndex: state.index,
      chapterHref: chapterHrefOf(state.index),
      percentInChapter: capturePercentInChapter(),
      scrollTop: scrollTop,
      scrollHeight: currentScrollHeight()
    };
  }

  function addBookmark() {
    if (isPdf() || isComic()) {
      var page = state.pdf ? state.pdf.page : -1;

      if (typeof page !== 'number' || page < 0) {
        notice('页面还没准备好。');
        return;
      }

      post('reader.addBookmark', { pageIndex: page, label: '第 ' + (page + 1) + ' 页' });
      notice('已加书签：第 ' + (page + 1) + ' 页');
      return;
    }

    var label = chapterTitleNow() || ('第 ' + (state.index + 1) + ' 章');

    post('reader.addBookmark', {
      chapterIndex: state.index,
      label: label,
      locator: currentLocator()
    });

    notice('已加书签：' + label);
  }

  // ---------- 书签与笔记面板 ----------

  /* 打开"书签页"或"笔记页"。两个页面共用一个面板，按 mode 只渲染一类
     （用户明确要求：书签页只显示书签，笔记页只显示笔记）。 */
  function openAnnotationPanel(mode) {
    var panel = document.getElementById('notesPanel');

    if (!panel) {
      return false;
    }

    state.panelMode = mode === 'notes' ? 'notes' : 'bookmarks';
    renderAnnotationLists();
    panel.hidden = false;
    return true;
  }

  function closeAnnotationPanel() {
    var panel = document.getElementById('notesPanel');

    if (panel) {
      panel.hidden = true;
    }
  }

  function toggleNotesPanel(open) {
    var panel = document.getElementById('notesPanel');

    if (!panel) {
      return false;
    }

    var next = typeof open === 'boolean' ? open : panel.hidden;

    if (next) {
      return openAnnotationPanel(state.panelMode);
    }

    closeAnnotationPanel();
    return false;
  }

  function pad2(n) {
    return (n < 10 ? '0' : '') + n;
  }

  function fmtTime(value) {
    if (!value) {
      return '';
    }

    var date = new Date(value);

    if (isNaN(date.getTime())) {
      return String(value);
    }

    return date.getFullYear() + '-' + pad2(date.getMonth() + 1) + '-' + pad2(date.getDate()) +
      ' ' + pad2(date.getHours()) + ':' + pad2(date.getMinutes());
  }

  function appendEmpty(body, text) {
    var empty = document.createElement('div');

    empty.className = 'annoEmpty';
    empty.textContent = text;
    body.appendChild(empty);
  }

  function annoMetaText(kind, item) {
    var parts = [];
    var time = fmtTime(item.createdAt);

    if (time) {
      parts.push(time);
    }

    if (kind === 'bookmark') {
      if (typeof item.pageIndex === 'number' && item.pageIndex >= 0) {
        parts.push('第 ' + (item.pageIndex + 1) + ' 页');
      } else if (typeof item.chapterIndex === 'number' && item.chapterIndex >= 0) {
        parts.push('第 ' + (item.chapterIndex + 1) + ' 章');
      }
    } else {
      if (typeof item.pageIndex === 'number' && item.pageIndex >= 0) {
        parts.push('第 ' + (item.pageIndex + 1) + ' 页');
      } else if (typeof item.spineIndex === 'number' && item.spineIndex >= 0) {
        parts.push('第 ' + (item.spineIndex + 1) + ' 章');
      }

      parts.push(item.type === 1 ? '批注' : markNameOf(item.style || 0));
    }

    return parts.join(' · ');
  }

  function buildAnnoRow(kind, item) {
    var row = document.createElement('div');
    var label = document.createElement('div');
    var meta = document.createElement('div');
    var del = document.createElement('button');

    row.className = 'annoItem';
    row.setAttribute('data-kind', kind);
    row.setAttribute('data-id', String(item.id || ''));

    label.className = 'annoLabel';
    label.textContent = kind === 'bookmark'
      ? String(item.label || ('第 ' + ((item.chapterIndex || 0) + 1) + ' 章'))
      : String(item.quote || '(空)');

    meta.className = 'annoMeta';
    meta.textContent = annoMetaText(kind, item);

    del.className = 'annoDel';
    del.type = 'button';
    del.title = '删除';
    del.textContent = '×';

    del.addEventListener('click', function (event) {
      event.stopPropagation();
      deleteAnnotation(kind, item.id);
    });

    row.appendChild(del);
    row.appendChild(label);
    row.appendChild(meta);

    if (kind === 'note' && item.content) {
      var quote = document.createElement('div');

      quote.className = 'annoQuote';
      quote.textContent = item.content;
      row.appendChild(quote);
    }

    row.addEventListener('click', function () {
      jumpToAnnotation(kind, item);
    });

    return row;
  }

  function renderAnnotationLists() {
    var body = document.getElementById('notesBody');

    if (!body) {
      return;
    }

    var mode = state.panelMode === 'notes' ? 'notes' : 'bookmarks';
    var title = document.getElementById('notesTitle');
    var hint = document.getElementById('notesHint');
    var add = document.getElementById('annotationsAdd');
    var list = mode === 'notes' ? (state.notes || []) : (state.bookmarks || []);

    body.textContent = '';

    if (title) {
      title.textContent = mode === 'notes' ? '笔记' : '书签';
    }

    if (hint) {
      hint.textContent = '共 ' + list.length + ' 条';
    }

    /* 「添加书签」只在书签页出现：笔记只能从正文选中文字来加（用户要求分开）。 */
    if (add) {
      add.hidden = mode !== 'bookmarks';
    }

    if (!list.length) {
      appendEmpty(body, mode === 'notes'
        ? '还没有笔记。选中正文里的文字，点「加笔记」写一条。'
        : '还没有书签。点右上角「添加书签」，或在正文里按 Ctrl+B。');
      return;
    }

    for (var i = 0; i < list.length; i++) {
      body.appendChild(buildAnnoRow(mode === 'notes' ? 'note' : 'bookmark', list[i]));
    }
  }

  // ---------- 跳转 ----------

  function scrollToFirstNoteMark() {
    var doc = frame && frame.contentDocument;

    if (!doc) {
      return false;
    }

    var mark = doc.querySelector('mark.ets-note');

    if (!mark) {
      return false;
    }

    var rect = mark.getBoundingClientRect();
    var win = frame.contentWindow;

    scrollTo(rect.top + ((win && win.scrollY) || 0) - 80);
    return true;
  }

  function jumpToAnnotation(kind, item) {
    closeAnnotationPanel();

    if (kind === 'bookmark') {
      if (typeof item.pageIndex === 'number' && item.pageIndex >= 0) {
        pdfGoto(item.pageIndex, 'user');
        return;
      }

      var loc = item.locator;

      if (!loc) {
        notice('这条书签没有可用的位置。');
        return;
      }

      if (loc.chapterIndex === state.index) {
        restoreLocator(loc, null);
        return;
      }

      state.pendingLocator = loc;
      requestChapter(loc.chapterIndex);
      return;
    }

    if (typeof item.pageIndex === 'number' && item.pageIndex >= 0) {
      pdfGoto(item.pageIndex, 'user');
      notice('已跳到笔记所在的第 ' + (item.pageIndex + 1) + ' 页。');
      return;
    }

    if (item.spineIndex === state.index) {
      var doc = frame && frame.contentDocument;
      var located = false;

      /* 【为什么要单独试一次】本章可能有别的笔记已经画出高亮，如果只看
         "本章有没有 mark"，位置失效的那一条会被误判成成功、也不给提示
         （验收 J5 实测踩到）。所以先清掉本章全部标记，单独恢复这一条拿到
         located，再整体重画一遍——高亮结果不变，但"这条到底找没找到"是准的。 */
      if (doc) {
        clearNoteMarks(doc);
        located = restoreNoteMark(item, doc);
      }

      applyAnnotations();

      if (located && scrollToFirstNoteMark()) {
        return;
      }

      if (item.anchor && typeof item.anchor.percentInChapter === 'number') {
        scrollTo(maxScrollTop() * item.anchor.percentInChapter);
        notice('笔记位置已失效，已按章内比例定位到附近。');
        return;
      }

      notice('笔记位置已失效（正文可能被修改过），无法精确定位。');
      return;
    }

    state.pendingLocator = {
      chapterIndex: item.spineIndex,
      chapterHref: chapterHrefOf(item.spineIndex),
      percentInChapter: item.anchor ? item.anchor.percentInChapter : null,
      scrollTop: null,
      scrollHeight: null
    };

    requestChapter(item.spineIndex);
  }

  function deleteAnnotation(kind, id) {
    if (!id) {
      return;
    }

    post('reader.deleteAnnotation', { kind: kind, id: id });
  }

  function exportAnnotations() {
    post('reader.exportAnnotations', {});
  }

  function onAnnotations(payload) {
    state.bookmarks = payload && payload.bookmarks ? payload.bookmarks : [];
    state.notes = payload && payload.notes ? payload.notes : [];
    renderAnnotationLists();
    applyAnnotations();
  }

  function onAnnotationsExported(payload) {
    var path = payload && payload.path ? String(payload.path) : '';

    notice(path ? ('已导出：' + path) : '已导出书签与笔记。');
  }

  // ---------- 高亮渲染（EPUB / TXT） ----------

  function clearNoteMarks(doc) {
    var marks = doc.querySelectorAll('mark[data-note-id]');

    for (var i = marks.length - 1; i >= 0; i--) {
      var mark = marks[i];
      var parent = mark.parentNode;

      if (!parent) {
        continue;
      }

      while (mark.firstChild) {
        parent.insertBefore(mark.firstChild, mark);
      }

      parent.removeChild(mark);
      parent.normalize();
    }
  }

  /* 把 Range 里的文本包成 mark。跨节点实现要点：
     先按节点收集区间，再**倒序**处理——处理后面的节点不会让前面的偏移失效。
     noteId 写到 data-note-id 上：点同一段再点标记按钮时要靠它找到"是哪一条"。 */
  function wrapRange(range, className, doc, noteId, note) {
    var start = range.startContainer;
    var end = range.endContainer;
    /* 遍历根必须取"公共祖先"，不能取起点父元素：跨段选区（p0 → p1）时
       起点父元素只是 p0，p1 那半截永远包不上（用户 2026-10-18 实测：跨段标记只亮半截）。 */
    var root = range.commonAncestorContainer;

    if (root.nodeType === 3) {
      root = root.parentNode;
    }

    var walker = doc.createTreeWalker(root, 4 /* SHOW_TEXT */, null);
    var items = [];

    while (walker.nextNode()) {
      var node = walker.currentNode;

      if (!range.intersectsNode(node)) {
        continue;
      }

      var from = node === start ? range.startOffset : 0;
      var to = node === end ? range.endOffset : node.length;

      if (to > from) {
        items.push({ node: node, from: from, to: to });
      }
    }

    for (var i = items.length - 1; i >= 0; i--) {
      var item = items[i];
      var text = item.node;

      if (item.to < text.length) {
        text.splitText(item.to);
      }

      if (item.from > 0) {
        text = text.splitText(item.from);
      }

      var mark = doc.createElement('mark');

      mark.className = className;

      if (noteId) {
        mark.setAttribute('data-note-id', String(noteId));
      }

      if (note) {
        applyMarkVisual(mark, note);
      }

      text.parentNode.insertBefore(mark, text);
      mark.appendChild(text);
    }

    return items.length;
  }

  /* 引文兜底：在某个块元素里按"文本流偏移"找 quote，再映射回 (node, offset)。
     只处理块内连续文本；跨块的引文（比如带小标题的段落）交给章内比例那一级。 */
  function rangeFromTextOffset(doc, root, needle) {
    var walker = doc.createTreeWalker(root, 4 /* SHOW_TEXT */, null);
    var nodes = [];
    var starts = [];
    var total = '';

    while (walker.nextNode()) {
      var node = walker.currentNode;

      nodes.push(node);
      starts.push(total.length);
      total += node.nodeValue || '';
    }

    var at = total.indexOf(needle);

    if (at < 0) {
      return null;
    }

    var endAt = at + needle.length;

    function locate(pos) {
      for (var i = 0; i < nodes.length; i++) {
        var len = (nodes[i].nodeValue || '').length;

        if (pos >= starts[i] && pos <= starts[i] + len) {
          return { node: nodes[i], offset: pos - starts[i] };
        }
      }

      return null;
    }

    var from = locate(at);
    var to = locate(endAt);

    if (!from || !to) {
      return null;
    }

    var range = doc.createRange();

    range.setStart(from.node, from.offset);
    range.setEnd(to.node, to.offset);
    return range;
  }

  function wrapQuoteInDoc(doc, note) {
    var quote = note.quote;
    var needle = String(quote || '').replace(/\s+/g, '');

    if (!needle) {
      return false;
    }

    var blocks = doc.querySelectorAll('p, h1, h2, h3, h4, li, blockquote, td');

    for (var i = 0; i < blocks.length; i++) {
      var text = String(blocks[i].textContent || '').replace(/\s+/g, '');

      if (text.indexOf(needle) < 0) {
        continue;
      }

      var range = rangeFromTextOffset(doc, blocks[i], quote);

      if (range) {
        wrapRange(range, markClassOf(note.style || 0), doc, note.id, note);
        return true;
      }
    }

    return false;
  }

  function restoreNoteMark(note, doc) {
    var anchor = note.anchor;

    if (!anchor || !note.quote) {
      return false;
    }

    /* 第一级：路径 + 偏移。命中后还要比对文本——路径可能还在，但内容被改过，
       直接按路径画会把高亮压到别的字上。 */
    if (anchor.startPath) {
      var startNode = nodeFromPath(doc, anchor.startPath);
      var endNode = nodeFromPath(doc, anchor.endPath || anchor.startPath);

      if (startNode && endNode) {
        try {
          var range = doc.createRange();
          var startOffset = Math.min(Math.max(0, anchor.startOffset || 0), startNode.nodeType === 3 ? startNode.length : startNode.childNodes.length);
          var endOffset = Math.min(Math.max(0, anchor.endOffset || 0), endNode.nodeType === 3 ? endNode.length : endNode.childNodes.length);

          range.setStart(startNode, startOffset);
          range.setEnd(endNode, endOffset);

          var got = String(range.toString() || '').replace(/\s+/g, '');
          var want = String(note.quote || '').replace(/\s+/g, '');

          if (got && (got === want || got.indexOf(want) >= 0 || want.indexOf(got) >= 0)) {
            wrapRange(range, markClassOf(note.style || 0), doc, note.id, note);
            return true;
          }
        } catch (ignored) {
          // 路径解析失败就走下一级
        }
      }
    }

    /* 第二级：引文搜索。 */
    return wrapQuoteInDoc(doc, note);
  }

  function applyAnnotations() {
    if (isPdf()) {
      applyPdfNoteHighlights(state.pdf ? state.pdf.page : -1);
      return;
    }

    var ctx = selectionContext();

    if (!ctx || !ctx.doc || !ctx.doc.body) {
      return;
    }

    clearNoteMarks(ctx.doc);

    var list = state.notes || [];

    for (var i = 0; i < list.length; i++) {
      var note = list[i];

      if (note.spineIndex !== state.index) {
        continue;
      }

      restoreNoteMark(note, ctx.doc);
    }
  }

  /* PDF：按页给文字层字符框加对应样式。chars 下标与文字层 data-i 一一对应。 */
  function applyPdfNoteHighlights(page) {
    if (!isPdf() || !state.pdf) {
      return;
    }

    var target = typeof page === 'number' ? page : state.pdf.page;
    var all = document.querySelectorAll('#pdfPages .pdfChar');

    for (var i = 0; i < all.length; i++) {
      for (var s = 0; s < MARK_STYLES.length; s++) {
        all[i].classList.remove(MARK_STYLES[s].cls);
      }
    }

    var obj = pdfPageObj(target);
    var pageEl = obj && obj.element;

    if (!pageEl) {
      return;
    }

    var notes = state.notes || [];

    for (var j = 0; j < notes.length; j++) {
      var note = notes[j];
      var anchor = note.anchor;

      if (note.pageIndex !== target || !anchor || typeof anchor.pageCharStart !== 'number') {
        continue;
      }

      var cls = markClassOf(note.style || 0);
      var spans = pageEl.querySelectorAll('.pdfChar[data-i]');

      for (var k = 0; k < spans.length; k++) {
        var index = parseInt(spans[k].getAttribute('data-i') || '-1', 10);

        if (index >= anchor.pageCharStart && index <= (anchor.pageCharEnd || anchor.pageCharStart)) {
          spans[k].classList.add(cls);
          applyMarkVisual(spans[k], note);
        }
      }
    }
  }

  /* 一次性装配：工具栏按钮、弹条上的标记/笔记动作、笔记编辑器、两个页面、外壳鼠标事件。 */
  function wireAnnotations() {
    var bookmarkButton = document.getElementById('btnBookmark');
    var notesButton = document.getElementById('btnAnnotations');
    var addBookmarkButton = document.getElementById('annotationsAdd');
    var selHighlight = document.getElementById('selHighlight');
    var selUnderline = document.getElementById('selUnderline');
    var selWavy = document.getElementById('selWavy');
    var selStrike = document.getElementById('selStrike');
    var selNote = document.getElementById('selNote');
    var selCopy = document.getElementById('selCopy');
    var selFind = document.getElementById('selFind');
    var notesClose = document.getElementById('notesClose');
    var notesExport = document.getElementById('notesExport');
    var cardSave = document.getElementById('noteCardSave');
    var cardClose = document.getElementById('noteCardClose');
    var cardDelete = document.getElementById('noteCardDelete');

    /* 工具栏「书签」= 打开书签页；「笔记」= 打开笔记页。加书签是书签页里那枚按钮
       （Ctrl+B 仍然直接加书签，是计划书要求的快捷方式）。 */
    if (bookmarkButton) {
      bookmarkButton.addEventListener('click', function () { openAnnotationPanel('bookmarks'); });
    }

    if (notesButton) {
      notesButton.addEventListener('click', function () { openAnnotationPanel('notes'); });
    }

    if (addBookmarkButton) { addBookmarkButton.addEventListener('click', addBookmark); }
    if (notesClose) { notesClose.addEventListener('click', closeAnnotationPanel); }
    if (notesExport) { notesExport.addEventListener('click', exportAnnotations); }

    /* 四个标记按钮：同一个动作，只是样式不同。再点一次同款 = 取消。 */
    if (selHighlight) { selHighlight.addEventListener('click', function () { applyMark(0); }); }
    if (selUnderline) { selUnderline.addEventListener('click', function () { applyMark(1); }); }
    if (selWavy) { selWavy.addEventListener('click', function () { applyMark(2); }); }
    if (selStrike) { selStrike.addEventListener('click', function () { applyMark(3); }); }

    /* 弹条上的「加笔记」打开完整笔记面板（颜色/样式/备注都在里面）。 */
    if (selNote) { selNote.addEventListener('click', openNotePanelDraft); }
    if (selCopy) { selCopy.addEventListener('click', copySelection); }
    if (selFind) { selFind.addEventListener('click', findSelection); }

    /* 点已标记文字弹出的笔记面板。 */
    if (cardSave) { cardSave.addEventListener('click', saveNoteCard); }
    if (cardClose) { cardClose.addEventListener('click', closeNoteCard); }
    if (cardDelete) { cardDelete.addEventListener('click', deleteNoteCard); }

    /* 面板里的备注框：Ctrl+Enter 保存、Esc 关闭（与弹条同一套手感）。 */
    var cardText = document.getElementById('noteCardText');

    if (cardText) {
      cardText.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
          event.preventDefault();
          saveNoteCard();
        } else if (event.key === 'Escape') {
          event.preventDefault();
          closeNoteCard();
        }
      });
    }

    /* 弹条 / 卡片 / 面板里的事件不要冒泡给外壳手势（滚轮翻页、拖拽）。 */
    [document.getElementById('selBar'),
      document.getElementById('noteCard'),
      document.getElementById('notesPanel')].forEach(function (el) {
      if (!el) { return; }

      ['mousedown', 'pointerdown', 'touchstart', 'wheel'].forEach(function (name) {
        el.addEventListener(name, function (event) { event.stopPropagation(); }, { passive: false });
      });
    });

    /* EPUB 的选区在 iframe 里（由 attachToFrame 逐章挂）；PDF 的选区在外壳文档里。 */
    document.addEventListener('mousedown', onShellMouseDown);
    document.addEventListener('mouseup', onShellMouseUp);
    document.addEventListener('contextmenu', function (event) {
      if (!isPdf()) { return; }
      onContentContextMenu(event);
    });
  }

  // ---------- 启动 ----------

  document.addEventListener('DOMContentLoaded', function () {
    frame = document.getElementById('chapter');

    frame.addEventListener('load', onFrameLoad);
    document.getElementById('btnPrev').addEventListener('click', function () {
      pageOrChapter(-1);
    });
    document.getElementById('btnNext').addEventListener('click', function () {
      pageOrChapter(1);
    });

    /* 「编辑本章」只绑一次（原因见 updateButtons 里的说明）：开多少章都只发一条消息 */
    var editChapterButton = document.getElementById('btnEditChapter');

    if (editChapterButton) {
      editChapterButton.addEventListener('click', function () {
        post('reader.editChapter', { index: state.index });
        /* TXT 没有"章节文件"：打开的是整本 txt 的全文编辑，只是光标会跳到本章标题行（用户 2026-10-20 要求）。 */
        notice(state.kind === 'txt'
          ? '正在打开整本 TXT 的全文编辑（光标会跳到本章标题）...'
          : '正在打开这一章的可视编辑...');
      });
    }

    var btnMode = document.getElementById('btnMode');
    if (btnMode) {
      btnMode.addEventListener('click', function () {
        setMode(isPaged() ? 'scroll' : 'paged');
      });
    }

    // 外壳自己也要收键盘：焦点在工具栏按钮上时事件不会进 iframe
    var themeSelect = document.getElementById('themeSelect');

    if (themeSelect) {
      themeSelect.addEventListener('change', function () {
        applyTheme(themeSelect.value);
      });
    }

var btnType = document.getElementById('btnType');

    if (btnType) {
      btnType.addEventListener('click', function () {
        var panel = document.getElementById('typePanel');

        if (panel) { panel.hidden = !panel.hidden; }

        if (panel && !panel.hidden) { closeTocPanel(); }
      });
    }

    var btnToc = document.getElementById('btnToc');

    if (btnToc) {
      btnToc.addEventListener('click', function () { toggleTocPanel(); });
    }

    var encodingSelect = document.getElementById('encodingSelect');

    if (encodingSelect) {
      encodingSelect.addEventListener('change', function () {
        /* 只发意图：真正换编码、重建缓存、重新下发 load 都是宿主的活 */
        post('reader.textEncodingChanged', { encoding: encodingSelect.value });
      });
    }

    // T4-6：底部状态条的两颗按钮（翻章，不翻页）
    var statusPrev = document.getElementById('statusPrev');
    var statusNext = document.getElementById('statusNext');

    if (statusPrev) {
      statusPrev.addEventListener('click', function () { gotoRelative(-1); });
    }

    if (statusNext) {
      statusNext.addEventListener('click', function () { gotoRelative(1); });
    }

    // T4-6：跳转面板（Ctrl+G）
    var gotoRange = document.getElementById('gotoRange');
    var gotoGo = document.getElementById('gotoGo');
    var gotoClose = document.getElementById('gotoClose');

    if (gotoRange) {
      gotoRange.addEventListener('input', function () {
        var value = document.getElementById('gotoValue');

        if (value) {
          value.textContent = String(gotoRange.value);
        }
      });
    }

    if (gotoGo) {
      gotoGo.addEventListener('click', function () {
        var range = document.getElementById('gotoRange');

        toggleGotoPanel(false);
        gotoBookPercent(range ? Number(range.value) : 0);
      });
    }

    if (gotoClose) {
      gotoClose.addEventListener('click', function () { toggleGotoPanel(false); });
    }

    // T4-6：章内查找（Ctrl+F）
    var findInput = document.getElementById('findInput');
    var findPrev = document.getElementById('findPrev');
    var findNext = document.getElementById('findNext');
    var findClose = document.getElementById('findClose');

    if (findInput) {
      findInput.addEventListener('input', function () { runFind(findInput.value || ''); });
    }

    if (findPrev) {
      findPrev.addEventListener('click', function () { findStep(false); });
    }

    if (findNext) {
      findNext.addEventListener('click', function () { findStep(true); });
    }

    if (findClose) {
      findClose.addEventListener('click', function () { closeFindBar(); });
    }

    // T5-3：阅读器工具栏里的"查找替换"
    var frFind = document.getElementById('frFind');
    var frPrev = document.getElementById('frPrev');
    var frNext = document.getElementById('frNext');
    var frReplaceOneButton = document.getElementById('frReplaceOne');
    var frReplaceAllButton = document.getElementById('frReplaceAll');
    var frClose = document.getElementById('frClose');
    var frUndoReplace = document.getElementById('frUndoReplace');
    var btnFindReplace = document.getElementById('btnFindReplace');

    if (btnFindReplace) {
      btnFindReplace.addEventListener('click', function () { openFindReplaceBar(); });
    }

    if (frFind) {
      frFind.addEventListener('input', function () { frHighlight(); });
    }

    if (frPrev) {
      frPrev.addEventListener('click', function () { frStep(false); });
    }

    if (frNext) {
      frNext.addEventListener('click', function () { frStep(true); });
    }

    if (frReplaceOneButton) {
      frReplaceOneButton.addEventListener('click', function () { frReplaceOne(); });
    }

    if (frReplaceAllButton) {
      frReplaceAllButton.addEventListener('click', function () { frPreview(); });
    }

    if (frClose) {
      frClose.addEventListener('click', function () { closeFindReplaceBar(); });

    if (frUndoReplace) {
      // T5-4：撤销上一次替换（宿主按「全部替换前」的快照把工作区还原回来）
      frUndoReplace.addEventListener('click', function () { post('reader.findReplace.undo', {}); });
    }
    }

    ['frRegex', 'frCase', 'frScope'].forEach(function (id) {
      var control = document.getElementById(id);

      if (control) {
        control.addEventListener('change', function () { frHighlight(); });
      }
    });

    // T4-6：鼠标移到顶部浮出工具栏（只在全屏模式生效）
    document.addEventListener('mousemove', onShellMouseMove);

    var tocClose = document.getElementById('tocClose');

    if (tocClose) {
      tocClose.addEventListener('click', function () { closeTocPanel(); });
    }

    /* 目录面板里的事件不要冒泡给外壳（外壳上有滚轮翻页、拖拽等全局手势） */
    var tocPanel = document.getElementById('tocPanel');

    if (tocPanel) {
      ['mousedown', 'pointerdown', 'touchstart', 'wheel'].forEach(function (name) {
        tocPanel.addEventListener(name, function (event) { event.stopPropagation(); }, { passive: false });
      });
    }

    /* 拖动滑块时，值先同步到数字框再统一读取；反之亦然。
       （上一版统一去读数字框，滑块拖动时数字框还是旧值 -> 把旧值又应用回去，看起来"拖不动"。） */
    ['tyFontSize', 'tyLineHeight', 'tyMarginTop', 'tyMarginBottom', 'tyMarginLeft', 'tyMarginRight',
      'tyParaSpacing', 'tyTextIndent', 'tyColumnGap'].forEach(function (id) {
      var range = document.getElementById(id);
      var number = document.getElementById(id + 'Num');

      if (range) {
        range.addEventListener('input', function () {
          if (number) { number.value = range.value; }
          pushTypographyFromUi();
        });
      }

      if (number) {
        number.addEventListener('input', function () {
          if (range) { range.value = number.value; }
          pushTypographyFromUi();
        });
      }
    });

    ['tyColumns', 'tyFontFamily'].forEach(function (id) {
      var input = document.getElementById(id);

      if (input) { input.addEventListener('change', pushTypographyFromUi); }
    });

    var gapRule = document.getElementById('tyColumnRule');

    if (gapRule) { gapRule.addEventListener('change', pushTypographyFromUi); }

    /* 面板内的事件不要冒泡给外壳（外壳上有滚轮翻页、拖拽等全局手势，
       否则滑块会被抢走，拖不动）。 */
    var panel = document.getElementById('typePanel');

    if (panel) {
      ['mousedown', 'pointerdown', 'touchstart', 'wheel'].forEach(function (name) {
        panel.addEventListener(name, function (event) { event.stopPropagation(); }, { passive: false });
      });
    }

    updateTypographyUi();

    ['customBg', 'customFg', 'customLink'].forEach(function (id) {
      var input = document.getElementById(id);

      if (input) {
        input.addEventListener('input', function () {
          setCustomColors(
            document.getElementById('customBg').value,
            document.getElementById('customFg').value,
            document.getElementById('customLink').value);
        });
      }
    });

    /* T7-3：宿主在地址栏里带 ?theme=xxx。首帧就按它渲染，避免深色用户看到一闪而过的白底。 */
    try {
      var initialTheme = new URLSearchParams(location.search).get('theme');
      if (initialTheme && THEMES.some(function (t) { return t.id === initialTheme; })) {
        state.theme.id = initialTheme;
      } else if (initialTheme === 'custom') {
        state.theme.id = 'custom';
      }
    } catch (e) { /* 老浏览器不支持 URLSearchParams：退回默认 light */ }

    applyTheme(currentThemeId(), false);

    // T6-2：PDF 工具栏的按钮装配。EPUB 模式下这些按钮不显示，装了也无害。
    wirePdfBar();

    // 目录抽屉的「编辑目录」（EPUB 才有意义，但绑定对其它格式无害）。
    wireTocEdit();

    // T6-5：书签 / 笔记的入口与弹条。
    wireAnnotations();

    // 2026-10-21：标题行「第 12/14 章　第 1/4 页」里的两个可输入框（回车 / 点别处就跳）。
    wireMetaJump();

    document.addEventListener('keydown', onKeyDown);
    window.addEventListener('resize', onViewportResize);

    /* 只给宿主与冒烟读的探针：除了 setMode / pageOrChapter（就是用户能做的两件事），
       其余都是只读。放在这里而不是散落各处，是为了让"测试能不能验"这件事一目了然。 */
    window.__readerDebug = {
      mode: function () { return state.mode; },
      pageIndex: function () { return currentPageIndex(); },
      pageCount: function () { return measurePageCount(); },
      chapterIndex: function () { return state.index; },

      /* 2026-10-21：标题行「第 12/14 章　第 1/4 页」的可断言状态。
         text 是"人眼看到的那一行"（input 的值不在 textContent 里，必须自己拼）；
         chapterFits / pageFits 是"数字有没有被框裁掉"的实测（canvas 量文字宽 vs 框内可用宽）。
         setChapterInput / setPageInput 与用户"在框里打字"等价（提交仍走 blur / Enter 那条真路）。 */
      chapterMetaState: function () {
        var chapter = metaJumpInput('chapterJumpInput');
        var page = metaJumpInput('pageJumpInput');
        var seg = document.getElementById('chapterMetaPage');
        var chapterTotal = document.getElementById('chapterTotal');
        var pageTotal = document.getElementById('pageTotal');
        var box = chapter ? chapter.getBoundingClientRect() : null;
        var cs = chapter ? getComputedStyle(chapter) : null;
        var pageVisible = !!(seg && !seg.hidden && getComputedStyle(seg).display !== 'none');
        var chapterValue = chapter ? String(chapter.value) : '';
        var pageValue = page ? String(page.value) : '';
        var chapterTotalText = chapterTotal ? String(chapterTotal.textContent || '') : '';
        var pageTotalText = pageTotal ? String(pageTotal.textContent || '') : '';

        return {
          text: '第 ' + chapterValue + '/' + chapterTotalText + ' 章'
            + (pageVisible ? '　第 ' + pageValue + '/' + pageTotalText + ' 页' : ''),
          chapterValue: chapterValue,
          pageValue: pageValue,
          chapterTotal: chapterTotalText,
          pageTotal: pageTotalText,
          pageVisible: pageVisible,
          chapterFits: metaValueFits(chapter),
          pageFits: metaValueFits(page),
          width: box ? Math.round(box.width * 10) / 10 : 0,
          height: box ? Math.round(box.height * 10) / 10 : 0,
          fontSize: cs ? cs.fontSize : ''
        };
      },
      /* 用户 2026-10-21：自定义主题下"框看不看得见"的实测 —— 读实际算出来的边框色，
         和底色算对比度（>= 1.3 才谈得上看得见；边框和底色完全相同就是自定义主题的老 bug）。 */
      chromeProbe: function () {
        var bg = getComputedStyle(document.body).backgroundColor || 'rgb(0,0,0)';
        var select = document.getElementById('themeSelect');
        var swatch = document.getElementById('customBg');
        var jump = metaJumpInput('chapterJumpInput');
        var pick = function (el, prop) { return el ? getComputedStyle(el)[prop] : ''; };
        var round = function (v) { return Math.round(v * 100) / 100; };

        return {
          theme: currentThemeId(),
          bg: bg,
          selectBorder: pick(select, 'borderTopColor'),
          selectContrast: round(contrastRatio(pick(select, 'borderTopColor'), bg)),
          swatchBorder: pick(swatch, 'borderTopColor'),
          swatchContrast: round(contrastRatio(pick(swatch, 'borderTopColor'), bg)),
          jumpBorder: pick(jump, 'borderTopColor'),
          jumpContrast: round(contrastRatio(pick(jump, 'borderTopColor'), bg))
        };
      },

      setChapterInput: function (value) {
        var input = metaJumpInput('chapterJumpInput');

        if (!input) { return false; }
        input.value = String(value);
        return true;
      },
      setPageInput: function (value) {
        var input = metaJumpInput('pageJumpInput');

        if (!input) { return false; }
        input.value = String(value);
        return true;
      },
      /* 宽度的可断言口径：给定位数，返回输入框会用的像素宽（1 / 3 / 4 / 5 位都验一遍）。 */
      metaJumpWidthFor: function (digits) {
        var input = metaJumpInput('chapterJumpInput');

        return input ? measureMetaJumpWidth(input, Math.max(1, Number(digits) || 1)) : 0;
      },

      /* T6-2B：PDF 自绘引擎的可断言状态 + 与用户动作等价的几个口子。
         验收脚本靠它判定"旋转 / 单页-连续 / 单页-双页 / 目录 四个按钮从置灰变可用"、
         "文字层真的建起来了"、"搜索命中真的有黄底"，不去截图猜。 */
      pdfState: function () { return pdfStateForProbe(); },
      pdfGoto: function (page) { return pdfGotoForProbe(page); },
      pdfSetView: function (view) { return pdfSetViewForProbe(view); },
      pdfHasBar: function () { return pdfHasBar(); },
      pdfZoom: function (step) { return pdfZoomForProbe(step); },
      pdfToggle: function (which) { return pdfToggleForProbe(which); },
      pdfFind: function (text) { return pdfFindForProbe(text); },
      pdfFindStep: function (forward) { return pdfFindStepForProbe(forward); },
      pdfRefresh: function () { return pdfRefreshForProbe(); },
      pdfReset: function () { pdfResetView(); return pdfStateForProbe(); },
      /* 当前页文字层的字符盒数量（证明"文本可选中"这条硬判据用的是真字符，不是空层）。 */
      pdfCharNodes: function () {
        return document.querySelectorAll('#pdfPages .pdfText .pdfChar').length;
      },
      /* 当前选中的文本（笔记锚点的基础）：拖选之后冒烟用它断言能拿到字。 */
      pdfSelection: function () {
        var sel = window.getSelection ? window.getSelection() : null;

        return sel ? String(sel.toString()) : '';
      },

      /* ---------- T6-5 书签与笔记 ---------- */
      annotationState: function () {
        var panel = document.getElementById('notesPanel');
        var body = document.getElementById('notesBody');
        var hint = document.getElementById('notesHint');
        var title = document.getElementById('notesTitle');
        var add = document.getElementById('annotationsAdd');
        var doc = contentDoc();
        var counts = { highlight: 0, underline: 0, wavy: 0, strike: 0 };

        if (doc) {
          for (var m = 0; m < MARK_STYLES.length; m++) {
            var key = ['highlight', 'underline', 'wavy', 'strike'][MARK_STYLES[m].id];
            counts[key] = doc.querySelectorAll('mark.' + MARK_STYLES[m].cls).length;
          }
        }

        return {
          panelOpen: !!(panel && !panel.hidden),
          panelMode: state.panelMode,
          panelTitle: title ? String(title.textContent || '') : '',
          addBookmarkVisible: !!(add && !add.hidden),
          bookmarkCount: (state.bookmarks || []).length,
          noteCount: (state.notes || []).length,
          bookmarkLabels: (state.bookmarks || []).map(function (b) { return String(b.label || ''); }),
          noteQuotes: (state.notes || []).map(function (n) { return String(n.quote || ''); }),
          noteContents: (state.notes || []).map(function (n) { return String(n.content || ''); }),
          noteStyles: (state.notes || []).map(function (n) { return n.style || 0; }),
          noteColors: (state.notes || []).map(function (n) { return n.color || 0; }),
          noteIds: (state.notes || []).map(function (n) { return String(n.id || ''); }),
          hint: hint ? String(hint.textContent || '') : '',
          rows: body ? body.querySelectorAll('.annoItem').length : 0,
          markCounts: counts,
          noteMarks: doc ? doc.querySelectorAll('mark[data-note-id]').length : 0,
          pdfNoteMarks: document.querySelectorAll('#pdfPages .pdfChar.ets-note').length
        };
      },
      /* 打开书签页 / 笔记页（'bookmarks' | 'notes'）。 */
      openAnnotations: function (mode) { return openAnnotationPanel(mode); },
      /* 与"点弹条上的标记按钮"等价。 */
      toggleMark: function (style) { applyMark(style); return true; },
      selectionAnchor: function () {
        var shot = captureSelection();

        return shot ? { quote: shot.quote, anchor: shot.anchor } : null;
      },
      addBookmarkNow: function () { addBookmark(); return true; },
      addNoteNow: function (type, content, style) {
        var shot = state.selPending || captureSelection();

        if (!shot) {
          return false;
        }

        post('reader.addNote', {
          type: type === 1 ? 1 : 0,
          style: typeof style === 'number' ? style : 0,
          quote: shot.quote,
          content: content || null,
          anchor: shot.anchor,
          spineIndex: isPdf() ? null : state.index,
          pageIndex: isPdf() ? (state.pdf ? state.pdf.page : null) : null
        });

        return true;
      },
      notesPanel: function (open) { return toggleNotesPanel(open); },
      jumpBookmark: function (i) {
        var list = state.bookmarks || [];

        if (i < 0 || i >= list.length) {
          return false;
        }

        jumpToAnnotation('bookmark', list[i]);
        return true;
      },
      jumpNote: function (i) {
        var list = state.notes || [];

        if (i < 0 || i >= list.length) {
          return false;
        }

        jumpToAnnotation('note', list[i]);
        return true;
      },
      deleteAnnotationNow: function (kind, id) { deleteAnnotation(kind, id); return true; },
      exportAnnotationsNow: function () { exportAnnotations(); return true; },

      /* 弹条：选中后打开、点空白关闭（判据 4）。 */
      selBarState: function () {
        var bar = document.getElementById('selBar');
        var quote = document.getElementById('selQuote');

        return {
          hidden: !bar || bar.hidden,
          quote: quote ? String(quote.textContent || '') : '',
          left: bar ? bar.style.left : '',
          top: bar ? bar.style.top : ''
        };
      },
      showSelBar: function () { showSelectionBarFromCurrent(); return true; },
      hideSelBar: function () { hideSelectionBar(); return true; },
      /* 右键入口与弹条等价：直接调同一个 handler（WebView2 已关掉内核右键菜单）。 */
      contextMenuNow: function () {
        onContentContextMenu({ preventDefault: function () { } });
        return true;
      },

      /* T6-5 补短三：笔记面板（颜色/样式/备注/删除）的可断言状态与操作入口。 */
      notePanelState: function () {
        var card = document.getElementById('noteCard');
        var style = document.getElementById('noteCardStyle');
        var time = document.getElementById('noteCardTime');
        var quote = document.getElementById('noteCardQuote');
        var text = document.getElementById('noteCardText');
        var colorRow = document.getElementById('noteColorRow');
        var styleRow = document.getElementById('noteStyleRow');

        return {
          open: !!(card && !card.hidden),
          noteId: state.panelNoteId || '',
          draft: !!state.panelDraft,
          styleId: state.panelStyle,
          styleName: markNameOf(state.panelStyle),
          colorId: state.panelColor,
          styleLabel: style ? String(style.textContent || '') : '',
          time: time ? String(time.textContent || '') : '',
          quote: quote ? String(quote.textContent || '') : '',
          content: text ? String(text.value || '') : '',
          colorDots: colorRow ? colorRow.querySelectorAll('.etsColorDot').length : 0,
          styleButtons: styleRow ? styleRow.querySelectorAll('button').length : 0,
          activeStyle: styleRow
            ? (styleRow.querySelector('button.etsStyleActive')
              ? parseInt(styleRow.querySelector('button.etsStyleActive').getAttribute('data-style') || '-1', 10)
              : -1)
            : -1
        };
      },
      openNotePanelDraftNow: function () { openNotePanelDraft(); return true; },
      /* 直接按 style+color 加一条标记（绕过面板，专供"荧光降调"这类渲染断言）。 */
      addMarkNow: function (style, color) {
        var shot = state.selPending || captureSelection();

        if (!shot) {
          return false;
        }

        postMark(typeof style === 'number' ? style : 0, shot, typeof color === 'number' ? color : 0);
        return true;
      },
      notePanelPickStyle: function (id) { pickNoteStyle(id); return true; },
      notePanelPickColor: function (id) { pickNoteColor(id); return true; },
      notePanelSetText: function (text) {
        var box = document.getElementById('noteCardText');

        if (box) { box.value = String(text || ''); }

        return true;
      },
      notePanelSaveNow: function () { saveNoteCard(); return true; },
      notePanelDeleteNow: function () { deleteNoteCard(); return true; },
      closeNotePanelNow: function () { closeNoteCard(); return true; },
      /* 切主题（验"荧光下的文字色跟随主题"用）。 */
      setThemeNow: function (id) { applyTheme(id, false); return true; },

      /* 判据 3：把选区造成"跨两个段落"的样子（Range 起止在不同文本节点）。 */
      selectAcrossParagraphs: function (from, to) {
        var doc = contentDoc();

        if (!doc) {
          return null;
        }

        var ps = doc.querySelectorAll('p');

        if (ps.length < 2) {
          return null;
        }

        var a = ps[from || 0];
        var b = ps[to || 1];
        var win = frame && frame.contentWindow;
        var sel = win ? win.getSelection() : null;

        if (!a || !b || !sel) {
          return null;
        }

        var startNode = a.firstChild || a;
        var endNode = b.firstChild || b;
        var range = doc.createRange();

        range.setStart(startNode, 0);
        range.setEnd(endNode, Math.min(6, endNode.length || 0));

        sel.removeAllRanges();
        sel.addRange(range);

        return String(sel.toString() || '');
      },
      lastNotice: function () {
        var el = document.getElementById('notice');

        return el ? String(el.textContent || '') : '';
      },
      /* 点已标记文字弹出的笔记面板（判据 10；完整字段见 notePanelState）。 */
      noteCardState: function () {
        var card = document.getElementById('noteCard');
        var style = document.getElementById('noteCardStyle');
        var quote = document.getElementById('noteCardQuote');
        var text = document.getElementById('noteCardText');

        return {
          open: !!(card && !card.hidden),
          style: style ? String(style.textContent || '') : '',
          styleId: state.panelStyle,
          colorId: state.panelColor,
          quote: quote ? String(quote.textContent || '') : '',
          content: text ? String(text.value || '') : '',
          noteId: state.panelNoteId || ''
        };
      },
      closeNoteCardNow: function () { closeNoteCard(); return true; },

      /* T5-3 查找替换：冒烟要能开面板、填值、查、替换、关门。 */
      openFindReplace: function () { return openFindReplaceBar(); },
      closeFindReplace: function () { closeFindReplaceBar(); return true; },
      findReplaceBar: function () { return findReplaceBarState(); },
      findReplaceProbe: function () {
        var bar = document.getElementById('frBar');

        return {
          open: !!(bar && !bar.hidden),
          toolbarButton: !!document.getElementById('btnFindReplace'),
          find: frValue('frFind'),
          replace: frValue('frReplace'),
          scope: frScope(),
          useRegex: frChecked('frRegex'),
          matchCase: frChecked('frCase'),
          count: currentMatchCount(),
          bookTotal: state.bookTotal,
          index: state.findIndex
        };
      },
      /* 当前处与普通命中的配色（冒烟验"全部黄底、当前处红底"）。 */
      findCurrentStyle: function () {
        var marks = state.findMatches;
        var current = marks && state.findIndex >= 0 ? marks[state.findIndex] : null;
        var cs = current ? getComputedStyle(current) : null;
        var doc = contentDoc();
        var normalCss = '';

        for (var i = 0; marks && i < marks.length; i++) {
          if (i !== state.findIndex) {
            normalCss = getComputedStyle(marks[i]).backgroundColor;
            break;
          }
        }

        if (!normalCss && doc && doc.body) {
          /* 只剩一处命中时没有"普通命中"可采样：临时造一个同 class 的元素读它的颜色，读完删掉。 */
          var probe = doc.createElement('mark');

          probe.className = 'ets-find';
          probe.textContent = 'x';
          probe.style.position = 'absolute';
          probe.style.visibility = 'hidden';
          doc.body.appendChild(probe);
          normalCss = getComputedStyle(probe).backgroundColor;
          doc.body.removeChild(probe);
        }

        var rgb = cs ? (cs.backgroundColor.match(/(\d+),\s*(\d+),\s*(\d+)/) || null) : null;

        return {
          current: cs ? cs.backgroundColor : '',
          normal: normalCss,
          distinct: !!cs && !!normalCss && cs.backgroundColor !== normalCss,
          /* 收紧过的"是不是真的红"：红通道高、绿蓝都低（黄底 #FFE066 会判 false）。 */
          redDominant: !!rgb && Number(rgb[1]) >= 180 && Number(rgb[2]) <= 120 && Number(rgb[3]) <= 120
        };
      },
      /* 当前命中处的位置信息（冒烟验"替换后是否真的定位到该处"用）。 */
      findCurrentPosition: function () {
        var marks = state.findMatches;
        var mark = marks && state.findIndex >= 0 ? marks[state.findIndex] : null;

        var rect = mark ? mark.getBoundingClientRect() : null;
        var win = contentWin();
        var height = win ? win.innerHeight : 0;

        return {
          index: state.findIndex,
          count: marks ? marks.length : 0,
          pageIndex: currentPageIndex(),
          markPage: mark ? pageOfElement(mark) : -1,
          top: rect ? Math.round(rect.top) : 0,
          innerHeight: height,
          visible: !!rect && rect.top >= -8 && rect.top <= (height - 8)
        };
      },
      /* DOM 里真实存在的命中标记数（冒烟验"替换后当页是否重新高亮"用）。 */
      findMarkCount: function () {
        var doc = contentDoc();

        return doc ? doc.querySelectorAll('mark.ets-find').length : -1;
      },
      findReplaceSet: function (find, replace, scope, useRegex, matchCase) {
        var set = function (id, value) { var el = document.getElementById(id); if (el) { el.value = value; } };

        set('frFind', find || '');
        set('frReplace', replace || '');

        var scopeEl = document.getElementById('frScope');
        if (scopeEl && scope) { scopeEl.value = scope; }

        var regexEl = document.getElementById('frRegex');
        if (regexEl) { regexEl.checked = !!useRegex; }

        var caseEl = document.getElementById('frCase');
        if (caseEl) { caseEl.checked = !!matchCase; }

        return frHighlight();
      },
      findReplaceStep: function (forward) { return frStep(!!forward); },
      findReplaceOne: function () { return frReplaceOne(); },
      findReplaceAll: function () { return frPreview(); },
      findReplaceUndo: function () { post('reader.findReplace.undo', {}); return true; },
      /* 一页几何与页框（冒烟要用来证明"一页就是一页"） */
      pageStride: function () { return pageMetrics().width; },
      pageHeight: function () { return pageMetrics().height; },
      marginLeft: function () { return pageMetrics().left; },
      marginTop: function () { return pageMetrics().top; },
      frameInfo: function () {
        var frame = frameElement();
        var flow = flowElement();

        if (!frame || !flow) {
          return { exists: false, width: 0, height: 0, left: 0, top: 0, stripWidth: 0, columns: 0 };
        }

        var rect = frame.getBoundingClientRect();

        return {
          exists: true,
          width: Math.round(rect.width),
          height: Math.round(rect.height),
          left: Math.round(rect.left),
          top: Math.round(rect.top),
          stripWidth: Math.round(flow.scrollWidth || 0),
          columns: Math.round((flow.scrollWidth || 0) / Math.max(1, pageMetrics().width))
        };
      },
      offsetXNow: function () {
        var flow = flowElement();

        if (!flow) {
          return 0;
        }

        var matched = /translateX\((-?[\d.]+)px\)/.exec(flow.style.transform || '');
        return matched ? Math.abs(Math.round(parseFloat(matched[1]))) : 0;
      },
      percent: function () { return capturePercentInChapter(); },
      paginateMs: function () { return state.lastPaginateMs; },
      hasPagedCss: function () { return !!contentDoc() && !!contentDoc().getElementById(PAGED_STYLE_ID); },
      pagedCssText: function () {
        var doc = contentDoc();
        var style = doc && doc.getElementById(PAGED_STYLE_ID);
        return style ? style.textContent : '';
      },

      /* 当前页有几行、最深的行底离页顶多远（用来证明"最后一行不会被切掉"）。 */
      linesOnPage: function () { return pageLineStats().lines; },
      deepestLineBottom: function () { return pageLineStats().deepest; },
      pageDiag: function () {
        var s = pageLineStats();
        var flow = flowElement();

        var metrics = pageMetrics();

        return {
          deepestX: s.deepestX,
          deepestTop: s.deepestTop,
          deepestText: s.deepestText,
          clientHeight: s.clientHeight,
          scrollHeight: s.scrollHeight,
          lineBottom: s.lineBottom,
          medianLineHeight: s.medianLineHeight,
          oversizeRects: s.oversizeRects,
          /* 用户口径：只看文字行有没有越过一页底（超高图片/表格不算），1px 容差防亚像素假越界 */
          textLinesInsidePage: s.lineBottom <= metrics.height + 1,
          /* 列高与流高都等于一页高 => 每列正好一页，超出的行会进下一列，
             页框再按这个高度裁剪，所以不可能出现"最后一行只显示一半"。 */
          columnsClippedToOnePage: s.clientHeight === metrics.height && s.scrollHeight === metrics.height,
          flowHeightCss: flow ? (flow.style.height || '(空)') : '(无流)',
          flowColumns: flow ? getComputedStyle(flow).columnWidth : ''
        };
      },

      maxLineWidth: function () {
        var doc = contentDoc();

        if (!doc || !doc.body) {
          return 0;
        }

        var widest = 0;
        var range = doc.createRange();
        var walker = doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT, null);
        var node;

        while ((node = walker.nextNode())) {
          if (!node.nodeValue || !node.nodeValue.trim()) {
            continue;
          }

          try {
            range.selectNodeContents(node);
          } catch (ignored) {
            continue;
          }

          var rects = range.getClientRects();

          for (var i = 0; i < rects.length; i++) {
            if (rects[i].width > widest) {
              widest = rects[i].width;
            }
          }
        }

        return Math.round(widest);
      },

      /* T7-3：读工具栏布局（冒烟校验「上一页/下一页」永远在首行最左/最右，窄窗口折行也一样）。 */
      toolbarRowsProbe: function () {
        var bar = document.getElementById('toolbar');
        var prev = document.getElementById('btnPrev');
        var next = document.getElementById('btnNext');

        if (!bar || !prev || !next) { return null; }

        function rect(el) {
          var b = el.getBoundingClientRect();

          return {
            left: Math.round(b.left), right: Math.round(b.right),
            top: Math.round(b.top), bottom: Math.round(b.bottom),
            width: Math.round(b.width), height: Math.round(b.height)
          };
        }

        var items = Array.prototype.slice.call(bar.querySelectorAll('button, select, input, textarea'));
        var visible = items.filter(function (el) {
          var r = el.getBoundingClientRect();

          return r.width > 0 && r.height > 0
            && getComputedStyle(el).display !== 'none'
            && getComputedStyle(el).visibility !== 'hidden';
        });
        var rects = visible.map(rect);
        var minLeft = Math.min.apply(null, rects.map(function (r) { return r.left; }));
        var maxRight = Math.max.apply(null, rects.map(function (r) { return r.right; }));
        var firstRowTop = Math.min.apply(null, rects.map(function (r) { return r.top; }));
        var barRect = bar.getBoundingClientRect();

        return {
          paged: isPaged(),
          rows: document.body.classList.contains('ets-epub-rows'),
          /* 一行（宽到放得下）还是两行；阈值 = 合并成一行所需的工具栏宽度（px）。 */
          wide: document.body.classList.contains('ets-epub-wide'),
          threshold: epubOneRowThreshold(),
          available: Math.round(barRect.width),
          prevText: prev.textContent,
          nextText: next.textContent,
          prev: rect(prev),
          next: rect(next),
          minLeft: minLeft,
          maxRight: maxRight,
          firstRowTop: firstRowTop,
          buttons: rects.length
        };
      },
      /* 给 T4-2 冒烟用：读外壳 + 章节 iframe 的真实配色。 */
      themeProbe: function () {
        var doc = contentDoc();
        var body = doc && doc.body ? doc.body : null;
        var img = doc ? doc.querySelector('img') : null;
        var link = doc ? doc.querySelector('a') : null;
        var selector = document.getElementById('themeSelect');

        return {
          id: currentThemeId(),
          dataTheme: document.documentElement.getAttribute('data-theme') || '',
          selectValue: selector ? selector.value : '',
          shellBgHex: cssColorToHex(getComputedStyle(document.body).backgroundColor),
          shellVarBg: getComputedStyle(document.documentElement).getPropertyValue('--shell-bg').trim(),
          chapterBgHex: body ? cssColorToHex(getComputedStyle(body).backgroundColor) : '',
          chapterFgHex: body ? cssColorToHex(getComputedStyle(body).color) : '',
          linkHex: link ? cssColorToHex(getComputedStyle(link).color) : '',
          imageCount: doc ? doc.querySelectorAll('img').length : 0,
          imageFilter: img ? (getComputedStyle(img).filter || 'none') : 'none',
          /* 主题样式是不是 head 里的最后一个（判据 2 的保证） */
          lastStyleId: doc && doc.head && doc.head.lastElementChild
            ? (doc.head.lastElementChild.id || doc.head.lastElementChild.tagName)
            : ''
        };
      },
      setTheme: function (id) { applyTheme(id, false); return currentThemeId(); },
      /* 模拟用户改自定义主题的颜色：走取色器的 input 事件（会回报宿主落盘） */
      customPick: function (bg, fg, link) {
        var set = function (id, value) {
          var input = document.getElementById(id);
          if (input && value) {
            input.value = value;
            input.dispatchEvent(new Event('input', { bubbles: true }));
          }
        };

        set('customBg', bg);
        set('customFg', fg);
        set('customLink', link);
        return currentThemeId();
      },
customColors: function () { return state.custom; },
      /* 排版设置：走和界面一样的 applyTypography（会回报宿主落盘） */
typographyPick: function (values) { applyTypography(values || {}); return state.typography; },
      /* 真正会被注入到章节里的正文 CSS 文本（出异常也把错误原文带回来，方便定位） */
      typographyCss: function () {
        try { return String(buildContentCss()); } catch (e) { return 'ERR: ' + (e && e.message ? e.message : e); }
      },
      typographyProbe: function () {
        var y = state.typography || {};
        var css = '';

        try { css = String(buildContentCss()); } catch (e) { css = ''; }

        var options = [];
        var sel = document.getElementById('tyFontFamily');

        if (sel) { for (var i = 0; i < sel.options.length; i++) { options.push(sel.options[i].value); } }

        var m = contentMargins();
        var computed = [];

        /* 真样式：直接量章节 iframe 里 body / 段落上算出来的值。
           上一版用"CSS 文本里有没有这句"判断，结果把"规则被塞进 html, body {} 被解析器丢掉"
           的 bug 判成了绿  这里改成量真值。 */
        try {
          var cdoc = contentDoc();
          var cwin = contentWin();

          if (cdoc && cwin && cdoc.body) {
            var bodyStyle = cwin.getComputedStyle(cdoc.body);
            var para = cdoc.body.querySelector('p') || cdoc.querySelector('p');
            var paraStyle = para ? cwin.getComputedStyle(para) : null;

            computed = [
              String(bodyStyle.columnCount || ''),
              String(bodyStyle.paddingTop || ''),
              String(bodyStyle.paddingLeft || ''),
              (function () {
                var flowEl = cdoc.getElementById('ets-flow');
                var flowStyle = flowEl ? cwin.getComputedStyle(flowEl) : null;
                return flowStyle ? String(flowStyle.columnWidth || '') : '';
              })(),
              (function () {
                var flowEl = cdoc.getElementById('ets-flow');
                var flowStyle = flowEl ? cwin.getComputedStyle(flowEl) : null;
                return flowStyle ? String(flowStyle.columnCount || '') : '';
              })(),
              (function () {
                var mm = pageMetrics();
                var gg = mm.gap;
                return 'fits=' + Math.floor((mm.width + gg) / (mm.columnWidth + gg));
              })(),
              (function () {
                var de = cdoc.documentElement;
                var b = cdoc.body;
                var sw = Math.max(b ? b.scrollWidth : 0, de ? de.scrollWidth : 0);
                var cw = de ? (de.clientWidth || 0) : 0;
                return 'overflow=' + (sw - cw) + 'px(sw=' + sw + ',cw=' + cw + ')';
              })(),
              (function () {
                var ps = cdoc.getElementById('ets-paged-css');
                var text = ps ? String(ps.textContent || '') : '(no-paged-style)';
                var m = /column-width:([^;]*);/.exec(text);
                return m ? ('column-width=' + m[1]) : ('len=' + text.length + ' ' + text.slice(0, 120));
              })(),
              paraStyle ? String(paraStyle.marginBottom || '') : '',
              paraStyle ? String(paraStyle.textIndent || '') : ''
            ];
          }
        } catch (e) {
          computed = ['ERR: ' + (e && e.message ? e.message : e)];
        }

        return {
          fontSize: y.fontSize, lineHeight: y.lineHeight, fontFamily: y.fontFamily,
          marginTop: marginPctOf(y, 'Top'), marginBottom: marginPctOf(y, 'Bottom'),
          marginLeft: marginPctOf(y, 'Left'), marginRight: marginPctOf(y, 'Right'),
          marginPx: m.left, marginTopPx: m.top, computed: computed,
          paraSpacing: paraSpacingOf(y), textIndent: textIndentOf(y),
          textAlign: textAlignOf(y), columns: columnsOf(y),
          computedTextAlign: css.indexOf('text-align: ' + textAlignOf(y)) >= 0 ? textAlignOf(y) : '',
          columnGap: columnGapPxOf(y), columnRule: String(columnRuleOn(y)),
          pageStride: pageStride(), columnWidth: pageMetrics().columnWidth, pageWidth: pageMetrics().width,
          pageIndex: currentPageIndex(), pageCount: state.pageCount, percentInChapter: capturePercentInChapter(),
          computedColumnCount: '',
          fontOptions: options
        };
      },
      /* 模拟用户在下拉里选主题：走 change 事件  会回报宿主  落盘（判据 4） */
      themePick: function (id) {
        var selector = document.getElementById('themeSelect');

        if (!selector) {
          return currentThemeId();
        }

        selector.value = id;
        selector.dispatchEvent(new Event('change', { bubbles: true }));
        return currentThemeId();
      },
      /* T4-4 目录：给冒烟读/点的口子（tocPick 派发的是真 click，和用户点一样） */
      /* T4-6：交互状态（状态条 / 全屏 / 跳转 / 查找 / tooltip） */
      interactionProbe: function () {
        var chapter = document.getElementById('statusChapter');
        var percent = document.getElementById('statusPercent');
        var statusBar = document.getElementById('statusBar');
        var statusPrev = document.getElementById('statusPrev');
        var statusNext = document.getElementById('statusNext');
        var gotoPanel = document.getElementById('gotoPanel');
        var findBar = document.getElementById('findBar');

        return {
          fullscreen: !!state.fullscreen,
          fullscreenClass: document.body.classList.contains('ets-fullscreen'),
          toolbarPeek: document.body.classList.contains('ets-toolbar-peek'),
          statusVisible: !!(statusBar && getComputedStyle(statusBar).display !== 'none'),
          statusChapter: chapter ? String(chapter.textContent || '') : '',
          statusPercent: percent ? String(percent.textContent || '') : '',
          statusPrevDisabled: statusPrev ? !!statusPrev.disabled : true,
          statusNextDisabled: statusNext ? !!statusNext.disabled : true,
          gotoOpen: !!(gotoPanel && !gotoPanel.hidden),
          gotoValue: document.getElementById('gotoRange')
            ? String(document.getElementById('gotoRange').value)
            : '',
          findOpen: !!(findBar && !findBar.hidden),
          findReplaceOpen: !!(document.getElementById('frBar') && !document.getElementById('frBar').hidden),
          findCount: currentMatchCount(),
          findIndex: state.findIndex,
          findText: state.findText || '',
          fontSize: Number((state.typography || {}).fontSize) || 0,
          mode: state.mode || 'scroll',
          chapterIndex: state.index
        };
      },
      setFullscreen: function (on) { return toggleFullscreen(!!on); },
      gotoPercent: function (percent) { return gotoBookPercent(Number(percent)); },
      find: function (text) { return runFind(String(text === undefined ? '' : text)); },
      findNext: function () { return findStep(true); },
      findPrev: function () { return findStep(false); },
      closeFind: function () { closeFindBar(); return true; },
      escape: function () { return escapeStep(); },
      mouseTop: function () {
        document.body.classList.add('ets-toolbar-peek');

        return document.body.classList.contains('ets-toolbar-peek');
      },
      toolbarTitles: function () {
        var nodes = document.querySelectorAll('#toolbar button, #toolbar select, #statusBar button');
        var out = [];

        for (var i = 0; i < nodes.length; i++) {
          out.push(String(nodes[i].id || nodes[i].tagName) + '=' + String(nodes[i].getAttribute('title') || ''));
        }

        return out;
      },

      /* T4-5：编码选择器的状态（冒烟读） */
      textProbe: function () {
        var select = document.getElementById('encodingSelect');
        var hint = document.getElementById('encodingHint');
        var options = [];

        if (select) {
          for (var i = 0; i < select.options.length; i++) {
            options.push(select.options[i].value);
          }
        }

        return {
          visible: !!(select && !select.hidden),
          value: select ? String(select.value || '') : '',
          options: options,
          hint: hint ? String(hint.textContent || '') : '',
          preview: select ? String(select.title || '') : ''
        };
      },
      /* 模拟用户在下拉里选编码：走真实 change 事件（会回报宿主 -> 重建缓存） */
      textPick: function (name) {
        var select = document.getElementById('encodingSelect');

        if (!select) {
          return '';
        }

        select.value = name;
        select.dispatchEvent(new Event('change', { bubbles: true }));
        return String(select.value || '');
      },
      tocProbe: function () {
        var panel = document.getElementById('tocPanel');
        var items = document.querySelectorAll('#tocList .tocItem');
        var titles = [];
        var levels = [];
        var spines = [];
        var fragments = [];
        var indents = [];

        for (var i = 0; i < items.length; i++) {
          titles.push(String(items[i].textContent || ''));
          levels.push(parseInt(items[i].getAttribute('data-level') || '0', 10));
          spines.push(parseInt(items[i].getAttribute('data-spine') || '-1', 10));
          fragments.push(items[i].getAttribute('data-fragment') || '');
          indents.push(items[i].style.paddingLeft || '');
        }

        var current = document.querySelector('#tocList .tocCurrent');
        var hint = document.getElementById('tocHint');

        return {
          open: !!(panel && !panel.hidden),
          count: items.length,
          titles: titles,
          levels: levels,
          spines: spines,
          fragments: fragments,
          indents: indents,
          hasToc: !!(state.manifest && state.manifest.toc && state.manifest.toc.length),
          fallback: !!state.tocFallback,
          hint: hint ? String(hint.textContent || '') : '',
          currentSpine: current ? parseInt(current.getAttribute('data-spine') || '-1', 10) : -1,
          currentTitle: current ? String(current.textContent || '') : '',
          pendingFragment: state.pendingFragment || ''
        };
      },
      /* 某个锚点现在落在哪：给"带 fragment 跳到章内位置"当判据（T4-4 判据 3） */
      anchorInfo: function (fragment) {
        var doc = contentDoc();
        var frame = frameElement();
        var target = null;

        if (doc && fragment) {
          try {
            target = doc.getElementById(fragment) || (doc.getElementsByName(fragment) || [])[0] || null;
          } catch (ignored) {
            target = null;
          }
        }

        if (!target || !target.getBoundingClientRect) {
          return { found: false, pageIndex: currentPageIndex(), onPage: false, text: '', percent: 0,
            rectLeft: 0, frameLeft: 0, frameRight: 0, stride: Math.round(pageStride()) };
        }

        var rect = target.getBoundingClientRect();
        var frameRect = frame ? frame.getBoundingClientRect() : null;

        return {
          found: true,
          pageIndex: currentPageIndex(),
          onPage: !!frameRect && rect.left >= frameRect.left - 2 && rect.left <= frameRect.right + 2,
          text: String(target.textContent || '').replace(/\s+/g, ' ').slice(0, 40),
          percent: capturePercentInChapter(),
          rectLeft: Math.round(rect.left),
          frameLeft: frameRect ? Math.round(frameRect.left) : 0,
          frameRight: frameRect ? Math.round(frameRect.right) : 0,
          stride: Math.round(pageStride())
        };
      },
      tocToggle: function (open) { return toggleTocPanel(typeof open === 'boolean' ? open : undefined); },
      tocPick: function (n) {
        var items = document.querySelectorAll('#tocList .tocItem');

        if (items.length === 0) {
          return false;
        }

        var index = Math.max(0, Math.min(items.length - 1, n || 0));

        items[index].dispatchEvent(new Event('click', { bubbles: true }));
        return true;
      },
      /* 直接跳某章某锚点（验"带 fragment 跳到章内位置"用） */
      tocJump: function (spine, fragment) {
        return jumpToTocEntry({ spineIndex: spine, fragment: fragment || null });
      },
      setMode: function (mode) { setMode(mode, false); return state.mode; },
      pageOrChapter: function (delta) { pageOrChapter(delta); return currentPageIndex(); },

      pressKey: function (key, where, modifiers) {
        /* T4-6：第三参是修饰键组合（'ctrl' / 'alt' / 'meta' / 'shift'，可组合），
           用来验"Ctrl+ 翻章"以及"Alt/Win 组合绝不被我们接管"。 */
        var flags = String(modifiers || '');
        var options = {
          key: key,
          bubbles: true,
          cancelable: true,
          ctrlKey: flags.indexOf('ctrl') >= 0,
          altKey: flags.indexOf('alt') >= 0,
          metaKey: flags.indexOf('meta') >= 0,
          shiftKey: flags.indexOf('shift') >= 0
        };

        if (where === 'shell') {
          document.dispatchEvent(new KeyboardEvent('keydown', options));
          return currentPageIndex();
        }

        var doc = contentDoc();

        if (doc && doc.defaultView && doc.defaultView.KeyboardEvent) {
          try { doc.dispatchEvent(new doc.defaultView.KeyboardEvent('keydown', options)); } catch (ignored) { }
        }

        return currentPageIndex();
      },

      spinWheel: function (deltaY) {
        var win = contentWin();

        if (!win || !win.WheelEvent) {
          return currentPageIndex();
        }

        try { win.dispatchEvent(new win.WheelEvent('wheel', { deltaY: deltaY, bubbles: true, cancelable: true })); } catch (ignored) { }
        return currentPageIndex();
      }
    };

    updateModeUi();

    post('reader.ready', {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: ['scroll', 'paged'],
      mode: state.mode
    });
  });
})();
