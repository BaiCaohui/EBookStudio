/* 可视编辑器前端（T5-2W）：流式渲染 + contenteditable 编辑 + 工具条（文字/图片/表格）。
   与宿主（C#）通过 webview postMessage 通信：
     宿主 -> 前端: editor.load { html, title, chapterHref } / editor.imagePicked { href } / editor.saved { ok, message }
     前端 -> 宿主: editor.ready / editor.event.changed / editor.event.save { bodyXml } / editor.event.pickImage */
(function () {
  'use strict';

  var surface = document.getElementById('surface');
  var status = document.getElementById('status');
  var tableOps = document.getElementById('tableOps');
  var selectedImage = null;
  var dirty = false;
  // 最后一次光标所在的单元格：点工具条会让编辑区失去焦点、选区被折叠，
  // 这时再去读"当前选区"就找不到单元格了（用户实测：点了按钮光标就不在表格里了）。
  // DOM 引用不会因为失焦而丢失，所以把它记下来兜底。
  var currentCell = null;

  /* ---------- 主题：跟书库「设置 → 外观 → 主题」走（用户 2026-10-21） ----------
     宿主把主题色算好，通过 editor.html?themeJson=… 传进来（JSON 字符串）。
     为什么不用 editor.css 里写死：书库主题有六档，自定义档是用户任选四色，
     写死在 CSS 里必然对不上；而且书库的 XAML 主题字典才是唯一真源。
     书的样式表是在加载章节时才插进 <head> 的，可能把 body 背景/文字色又写回白色，
     所以主题样式表每次加载章节后都要重新追加到 <head> 最后（见 refreshThemeLayer）。 */
  var etsTheme = null;

  function applyEtsTheme(payload) {
    if (!payload) { return 'no-payload'; }

    etsTheme = payload;

    var root = document.documentElement;
    var vars = ['host', 'ribbon', 'panel', 'text', 'muted', 'border', 'hover', 'accent', 'accentText', 'selection'];
    var cssNames = {
      host: '--ets-host', ribbon: '--ets-ribbon', panel: '--ets-panel', text: '--ets-text',
      muted: '--ets-muted', border: '--ets-border', hover: '--ets-hover', accent: '--ets-accent',
      accentText: '--ets-accent-text', selection: '--ets-selection'
    };

    for (var i = 0; i < vars.length; i++) {
      if (payload[vars[i]]) { root.style.setProperty(cssNames[vars[i]], payload[vars[i]]); }
    }

    refreshThemeLayer();
    return 'ok';
  }

  function refreshThemeLayer() {
    if (!etsTheme) { return; }

    var style = document.getElementById('ets-theme');

    if (!style) {
      style = document.createElement('style');
      style.id = 'ets-theme';
    }

    // 每次都挪到 <head> 最后：书的样式表是后插进来的，必须排在它们后面才压得住
    if (style.parentNode) { style.parentNode.removeChild(style); }

    /* 为什么强制 color: inherit：不少 EPUB 把正文颜色硬编码成 #000，
       深色主题下就成了"深底黑字"没法看（阅读器那边同样这么处理，见 reader.js）。
       图片不反色，只换底色/文字/边框。 */
    style.textContent = [
      'html, body { background: var(--ets-host) !important; color: var(--ets-text) !important; }',
      '#ribbon { background: var(--ets-ribbon) !important; border-bottom-color: var(--ets-border) !important; }',
      '#host { background: var(--ets-host) !important; }',
      '#status { background: var(--ets-ribbon) !important; color: var(--ets-muted) !important; border-top-color: var(--ets-border) !important; }',
      '#surface { background: var(--ets-panel) !important; color: var(--ets-text) !important; }',
      '#surface, #surface * { color: inherit !important; }'
    ].join('\n');

    document.head.appendChild(style);
  }

  // 启动：URL 上带了主题就立刻用（首帧就是对的，不会先闪一下浅色再变）
  (function readThemeFromUrl() {
    try {
      var raw = new URLSearchParams(location.search).get('themeJson');

      if (raw) { applyEtsTheme(JSON.parse(raw)); }
    } catch (e) {
      // 主题参数坏了也不影响编辑
    }
  })();

  // 主题热切换（书库设置里改主题）时宿主会再推一次（ExecuteScriptAsync 调这个函数）
  window.etsApplyTheme = function (payload) {
    try { return applyEtsTheme(payload); } catch (e) { return 'err: ' + e; }
  };

  /* ---------- 选区保持：点工具条会让输入框失去焦点、选区被折叠，所以点按钮时不许抢焦点，
        并把最后一次选区存下来，操作前恢复（这是"按钮点了没反应"的根因） ---------- */
  var lastRange = null;
  var frozenRange = null;

  function saveSelection() {
    // 正在执行工具条动作时不要覆盖：点按钮会让输入框丢焦点、选区被折叠，
    // 那次折叠的选区会把真正的选区冲掉（这就是"点了没反应"的根因）
    if (frozenRange) { return; }

    var selection = window.getSelection();

    if (selection && selection.rangeCount > 0 && surface.contains(selection.anchorNode)) {
      lastRange = selection.getRangeAt(0).cloneRange();
    }
  }

  function ensureSelection() {
    var selection = window.getSelection();

    if (selection && selection.rangeCount > 0 && surface.contains(selection.anchorNode)) {
      return;      // 现场选区还在正文里 -> 就用它
    }

    var range = frozenRange || lastRange;

    if (!range) { return; }

    selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function freezeSelection() {
    var selection = window.getSelection();

    if (selection && selection.rangeCount > 0 && surface.contains(selection.anchorNode)) {
      frozenRange = selection.getRangeAt(0).cloneRange();
    }
  }

  function useFrozen() {
    var range = frozenRange || lastRange;

    if (!range) { return; }

    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function releaseSelection() {
    frozenRange = null;
    saveSelection();
  }

  function restoreSelection() {
    if (!lastRange) { return; }

    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(lastRange);
  }

  function post(message) {
    if (window.chrome && chrome.webview) {
      chrome.webview.postMessage(message);
    }
  }

  function say(text) { status.textContent = text; }

  /* ---------- 撤销 / 重做：自建快照栈（浏览器的 execCommand 历史跟不上我们自己的 DOM 改动，Word 式前进后退） ---------- */
  var history = [];
  var historyAt = -1;
  var idleTimer = null;
  var applying = false;
  var composing = false;

  function commitHistory() {
    if (applying) { return; }

    var html = surface.innerHTML;

    if (historyAt >= 0 && history[historyAt] === html) { return; }

    history = history.slice(0, historyAt + 1);
    history.push(html);

    if (history.length > 200) { history.shift(); }

    historyAt = history.length - 1;
    updateHistoryButtons();
  }

  function snapshot(force) {
    if (applying) { return; }

    clearTimeout(idleTimer);

    if (force) { commitHistory(); } else { idleTimer = setTimeout(commitHistory, 400); }
  }

  function applyHistory(step) {
    var target = historyAt + step;

    if (target < 0 || target >= history.length) { return; }

    historyAt = target;
    applying = true;
    surface.innerHTML = history[historyAt];
    applying = false;
    updateHistoryButtons();
    say('已' + (step < 0 ? '撤销' : '重做') + '：第 ' + (historyAt + 1) + ' / ' + history.length + ' 步');
  }

  function updateHistoryButtons() {
    document.getElementById('undo').disabled = historyAt <= 0;
    document.getElementById('redo').disabled = historyAt >= history.length - 1;
  }

  /* ---------- 兜底实现：不依赖 execCommand ---------- */
  function wrapInline(tagName) {
    var selection = window.getSelection();

    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) { return false; }

    var range = selection.getRangeAt(0);
    var existing = range.commonAncestorContainer;

    while (existing && existing.nodeType !== 1) { existing = existing.parentNode; }

    // 已经在同样的标签里 -> 取消格式（unwrap）
    if (existing && existing.tagName && existing.tagName.toLowerCase() === tagName) {
      var parent = existing.parentNode;

      while (existing.firstChild) { parent.insertBefore(existing.firstChild, existing); }

      parent.removeChild(existing);
      return true;
    }

    var wrapper = document.createElement(tagName);

    try {
      range.surroundContents(wrapper);
    } catch (e) {
      wrapper.appendChild(range.extractContents());
      range.insertNode(wrapper);
    }

    selection.removeAllRanges();
    return true;
  }

  function setBlock(tagName) {
    var node = getSelectionNode();

    if (!node) { return false; }

    while (node && node !== surface && node.nodeType !== 1) { node = node.parentNode; }

    var block = node;

    while (block && block !== surface && block.parentNode !== surface) { block = block.parentNode; }

    if (!block || block === surface) { return false; }

    var replacement = document.createElement(tagName);
    replacement.innerHTML = block.innerHTML;

    if (block.tagName === tagName.toUpperCase()) {
      // 已经是这个样式 -> 变回正文
      var plain = document.createElement('p');
      plain.innerHTML = block.innerHTML;
      block.parentNode.replaceChild(plain, block);
      return true;
    }

    block.parentNode.replaceChild(replacement, block);
    return true;
  }

  /* ---------- 工具条跟随光标：段落样式 + 字号实时反映选区 ---------- */
  function currentBlock() {
    var value = (document.queryCommandValue('formatBlock') || '').toLowerCase();
    return value || 'p';
  }

  function currentSize() {
    var node = getSelectionNode();

    while (node && node !== surface) {
      if (node.nodeType === 1 && node.style && node.style.fontSize) { return node.style.fontSize; }
      node = node.parentNode;
    }

    return '100%';
  }

  function syncToolbar() {
    var block = currentBlock();
    var blocks = ['p', 'h1', 'h2', 'h3', 'h4', 'blockquote'];
    document.getElementById('block').value = blocks.indexOf(block) >= 0 ? block : 'p';

    var size = String(currentSize()).replace('font-size:', '').trim();
    var sizeSelect = document.getElementById('size');
    var found = Array.prototype.some.call(sizeSelect.options, function (option) { return option.value === size; });
    sizeSelect.value = found ? size : '100%';
  }

  function markDirty() {
    if (!dirty) {
      dirty = true;
      post({ type: 'editor.event.changed', dirty: true });
    }
    say('有未保存的修改（点【保存到工作区】写入工作区；写进书里还要在主窗口点【保存】）');
  }

  /* ---------- 载入 ---------- */
  function reportImages() {
    var images = surface.querySelectorAll('img');
    var broken = 0;

    for (var i = 0; i < images.length; i++) {
      if (!images[i].complete || images[i].naturalWidth === 0) { broken++; }
    }

    if (images.length > 0) {
      say('图片 ' + (images.length - broken) + ' / ' + images.length + ' 张正常显示'
        + (broken > 0 ? '（' + broken + ' 张加载失败）' : ''));
    }
  }

  function load(payload) {
    // 关键：书里的图片/样式多是**相对路径**（如 src="xxx_005_r1.gif"），
    // 而这一页在另一个虚拟主机上，必须用 <base> 指到章节所在目录，否则图片全 404
    var base = document.head.querySelector('base[data-book]');

    if (!base) {
      base = document.createElement('base');
      base.setAttribute('data-book', '1');
      document.head.insertBefore(base, document.head.firstChild);
    }

    base.setAttribute('href', payload.baseUrl || '');

    if (!payload.baseUrl) { base.remove(); }

    var doc = new DOMParser().parseFromString(payload.html || '', 'text/html');

    // 把书自带的样式搬进本页（资源地址已由宿主绝化成虚拟主机 URL，图片/字体自动生效）
    Array.prototype.slice.call(document.head.querySelectorAll('[data-book]')).forEach(function (n) { n.remove(); });
    Array.prototype.slice.call(doc.head.querySelectorAll('link[rel=stylesheet], style')).forEach(function (n) {
      var copy = n.cloneNode(true);
      copy.setAttribute('data-book', '1');
      document.head.appendChild(copy);
    });

    // 正文：书里的样式类、样式表都保留；编辑属性一律清掉
    surface.innerHTML = doc.body ? doc.body.innerHTML : '';
    Array.prototype.slice.call(surface.querySelectorAll('[contenteditable]')).forEach(function (n) {
      n.removeAttribute('contenteditable');
    });

    // 书的样式刚插进 <head>，把主题样式再挪到最后，避免书的 body 底色/文字色把主题盖掉
    refreshThemeLayer();

    dirty = false;
    selectedImage = null;
    history = [];
    historyAt = -1;
    snapshot(true);
    refreshTableOps();
    syncToolbar();
    updateImageButtons();
    say('已载入' + (payload.title ? '：' + payload.title : '') + '（可视模式，流式视图）');
    post({ type: 'editor.ready' });
    setTimeout(reportImages, 1200);
  }

  /* ---------- 工具条 ---------- */
  function exec(cmd, value) {
    if (document.activeElement !== surface) { surface.focus({ preventScroll: true }); }
    // 不要再调用 surface.focus()：它会把刚恢复的选区又折叠掉
    ensureSelection();
    var handled = false;

    // execCommand 在"没有窗口焦点"时会静默失败（实测取证过），所以常用命令自己实现兜底
    if (cmd === 'bold' || cmd === 'italic' || cmd === 'underline' || cmd === 'strikeThrough') {
      handled = wrapInline({ bold: 'b', italic: 'i', underline: 'u', strikeThrough: 's' }[cmd]);
    } else if (cmd === 'formatBlock') {
      handled = setBlock(value);
    }

    if (!handled) { document.execCommand(cmd, false, value === undefined ? null : value); }
    markDirty();
    snapshot(true);
    releaseSelection();
  }

  // 【真因修复】这里原来写死 document.querySelectorAll('#ribbon button[data-cmd]')，
  // 而工具条容器后来改名成了 #ribbon -> 所有按钮一个都没绑上事件（点了完全没反应）。
  // 改成事件委托：不管按钮在哪个分组容器里都能触发。
  document.addEventListener('click', function (event) {
    var target = event.target;

    if (!target || !target.closest) { return; }

    var commandButton = target.closest('button[data-cmd]');

    if (commandButton) { exec(commandButton.getAttribute('data-cmd')); return; }

    var tableButton = target.closest('button[data-tbl]');

    if (tableButton) { tableAction(tableButton.getAttribute('data-tbl')); }
  });

  document.getElementById('block').addEventListener('change', function () {
    exec('formatBlock', this.value);
    this.selectedIndex = 0;
  });

  // 字号：先让 execCommand 生成 <font size=7>，再换成相对字号 <span style="font-size:125%">
  document.getElementById('size').addEventListener('change', function () {
    var percent = this.value;
    ensureSelection();
    document.execCommand('fontSize', false, '7');
    var fonts = surface.querySelectorAll('font[size="7"]');
    for (var i = 0; i < fonts.length; i++) {
      var span = document.createElement('span');
      span.style.fontSize = percent;
      while (fonts[i].firstChild) { span.appendChild(fonts[i].firstChild); }
      fonts[i].parentNode.replaceChild(span, fonts[i]);
    }
    markDirty();
    snapshot(true);
    syncToolbar();
  });

  function clearDirty(id) {
    var input = document.getElementById(id);

    if (input) { delete input.dataset.etsDirty; }
  }

  function setSizeValue(id, value) {
    var input = document.getElementById(id);

    if (!input) { return; }

    if (input === document.activeElement || input.dataset.etsDirty === '1') { return; }

    input.value = value;
  }

  /* ---------- 图片 ---------- */
  function updateImageButtons() {
    document.getElementById('imgReplace').disabled = !selectedImage;
    document.getElementById('imgDelete').disabled = !selectedImage;
  }

  surface.addEventListener('click', function (event) {
    if (selectedImage) { selectedImage.classList.remove('ets-sel'); }
    selectedImage = event.target && event.target.tagName === 'IMG' ? event.target : null;
    if (selectedImage) { selectedImage.classList.add('ets-sel'); }
    updateImageButtons();
    refreshTableOps();
  });

  document.getElementById('imgInsert').addEventListener('click', function () {
    post({ type: 'editor.event.pickImage', replace: false });
  });

  document.getElementById('imgReplace').addEventListener('click', function () {
    if (selectedImage) { post({ type: 'editor.event.pickImage', replace: true }); }
  });

  document.getElementById('imgDelete').addEventListener('click', function () {
    if (selectedImage) { selectedImage.remove(); selectedImage = null; updateImageButtons(); markDirty(); }
  });

  function insertNodeAtSelection(node) {
    ensureSelection();
    var selection = window.getSelection();

    if (!selection || selection.rangeCount === 0) { surface.appendChild(node); return; }

    var range = selection.getRangeAt(0);
    range.deleteContents();
    range.insertNode(node);
    range.setStartAfter(node);
    range.collapse(true);
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function imagePicked(href) {
    if (selectedImage) {
      selectedImage.setAttribute('src', href);
      markDirty();
      snapshot(true);
      say('已替换图片：' + href);
      return;
    }

    var image = document.createElement('img');
    image.setAttribute('src', href);
    image.setAttribute('alt', '');
    insertNodeAtSelection(image);
    markDirty();
    snapshot(true);
    say('已插入图片：' + href);
  }

  /* ---------- 表格（Word 式） ---------- */
  function cellOf(node) {
    while (node && node !== surface) {
      if (node.tagName === 'TD' || node.tagName === 'TH') { return node; }
      node = node.parentNode;
    }
    return null;
  }

  function tableOf(node) {
    while (node && node !== surface) {
      if (node.tagName === 'TABLE') { return node; }
      node = node.parentNode;
    }
    return null;
  }

  function refreshTableOps() {
    var cell = cellOf(getSelectionNode());

    if (cell && surface.contains(cell)) {
      currentCell = cell;          // 只记住仍然挂在正文里的单元格
    } else if (currentCell && !surface.contains(currentCell)) {
      currentCell = null;          // 单元格被删了/换章了
    }

    var active = cell || currentCell;
    tableOps.hidden = !active;
    refreshTableSize(active);
  }

  /* ---------- 表格尺寸：列宽 / 行高 / 表宽（Word 式） ---------- */
  /* ---------- 表格尺寸的两个通用零件（列宽/表宽/拖拽三条路都用它们，免得到处算不齐） ---------- */

  // 让我们写进去的 width 就是"渲染出来的宽度"。书自带的样式里 td 可能是 content-box，
  // 那样 style.width 只是内容宽，渲染宽还要加上 padding+border，跟用户填的数字永远对不上。
  function lockBoxSizing(table) {
    table.style.boxSizing = 'border-box';

    for (var r = 0; r < table.rows.length; r++) {
      for (var c = 0; c < table.rows[r].cells.length; c++) {
        table.rows[r].cells[c].style.boxSizing = 'border-box';
      }
    }
  }

  // 把整张表的列宽按 assigned[] 写死，并把表宽设成各列之和。
  // 为什么表宽必须**正好**等于各列之和：table-layout:fixed 下，表格的实际宽度 =
  // max(表格 width, 各列宽之和)（CSS 2.1 17.5.2.1 的 MIN 规则），差一点点就会渲染成
  // "各列之和"而不是用户填的那个数（实测：填 600，渲染 668，就是各列之和 667 撑出来的）。
  function writeTableWidths(table, assigned) {
    lockBoxSizing(table);
    table.style.tableLayout = 'fixed';

    for (var r = 0; r < table.rows.length; r++) {
      for (var k = 0; k < table.rows[r].cells.length && k < assigned.length; k++) {
        table.rows[r].cells[k].style.width = assigned[k] + 'px';
      }
    }

    var total = 0;

    for (var i = 0; i < assigned.length; i++) { total += assigned[i]; }

    table.style.width = total + 'px';
    return total;
  }

  // 按当前各列的实际宽度，把目标表宽按比例分配下去，并保证各列之和**正好**等于目标值
  function distributeTableWidth(table, width) {
    var cells = table.rows[0].cells;
    var measured = [];
    var sum = 0;

    for (var c = 0; c < cells.length; c++) {
      measured[c] = Math.max(1, cells[c].getBoundingClientRect().width);
      sum += measured[c];
    }

    var assigned = [];
    var left = width;

    for (var i = 0; i < cells.length; i++) {
      // 最后一列吃掉四舍五入的零头，保证总和分毫不差
      var w = i === cells.length - 1
        ? Math.max(20, left)
        : Math.max(20, Math.round(measured[i] / sum * width));

      assigned[i] = w;
      left -= w;
    }

    return assigned;
  }

  function refreshTableSize(cell) {
    var box = document.getElementById('tableSize');

    if (!cell) { cell = cellOf(getSelectionNode()); }

    if (!cell) { box.hidden = true; return; }

    box.hidden = false;
    var table = tableOf(cell);

    // 读"真实渲染尺寸"而不是内联 style（新表格没有内联宽度，读内联永远是空，实测过）。
    // 【关键】回填时**绝不覆盖用户正在编辑或已经改过的输入框**否则打字时
    // keyup/selectionchange/mouseup 每次都会把数字覆盖回原值，表现就是"数字定死改不了"（用户实测）。
    setSizeValue('colWidth', Math.round(cell.getBoundingClientRect().width));
    setSizeValue('rowHeight', Math.round(cell.parentNode.getBoundingClientRect().height));
    setSizeValue('tableWidth', Math.round(table.getBoundingClientRect().width));
    return;
  }

  ['colWidth', 'rowHeight', 'tableWidth'].forEach(function (id) {
    var input = document.getElementById(id);

    if (input) {
      input.addEventListener('input', function () { this.dataset.etsDirty = '1'; });
    }
  });

  document.getElementById('colWidthApply').addEventListener('click', function () {
    var cell = cellOf(getSelectionNode()) || currentCell;

    if (!cell) { return; }

    var width = parseInt(document.getElementById('colWidth').value, 10);

    if (!width) { return; }

    var table = tableOf(cell);
    var index = pickedCol >= 0 ? pickedCol : cellIndex(cell);
    var firstRow = table.rows[0];
    var assigned = [];

    // 先把每一列的实际宽定下来（改的那一列用新值），再把表宽改成它们的总和：
    // 只写单元格宽度而不动表宽的话，fixed + width:100% 会重新分配，看上去"改了没反应"（实测 colBefore=colAfter=217）
    for (var c = 0; c < firstRow.cells.length; c++) {
      assigned[c] = Math.max(
        20, c === index ? width : Math.round(firstRow.cells[c].getBoundingClientRect().width));
    }

    var total = writeTableWidths(table, assigned);

    snapshot(true);
    clearDirty('colWidth');
    say('第 ' + (index + 1) + ' 列列宽 = ' + width + 'px，表宽已固定为 ' + total + 'px');
  });

  document.getElementById('rowHeightApply').addEventListener('click', function () {
    var cell = cellOf(getSelectionNode()) || currentCell;

    if (!cell) { return; }

    var height = parseInt(document.getElementById('rowHeight').value, 10);

    if (!height) { return; }

    var row = pickedRow || cell.parentNode;
    row.style.height = height + 'px';

    for (var c = 0; c < row.cells.length; c++) {
      row.cells[c].style.height = height + 'px';
      row.cells[c].style.verticalAlign = 'top';
    }

    snapshot(true);
    clearDirty('rowHeight');
    say('第 ' + (rowIndex(row) + 1) + ' 行行高已设为 ' + height + 'px（内容更高时以内容为准）');
  });

  document.getElementById('tableWidthApply').addEventListener('click', function () {
    var cell = cellOf(getSelectionNode()) || currentCell;

    if (!cell) { return; }

    var width = parseInt(document.getElementById('tableWidth').value, 10);

    if (!width) { return; }

    var table = tableOf(cell);

    // 表格是 table-layout:fixed 且每列已写死宽度时，**只改表宽不会生效**（列宽会把表格撑回原样）。
    // 所以按比例把每一列的宽度一起缩放；而且各列之和要**正好**等于用户填的表宽 
    // 否则表格实际宽度会被"各列之和"撑大（实测：填 600 渲染成 668）。（用户早先实测"表宽改不了"）
    var assigned = distributeTableWidth(table, width);

    writeTableWidths(table, assigned);

    snapshot(true);
    clearDirty('tableWidth');
    say('表格宽度已设为 ' + width + 'px（各列已按比例同步缩放）');
  });

  document.getElementById('rowAdd').addEventListener('click', function () {
    ensureSelection();
    var cell = cellOf(getSelectionNode()) || currentCell;

    if (!cell) { return; }

    var table = tableOf(cell);
    var row = table.rows[table.rows.length - 1];
    var count = row.cells.length;
    var created = table.insertRow(-1);

    for (var i = 0; i < count; i++) { created.insertCell(-1).innerHTML = '<br/>'; }

    snapshot(true);
    say('已在表格末尾加一行');
  });

  document.getElementById('colAdd').addEventListener('click', function () {
    ensureSelection();
    var cell = cellOf(getSelectionNode()) || currentCell;

    if (!cell) { return; }

    var table = tableOf(cell);

    for (var r = 0; r < table.rows.length; r++) { table.rows[r].insertCell(-1).innerHTML = '<br/>'; }

    snapshot(true);
    say('已在表格末尾加一列');
  });

  function getSelectionNode() {
    var selection = window.getSelection();
    var node = selection && selection.anchorNode ? selection.anchorNode : null;

    if (node) { return node; }

    // 选区丢过（点过按钮/下拉框）时，用最后保存的选区兜底，表格单元格也能认出来
    return lastRange ? lastRange.startContainer : null;
  }

  document.getElementById('tableInsert').addEventListener('click', function () {
    ensureSelection();
    var rows = Math.max(1, Math.min(50, parseInt(document.getElementById('tbRows').value, 10) || 3));
    var cols = Math.max(1, Math.min(20, parseInt(document.getElementById('tbCols').value, 10) || 3));
    var html = '<table><tbody>';
    for (var r = 0; r < rows; r++) {
      html += '<tr>';
      for (var c = 0; c < cols; c++) { html += '<td><br/></td>'; }
      html += '</tr>';
    }
    html += '</tbody></table><p><br/></p>';
    // 同样不依赖 execCommand：直接构造 DOM 插到光标处
    var holder = document.createElement('div');
    holder.innerHTML = html;
    var fragment = document.createDocumentFragment();
    while (holder.firstChild) { fragment.appendChild(holder.firstChild); }
    insertNodeAtSelection(fragment);
    markDirty();
    say('已插入 ' + rows + ' 行 x ' + cols + ' 列的表格');
  });

  // 表格按钮也在上面的委托里统一处理（这里不再单独绑定，避免一次点击触发两次）

  function cellIndex(cell) {
    var index = 0;
    var node = cell;
    while (node && node.previousElementSibling) { node = node.previousElementSibling; index++; }
    return index;
  }

  function rowIndex(row) {
    var index = 0;
    var node = row;
    while (node && node.previousElementSibling) { node = node.previousElementSibling; index++; }
    return index;
  }

  function tableAction(action) {
    ensureSelection();
    var cell = cellOf(getSelectionNode());

    if (!cell) { return; }

    var row = cell.parentNode;
    var table = tableOf(cell);
    var rows = table.rows;
    var r = rowIndex(row);
    var c = cellIndex(cell);

    if (action === 'rowBefore') { table.insertRow(r); fillRow(table.rows[r], cell, cellsOf(table.rows[r - 1 < 0 ? 1 : r - 1] || row)); }
    else if (action === 'rowAfter') { table.insertRow(r + 1); fillRow(table.rows[r + 1], cell, cellsOf(row)); }
    else if (action === 'rowDelete') { if (rows.length > 1) { table.deleteRow(r); } }
    else if (action === 'colBefore' || action === 'colAfter') { column(table, action === 'colBefore' ? c : c + 1, false, cell); }
    else if (action === 'colDelete') { if (cellsOf(row).length > 1) { column(table, c, true, cell); } }
    else if (action === 'tableDelete') { table.remove(); }
    else if (action === 'mergeRight') { mergeRight(cell); }
    else if (action === 'split') { splitCell(cell); }

    markDirty();
    snapshot(true);
    refreshTableOps();
    releaseSelection();
    say('表格操作：' + action);
  }

  function cellsOf(row) {
    return Array.prototype.slice.call(row ? row.cells : []);
  }

  function fillRow(target, model, sourceCells) {
    var count = sourceCells.length;
    while (target.cells.length > 0) { target.deleteCell(0); }
    for (var i = 0; i < count; i++) {
      var tag = sourceCells[i] && sourceCells[i].tagName === 'TH' ? 'th' : 'td';
      var created = target.insertCell(-1);
      created.outerHTML = '<' + tag + '><br/></' + tag + '>';
    }
  }

  function column(table, index, remove, model) {
    for (var r = 0; r < table.rows.length; r++) {
      var cells = table.rows[r].cells;

      if (index >= cells.length) { continue; }

      if (remove) { table.rows[r].deleteCell(index); }
      else {
        var tag = model && model.tagName === 'TH' ? 'th' : 'td';
        var created = table.rows[r].insertCell(index);
        created.outerHTML = '<' + tag + '><br/></' + tag + '>';
      }
    }
  }

  function mergeRight(cell) {
    var next = cell.nextElementSibling;

    if (!next) { return; }

    var span = parseInt(cell.getAttribute('colspan') || '1', 10) + parseInt(next.getAttribute('colspan') || '1', 10);

    while (next.firstChild) { cell.appendChild(next.firstChild); }
    next.remove();
    cell.setAttribute('colspan', String(span));
    cell.setAttribute('data-ets-merged', '1');
  }

  function splitCell(cell) {
    var span = parseInt(cell.getAttribute('colspan') || '1', 10);

    if (span <= 1) { cell.removeAttribute('rowspan'); return; }

    cell.setAttribute('colspan', String(span - 1));
    var created = document.createElement(cell.tagName.toLowerCase() === 'th' ? 'th' : 'td');
    created.innerHTML = '<br/>';
    cell.parentNode.insertBefore(created, cell.nextSibling);
  }

  /* ---------- 选整行 / 选整列（Word 式：先在表格里选中一行或一列，再改尺寸） ---------- */
  var pickedRow = null;
  var pickedCol = -1;

  function clearPicked() {
    Array.prototype.slice.call(surface.querySelectorAll('.ets-sel')).forEach(function (node) {
      node.classList.remove('ets-sel');
    });
    pickedRow = null;
    pickedCol = -1;
  }

  function pick(column) {
    ensureSelection();
    var cell = cellOf(getSelectionNode()) || currentCell;

    if (!cell) { say('请先把光标放到表格里'); return; }

    clearPicked();
    var table = tableOf(cell);

    if (column) {
      pickedCol = cellIndex(cell);

      for (var r = 0; r < table.rows.length; r++) {
        if (table.rows[r].cells[pickedCol]) { table.rows[r].cells[pickedCol].classList.add('ets-sel'); }
      }

      document.getElementById('colWidth').value = Math.round(cell.getBoundingClientRect().width) || '';
      say('已选中第 ' + (pickedCol + 1) + ' 列（当前列宽 ' + Math.round(cell.getBoundingClientRect().width) + 'px，改完点应用）');
    } else {
      pickedRow = cell.parentNode;

      Array.prototype.slice.call(pickedRow.cells).forEach(function (node) { node.classList.add('ets-sel'); });

      document.getElementById('rowHeight').value = Math.round(pickedRow.getBoundingClientRect().height) || '';
      say('已选中第 ' + (rowIndex(pickedRow) + 1) + ' 行（当前行高 ' + Math.round(pickedRow.getBoundingClientRect().height) + 'px，改完点应用）');
    }
  }

  document.getElementById('selectRow').addEventListener('click', function () { pick(false); });
  document.getElementById('selectCol').addEventListener('click', function () { pick(true); });

  /* ---------- 拖拽调列宽（Word 式：把鼠标放到单元格右边界拖动） ---------- */
  var dragState = null;

  surface.addEventListener('mousemove', function (event) {
    var cell = event.target && event.target.closest ? event.target.closest('td,th') : null;

    if (!cell) { surface.style.cursor = ''; return; }

    var box = cell.getBoundingClientRect();
    surface.style.cursor = Math.abs(event.clientX - box.right) < 6 ? 'col-resize' : '';
  });

  surface.addEventListener('mousedown', function (event) {
    var cell = event.target && event.target.closest ? event.target.closest('td,th') : null;

    if (!cell || !event.clientX && event.clientX !== 0) { return; }

    var box = cell.getBoundingClientRect();

    if (Math.abs(event.clientX - box.right) > 6) { return; }   // 只有贴近右边界才算拖列宽

    dragState = { cell: cell, startX: event.clientX, startWidth: Math.round(box.width) };
    event.preventDefault();
    say('拖动调整列宽中（松开结束）');
  }, true);

  document.addEventListener('mousemove', function (event) {
    if (!dragState) { return; }

    var width = Math.max(24, dragState.startWidth + (event.clientX - dragState.startX));
    var table = tableOf(dragState.cell);
    var index = cellIndex(dragState.cell);
    var firstRow = table.rows[0];
    var assigned = [];

    for (var c = 0; c < firstRow.cells.length; c++) {
      assigned[c] = Math.max(
        20, c === index ? width : Math.round(firstRow.cells[c].getBoundingClientRect().width));
    }

    // 表宽永远跟着各列之和走，不另外推算，免得两边对不上
    writeTableWidths(table, assigned);
    event.preventDefault();
  });

  document.addEventListener('mouseup', function () {
    if (!dragState) { return; }

    var width = Math.round(dragState.cell.getBoundingClientRect().width);
    snapshot(true);
    clearDirty('colWidth');
    say('列宽已拖到 ' + width + 'px');
    dragState = null;
  });

  /* ---------- 保存与脏状态 ---------- */
  surface.addEventListener('compositionstart', function () { composing = true; });
  surface.addEventListener('compositionend', function () { composing = false; markDirty(); snapshot(false); });
  // 【IME】合成期间只标脏、不落历史快照，避免把半成品拼音存进撤销栈。
  surface.addEventListener('input', function () { markDirty(); if (!composing) { snapshot(false); } });

  var HOST_PREFIX = 'http://ebook.local/';
  var basePath = '';

  // 把 http://ebook.local/... 的绝对地址换回章节文件里的相对路径（书里的写法才是相对路径）
  function relativeFromBase(path) {
    var base = basePath.split('/').filter(function (s) { return s.length > 0; });
    var target = path.split('/').filter(function (s) { return s.length > 0; });

    while (base.length > 0 && target.length > 0 && base[0] === target[0]) { base.shift(); target.shift(); }

    return new Array(base.length).fill('..').concat(target).join('/');
  }

  function relativize(node) {
    Array.prototype.slice.call(node.querySelectorAll('[src],[href]')).forEach(function (element) {
      ['src', 'href'].forEach(function (attribute) {
        var value = element.getAttribute(attribute);

        if (value && value.indexOf(HOST_PREFIX) === 0) {
          element.setAttribute(attribute, relativeFromBase(decodeURIComponent(value.substring(HOST_PREFIX.length))));
        }
      });
    });
  }

  function bodyXml() {
    var clone = surface.cloneNode(true);
    relativize(clone);
    Array.prototype.slice.call(clone.querySelectorAll('.ets-sel')).forEach(function (n) { n.classList.remove('ets-sel'); });
    Array.prototype.slice.call(clone.querySelectorAll('[data-ets-merged]')).forEach(function (n) { n.removeAttribute('data-ets-merged'); });
    var serializer = new XMLSerializer();
    var out = '';
    for (var i = 0; i < clone.childNodes.length; i++) { out += serializer.serializeToString(clone.childNodes[i]); }
    return out;
  }

  document.getElementById('btnSourceMode').addEventListener('click', function () {
    post({ type: 'editor.event.openSource' });
  });

  document.getElementById('save').addEventListener('click', function () {
    say('正在保存');
    post({ type: 'editor.event.save', bodyXml: bodyXml() });
  });

  document.addEventListener('selectionchange', function () {
    saveSelection();
    refreshTableOps();
    syncToolbar();
  });

  // 【关键】工具条上除输入框/下拉框以外的一切点击，都在**捕获阶段**阻止默认行为：
  // 否则点击会把焦点和选区从正文里抢走（用户实测：一改选项，选中的文字/单元格就被取消选中）
  document.addEventListener('mousedown', function (event) {
    var target = event.target;

    if (!target || !target.closest) { return; }

    if (target.closest('#ribbon') && !target.closest('input, select, textarea')) {
      event.preventDefault();
      freezeSelection();     // 趁选区还在，先冻住
    }
  }, true);

  document.addEventListener('mouseup', function () { saveSelection(); syncToolbar(); refreshTableOps(); });
  document.addEventListener('keyup', function () { saveSelection(); syncToolbar(); refreshTableOps(); });

  // 段落按钮用文字标签，比符号看得懂
  var buttonLabels = {
    insertUnorderedList: ' 列表', insertOrderedList: '1. 列表',
    justifyLeft: '左', justifyCenter: '中', justifyRight: '右', justifyFull: '两端',
    outdent: '缩进-', indent: '缩进+'
  };

  Array.prototype.slice.call(document.querySelectorAll('#ribbon button[data-cmd]')).forEach(function (button) {
    var label = buttonLabels[button.getAttribute('data-cmd')];

    if (label) { button.textContent = label; }
  });

  document.getElementById('undo').addEventListener('click', function () { applyHistory(-1); });
  document.getElementById('redo').addEventListener('click', function () { applyHistory(1); });
  window.addEventListener('keydown', function (e) {
    if (e.isComposing || e.keyCode === 229) { return; }   // 【IME】合成中不抢 Ctrl 组合键
    if (!(e.ctrlKey || e.metaKey)) { return; }

    var key = e.key.toLowerCase();

    if (key === 's') { e.preventDefault(); document.getElementById('save').click(); }
    else if (key === 'z' && !e.shiftKey) { e.preventDefault(); applyHistory(-1); }
    else if (key === 'y' || (key === 'z' && e.shiftKey)) { e.preventDefault(); applyHistory(1); }
  });

  /* ---------- 与宿主通信 ---------- */
  if (window.chrome && chrome.webview) {
    chrome.webview.addEventListener('message', function (event) {
      var payload = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;

      if (payload.type === 'editor.load') { basePath = payload.basePath || ''; load(payload); }
      else if (payload.type === 'editor.imagePicked') { imagePicked(payload.href); }
      else if (payload.type === 'editor.saved') {
        if (payload.ok) { dirty = false; }
        say(payload.message || (payload.ok ? '已保存' : '保存失败'));
      }
    });
  }
})();
