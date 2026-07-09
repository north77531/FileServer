public static class SharedLayout
{
    static readonly (string href, string label, string key)[] MainItems =
    [
        ("/stocks", "📈 股票", "stocks"),
        ("/debt",   "💳 負債", "debt"),
        ("/house",  "🏠 房子", "house"),
        ("/car",    "🚗 車子", "car"),
        ("/life",   "🌿 生活", "life"),
        ("/work/log", "💼 工作", "work"),
    ];

    static readonly Dictionary<string, (string href, string label, string key)[]> SubItems = new()
    {
        ["stocks"] = [
            ("/stocks",           "庫存",   "stocks"),
            ("/stocks/trade",     "新增交易", "trade"),
            ("/stocks/history",   "交易紀錄", "history"),
            ("/stocks/dividends", "歷史配息", "dividends"),
            ("/stocks/snapshots", "市值快照", "snapshots"),
        ],
        ["debt"] = [
            ("/debt",     "貸款列表", "list"),
            ("/debt/add", "新增貸款", "add"),
        ],
        ["house"] = [
            ("/house/properties", "目前房產", "properties"),
        ],
        ["car"] = [
            ("/car",              "車輛管理", "list"),
            ("/car/add",          "新增車輛", "add"),
            ("/car/expenses",     "花費紀錄", "expenses"),
            ("/car/expenses/add", "新增花費", "addexp"),
        ],
        ["life"] = [
            ("/life",          "棒棒集點卡", "cards"),
            ("/life/card/add", "新增集點卡", "cardadd"),
        ],
        ["work"] = [
            ("/work/log",       "工作紀錄", "log"),
            ("/work/log/add",   "新增紀錄", "logadd"),
            ("/work/dashboard", "Dashboard", "dashboard"),
        ],
    };

    public static string Nav(string cat, string sub = "")
    {
        var mainLinks = string.Join("", MainItems.Select(n =>
            $"<a href='{n.href}'{(n.key == cat ? " class='active'" : "")}>{n.label}</a>"));

        SubItems.TryGetValue(cat, out var subs);
        subs ??= [];
        var subLinks = string.Join("", subs.Select(n =>
            $"<a href='{n.href}'{(n.key == sub ? " class='active'" : "")}>{n.label}</a>"));

        var subNav = subs.Length > 0
            ? $"<nav class='sub-nav'>{subLinks}</nav>"
            : "<div class='sub-spacer'></div>";

        return $"<nav class='main-nav'>{mainLinks}</nav>{subNav}";
    }

    public static string Css => @"
*{box-sizing:border-box}
body{font-family:'Microsoft JhengHei','Noto Sans TC',sans-serif;max-width:1200px;margin:0 auto;padding:16px;background:#f5f7fa;color:#333}
.main-nav{display:flex;gap:4px;flex-wrap:wrap;margin-bottom:0}
.main-nav a{padding:9px 18px;border-radius:8px 8px 0 0;font-size:.9rem;font-weight:600;text-decoration:none;color:#666;background:#dde2e8;border:1px solid transparent;border-bottom:none;transition:background .15s}
.main-nav a:hover:not(.active){background:#c8d0da;color:#333}
.main-nav a.active{background:#fff;color:#0055cc;border-color:#ddd;border-bottom-color:#fff;margin-bottom:-1px;position:relative;z-index:1}
.sub-nav{background:#fff;padding:10px 16px;border-radius:0 8px 8px 8px;box-shadow:0 2px 8px rgba(0,0,0,.1);margin-bottom:24px;display:flex;gap:6px;flex-wrap:wrap;border:1px solid #ddd;position:relative;z-index:0}
.sub-spacer{height:20px}
.sub-nav a{padding:5px 16px;border-radius:20px;font-size:.88rem;text-decoration:none;color:#555;transition:background .15s}
.sub-nav a:hover{background:#f0f4ff;color:#0055cc}
.sub-nav a.active{background:#0055cc;color:#fff}
h1{font-size:1.4rem;margin:0 0 20px;color:#222}
h2{font-size:1.1rem;margin:20px 0 12px;color:#333;border-bottom:2px solid #eee;padding-bottom:6px}
table{width:100%;border-collapse:collapse;background:#fff;box-shadow:0 1px 4px rgba(0,0,0,.08)}
.table-wrap{border-radius:8px;overflow:auto;max-height:75vh}
th{text-align:left;padding:10px 14px;background:#f0f4ff;color:#444;font-size:.85rem;white-space:nowrap}
td{padding:9px 14px;border-bottom:1px solid #f0f0f0;font-size:.9rem;vertical-align:middle}
tr:last-child td{border-bottom:none}
.tr-right th,.tr-right td{text-align:right}
.tr-right th:first-child,.tr-right td:first-child,.tr-right th:nth-child(2),.tr-right td:nth-child(2){text-align:left}
.pos{color:#c00}
.neg{color:#080}
.loading{color:#999;font-style:italic}
tfoot td{font-weight:600;background:#f8f8f8;border-top:2px solid #ddd}
thead th{position:sticky;top:0;z-index:2;background:#f0f4ff}
.filter-row th{background:#dae2f0;padding:3px 4px;z-index:3}
.filter-btn{width:100%;display:flex;align-items:center;justify-content:space-between;gap:4px;padding:3px 6px;border:1px solid #bbc;border-radius:3px;font-size:.78rem;background:#fff;cursor:pointer;color:#445;font-weight:400}
.filter-btn:hover{border-color:#0055cc}
.filter-btn:focus{outline:none;border-color:#0055cc}
.filter-btn.active{border-color:#0055cc;background:#eaf1ff;color:#0055cc;font-weight:600}
.filter-btn-label{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.filter-btn-arrow{flex:none;font-size:.7rem;color:#889}
.filter-panel{position:fixed;z-index:1000;background:#fff;border:1px solid #ccc;border-radius:8px;box-shadow:0 6px 20px rgba(0,0,0,.18);padding:8px;width:220px;max-height:360px;display:flex;flex-direction:column;font-size:.85rem}
.filter-panel-search{padding:6px 8px;border:1px solid #ddd;border-radius:5px;font-size:.85rem;margin-bottom:6px;width:100%}
.filter-panel-actions{display:flex;justify-content:space-between;align-items:center;font-size:.8rem;color:#555;margin-bottom:4px;padding-bottom:6px;border-bottom:1px solid #eee}
.filter-panel-actions label{display:flex;align-items:center;gap:4px;cursor:pointer;font-weight:600;margin-bottom:0}
.filter-panel-actions a{color:#0055cc;text-decoration:none;font-size:.78rem}
.filter-panel-actions a:hover{text-decoration:underline}
.filter-panel-quick{color:#0055cc;text-decoration:none;font-size:.78rem;margin-bottom:6px;display:inline-block;cursor:pointer}
.filter-panel-quick:hover{text-decoration:underline}
.filter-panel-list{overflow-y:auto;flex:1;display:flex;flex-direction:column;gap:1px;margin-bottom:8px}
.filter-panel-list label{display:flex;align-items:center;gap:6px;padding:3px 4px;border-radius:4px;cursor:pointer;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:400;margin-bottom:0;color:#333}
.filter-panel-list label:hover{background:#f0f4ff}
.filter-panel input[type=checkbox]{width:auto;flex:none}
.filter-panel-buttons{display:flex;gap:6px;justify-content:flex-end}
.form-card{background:#fff;border-radius:8px;padding:24px;box-shadow:0 1px 4px rgba(0,0,0,.08);max-width:600px}
.field{margin-bottom:16px}
label{display:block;font-size:.85rem;color:#555;margin-bottom:4px;font-weight:500}
input,select,textarea{width:100%;padding:9px 12px;border:1px solid #ddd;border-radius:6px;font-size:.95rem;font-family:inherit}
input:focus,select:focus,textarea:focus{outline:none;border-color:#0055cc;box-shadow:0 0 0 2px rgba(0,85,204,.15)}
.row2{display:grid;grid-template-columns:1fr 1fr;gap:12px}
.row3{display:grid;grid-template-columns:1fr 1fr 1fr;gap:12px}
.combo{position:relative}
.combo-panel{display:none;position:absolute;top:100%;left:0;right:0;margin-top:2px;background:#fff;border:1px solid #ddd;border-radius:6px;max-height:220px;overflow-y:auto;box-shadow:0 4px 14px rgba(0,0,0,.14);z-index:50}
.combo-panel.open{display:block}
.combo-option{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:7px 10px;font-size:.88rem;cursor:pointer}
.combo-option:hover{background:#f0f4ff}
.combo-option-text{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.combo-option-del{flex:none;color:#999;padding:1px 7px;border-radius:4px;font-size:.78rem}
.combo-option-del:hover{background:#fde8e8;color:#c0392b}
.btn{padding:10px 24px;background:#0055cc;color:#fff;border:none;border-radius:6px;font-size:.95rem;cursor:pointer;font-weight:500;text-decoration:none;display:inline-block;line-height:1.4}
.btn:hover{background:#0044aa}
.btn-sm{padding:5px 14px;font-size:.82rem}
.btn-danger{background:#c0392b}
.btn-danger:hover{background:#a93226}
.btn-outline{background:#fff;color:#0055cc;border:1.5px solid #0055cc}
.btn-outline:hover{background:#f0f4ff}
.alert{padding:10px 14px;border-radius:6px;margin-bottom:16px;font-size:.9rem}
.alert.ok{background:#e6f4ea;color:#1e7e34;border:1px solid #b8dfc0}
.alert.err{background:#fde8e8;color:#c0392b;border:1px solid #f5b8b8}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:14px;margin-bottom:24px}
.card{background:#fff;border-radius:10px;padding:16px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08)}
.card .lbl{font-size:.78rem;color:#888;margin-bottom:4px}
.card .val{font-size:1.3rem;font-weight:700}
.empty-state{text-align:center;padding:60px 20px;color:#999}
.empty-state .icon{font-size:3rem;margin-bottom:12px}
.tag{display:inline-block;padding:2px 10px;border-radius:12px;font-size:.8rem;font-weight:500;background:#eef2ff;color:#445}
.actions{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.section{background:#fff;border-radius:8px;padding:20px;box-shadow:0 1px 4px rgba(0,0,0,.08);margin-bottom:20px}
.calc-box{background:#f5f8ff;border:1px solid #dce6ff;border-radius:6px;padding:12px 14px;font-size:.88rem;margin-bottom:16px}
.calc-box div{display:flex;justify-content:space-between;padding:2px 0}
.calc-box .total{font-weight:600;border-top:1px solid #c0d0f0;margin-top:6px;padding-top:6px}
";

    public static string TableJs => @"
function initTable(id, opts) {
  opts = opts || {};
  const t = document.getElementById(id);
  if (!t) return;
  const tb = t.tBodies[0], tf = t.tFoot;
  const skip = opts.noFilter || [];
  let cols = opts.cols;
  if (!cols) {
    const r = tb && tb.rows[0];
    cols = r ? r.cells.length : (t.tHead.rows[0] ? t.tHead.rows[0].cells.length : 0);
  }
  if (!cols) return;

  const BLANK = '(空白)';

  // Collect unique values per column from data rows, plus whether any row is blank
  const colVals = [], colHasBlank = [];
  for (let i = 0; i < cols; i++) {
    if (skip.indexOf(i) >= 0) { colVals.push(null); colHasBlank.push(false); continue; }
    const vals = new Set();
    let hasBlank = false;
    for (const row of tb.rows) {
      if (row.dataset.nofilter) continue;
      const c = row.cells[i];
      const txt = c ? c.textContent.trim() : '';
      if (txt) vals.add(txt); else hasBlank = true;
    }
    colVals.push([...vals].sort((a, b) => a.localeCompare(b, 'zh-TW')));
    colHasBlank.push(hasBlank);
  }

  // colState[i]: null = no filter (everything shown); Set = the values allowed through (BLANK included when relevant)
  const colState = new Array(cols).fill(null);
  const buttons = new Array(cols).fill(null);

  // Build filter row with Excel-style checkbox dropdown buttons
  const fr = document.createElement('tr');
  fr.className = 'filter-row';
  for (let i = 0; i < cols; i++) {
    const th = document.createElement('th');
    const vals = colVals[i];
    if (vals && (vals.length > 0 || colHasBlank[i])) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'filter-btn';
      btn.innerHTML = `<span class='filter-btn-label'>全部</span><span class='filter-btn-arrow'>▾</span>`;
      btn.addEventListener('click', (e) => { e.stopPropagation(); openPanel(i, btn); });
      th.appendChild(btn);
      buttons[i] = btn;
    }
    fr.appendChild(th);
  }
  t.tHead.appendChild(fr);

  // Fix sticky top offset: filter row sits below existing header rows
  requestAnimationFrame(() => {
    let h = 0;
    for (const row of t.tHead.rows) {
      if (row === fr) break;
      h += row.offsetHeight;
    }
    for (const th of fr.children) { th.style.position = 'sticky'; th.style.top = h + 'px'; }
  });

  function pn(s) { return parseFloat(String(s).replace(/,/g, '')) || 0; }

  let openPanelEl = null;
  function closePanel() { if (openPanelEl) { openPanelEl.remove(); openPanelEl = null; } }
  document.addEventListener('click', closePanel);
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closePanel(); });

  function updateBtnLabel(colIdx) {
    const btn = buttons[colIdx];
    if (!btn) return;
    const lbl = btn.querySelector('.filter-btn-label');
    const st = colState[colIdx];
    if (st === null) { lbl.textContent = '全部'; btn.classList.remove('active'); }
    else { lbl.textContent = '已選(' + st.size + ')'; btn.classList.add('active'); }
  }

  function openPanel(colIdx, btn) {
    closePanel();
    const allVals = (colVals[colIdx] || []).slice();
    if (colHasBlank[colIdx]) allVals.unshift(BLANK);
    const selected = colState[colIdx] ? colState[colIdx] : new Set(allVals);

    const panel = document.createElement('div');
    panel.className = 'filter-panel';
    panel.addEventListener('click', e => e.stopPropagation());

    const search = document.createElement('input');
    search.type = 'text';
    search.className = 'filter-panel-search';
    search.placeholder = '搜尋…';
    panel.appendChild(search);

    const actions = document.createElement('div');
    actions.className = 'filter-panel-actions';
    const allLbl = document.createElement('label');
    const allChk = document.createElement('input');
    allChk.type = 'checkbox';
    allLbl.appendChild(allChk);
    allLbl.appendChild(document.createTextNode('全選'));
    actions.appendChild(allLbl);
    const clearLink = document.createElement('a');
    clearLink.href = '#';
    clearLink.className = 'filter-clear';
    clearLink.textContent = '清除篩選';
    actions.appendChild(clearLink);
    panel.appendChild(actions);

    if (colHasBlank[colIdx]) {
      const quick = document.createElement('a');
      quick.href = '#';
      quick.className = 'filter-panel-quick';
      quick.textContent = '僅顯示非空白';
      quick.addEventListener('click', e => {
        e.preventDefault();
        for (const c of checks) c.chk.checked = c.v !== BLANK;
        syncAllState();
      });
      panel.appendChild(quick);
    }

    const list = document.createElement('div');
    list.className = 'filter-panel-list';
    const checks = [];
    for (const v of allVals) {
      const lbl = document.createElement('label');
      const chk = document.createElement('input');
      chk.type = 'checkbox';
      chk.value = v;
      chk.checked = selected.has(v);
      chk.addEventListener('change', syncAllState);
      lbl.appendChild(chk);
      lbl.appendChild(document.createTextNode(v));
      list.appendChild(lbl);
      checks.push({ lbl, chk, v });
    }
    panel.appendChild(list);

    function syncAllState() {
      const visible = checks.filter(c => c.lbl.style.display !== 'none');
      allChk.checked = visible.length > 0 && visible.every(c => c.chk.checked);
      allChk.indeterminate = !allChk.checked && visible.some(c => c.chk.checked);
    }
    syncAllState();

    allChk.addEventListener('change', () => {
      for (const c of checks) if (c.lbl.style.display !== 'none') c.chk.checked = allChk.checked;
      syncAllState();
    });

    search.addEventListener('input', () => {
      const q = search.value.trim().toLowerCase();
      for (const c of checks) c.lbl.style.display = !q || c.v.toLowerCase().indexOf(q) >= 0 ? '' : 'none';
      syncAllState();
    });

    clearLink.addEventListener('click', e => {
      e.preventDefault();
      colState[colIdx] = null;
      updateBtnLabel(colIdx);
      closePanel();
      run();
    });

    const btns = document.createElement('div');
    btns.className = 'filter-panel-buttons';
    const okBtn = document.createElement('button');
    okBtn.type = 'button'; okBtn.className = 'btn btn-sm'; okBtn.textContent = '確定';
    okBtn.addEventListener('click', () => {
      const picked = new Set(checks.filter(c => c.chk.checked).map(c => c.v));
      colState[colIdx] = picked.size === allVals.length ? null : picked;
      updateBtnLabel(colIdx);
      closePanel();
      run();
    });
    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button'; cancelBtn.className = 'btn btn-sm btn-outline'; cancelBtn.textContent = '取消';
    cancelBtn.addEventListener('click', closePanel);
    btns.appendChild(okBtn); btns.appendChild(cancelBtn);
    panel.appendChild(btns);

    document.body.appendChild(panel);
    const r = btn.getBoundingClientRect();
    let left = r.left;
    if (left + panel.offsetWidth > window.innerWidth - 8) left = window.innerWidth - panel.offsetWidth - 8;
    if (left < 8) left = 8;
    panel.style.left = left + 'px';
    panel.style.top = (r.bottom + 4) + 'px';
    openPanelEl = panel;
    search.focus();
    requestAnimationFrame(() => {
      const ph = panel.offsetHeight;
      if (r.bottom + 4 + ph > window.innerHeight - 8) panel.style.top = Math.max(8, r.top - ph - 4) + 'px';
    });
  }

  function run() {
    const vis = [];
    for (const row of tb.rows) {
      if (row.dataset.nofilter) { vis.push(row); continue; }
      let ok = true;
      for (let i = 0; i < cols && ok; i++) {
        const st = colState[i];
        if (!st) continue;
        const c = row.cells[i];
        const txt = c ? c.textContent.trim() : '';
        if (!st.has(txt || BLANK)) ok = false;
      }
      if (ok && opts.extraFilter && !opts.extraFilter(row)) ok = false;
      row.style.display = ok ? '' : 'none';
      if (ok) vis.push(row);
    }
    if (tf && opts.sumCols) {
      for (const sc of opts.sumCols) {
        let sum = 0;
        for (const r of vis) sum += pn(r.cells[sc.col] ? r.cells[sc.col].textContent : '');
        const el = sc.id ? document.getElementById(sc.id) : (tf.rows[0] && tf.rows[0].cells[sc.col]);
        if (el) el.textContent = Math.round(sum).toLocaleString('zh-TW');
      }
    }
    if (opts.onFilter) opts.onFilter(vis);
  }
  return { run };
}
";

    // Prevents the mouse-wheel-over-a-focused-number-input browser behavior from silently changing values.
    public static string NoScrollJs => @"
document.addEventListener('wheel', function(e) {
  if (document.activeElement && document.activeElement.tagName === 'INPUT' && document.activeElement.type === 'number')
    document.activeElement.blur();
}, { passive: true });
";

    // Exports a rendered <table> (after filtering/client-side computation) to a real .xlsx via the server.
    public static string ExportJs => @"
async function exportTableToExcel(tableId, filename, opts) {
  opts = opts || {};
  const t = document.getElementById(tableId);
  if (!t) return;
  const skip = new Set(opts.skipCols || []);
  let headers = opts.headers;
  if (!headers) headers = Array.from(t.tHead.rows[0].cells).map(c => c.textContent.trim());
  headers = headers.filter((_, i) => !skip.has(i));

  const rows = [];
  for (const r of t.tBodies[0].rows) {
    if (r.dataset.nofilter) continue;
    if (r.style.display === 'none') continue;
    const cells = Array.from(r.cells).map(c => c.textContent.trim());
    rows.push(cells.filter((_, i) => !skip.has(i)));
  }

  const btn = opts.btn;
  const oldText = btn ? btn.textContent : null;
  if (btn) { btn.textContent = '匯出中...'; btn.disabled = true; }
  try {
    const res = await fetch('/api/export/excel', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fileName: filename, sheetName: opts.sheetName || filename, headers, rows })
    });
    if (!res.ok) { alert('匯出失敗'); return; }
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename.endsWith('.xlsx') ? filename : filename + '.xlsx';
    document.body.appendChild(a); a.click(); document.body.removeChild(a);
    URL.revokeObjectURL(url);
  } finally {
    if (btn) { btn.textContent = oldText; btn.disabled = false; }
  }
}

// Downloads headers/rows as a comma-separated .csv file (client-side, no server round trip).
function csvEscape(v) {
  v = v == null ? '' : String(v);
  var q = String.fromCharCode(34);
  return (v.indexOf(',') >= 0 || v.indexOf(q) >= 0 || v.indexOf('\r') >= 0 || v.indexOf('\n') >= 0)
    ? q + v.split(q).join(q + q) + q : v;
}
function downloadCsv(filename, headers, rows) {
  const lines = [headers, ...rows].map(r => r.map(csvEscape).join(','));
  const csv = lines.join('\r\n');
  const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename.endsWith('.csv') ? filename : filename + '.csv';
  document.body.appendChild(a); a.click(); document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
";

    public static string Page(string title, string cat, string sub, string body) =>
        $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>{title}</title><style>{Css}</style><script>{TableJs}{NoScrollJs}{ExportJs}</script></head><body>
{Nav(cat, sub)}{body}</body></html>";
}
