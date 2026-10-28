using System.Text.Json;
using System.Text.Json.Serialization;

// 股票質借（質押借款）管理：每筆質借獨立追蹤本金、利率、累計利息，
// 支援利率變更與還款（現金／賣出）時「結算」目前區段利息後再變動本金。
public static class PledgeModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string PledgesFile = Path.Combine(DataDir, "stock_pledges.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/stocks/pledge", ListPage);
        app.MapPost("/api/stocks/pledge", AddPledge);
        app.MapPut("/api/stocks/pledge/{id}", UpdatePledge);
        app.MapPost("/api/stocks/pledge/{id}/rate", ChangeRate);
        app.MapPost("/api/stocks/pledge/{id}/repay", Repay);
        app.MapDelete("/api/stocks/pledge/{id}", DeletePledge);
    }

    // ── List page ────────────────────────────────────────────────────────

    static async Task ListPage(HttpContext ctx)
    {
        var pledges = LoadPledges().OrderBy(p => p.ClosedDate != null).ThenBy(p => p.StockCode).ToList();
        var pledgesJson = JsonSerializer.Serialize(pledges, JsonOpts);

        var rows = pledges.Count == 0 ? "" : string.Join("", pledges.Select(p =>
        {
            // 已結清的列在起息(結算)日欄顯示結清日，不另佔股票欄寬度
            var sinceCell = p.ClosedDate != null ? $"<span class='tag' style='background:#eee;color:#888'>結清 {p.ClosedDate}</span>" : p.InterestSince;
            return $@"
<tr id='row-{p.Id}' class='{(p.ClosedDate != null ? "closed-row" : "")}'>
  <td>{System.Net.WebUtility.HtmlEncode(p.StockCode)} <span style='color:#666'>{System.Net.WebUtility.HtmlEncode(p.StockName)}</span></td>
  <td class='num'>{p.Shares:N0}</td>
  <td class='num' id='price-{p.Id}'><span class='loading'>—</span></td>
  <td class='num' id='mv-{p.Id}'>—</td>
  <td class='num'>{p.Principal:N0}</td>
  <td class='num'>{p.Rate:F3}%</td>
  <td class='num' id='daily-{p.Id}'>—</td>
  <td class='num' id='interest-{p.Id}'>—</td>
  <td class='num' id='owed-{p.Id}'>—</td>
  <td class='num' id='mr-{p.Id}'>—</td>
  <td>{sinceCell}</td>
  <td>{System.Net.WebUtility.HtmlEncode(p.CustodyAccount)}</td>
  <td><button class='btn btn-sm btn-outline act-btn' onclick=""openActMenu(event,'{p.Id}')"">操作 ▾</button></td>
</tr>
<tr id='panel-{p.Id}' style='display:none' data-nofilter='1'>
  <td colspan='13' style='background:#fafbfe;border-bottom:2px solid #dde4f5;padding:0'>
    <div style='padding:14px 18px' id='panel-body-{p.Id}'></div>
  </td>
</tr>";
        }));

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>🔒 股票質借</h1>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('pledge-table','股票質借',{{skipCols:[12],btn:this,includeFoot:true}})"">⬇ 下載Excel</button>
  <button class='btn btn-outline' onclick=""toggleImport()"">📄 匯入對帳單</button>
  <button class='btn' onclick=""togglePanel('new','new')"">＋ 新增質借</button>
</div>

<div id='importBox' class='form-card' style='display:none;margin-bottom:16px;max-width:none'>
  <strong>📄 依元大證金月對帳單更新質借</strong>
  <p style='font-size:.85rem;color:#666;margin:6px 0 10px'>選擇對帳單 PDF 或截圖，也可以直接按 Ctrl+V 貼上截圖。PDF 最準；截圖請放大到數字清楚，表格太長可分成多張一起選，重複的列會自動合併。辨識在瀏覽器內進行（第一次需下載辨識模型），完成後先列出變更，勾選確認才會寫入。</p>
  <div class='mini-row'>
    <div class='field'><label>對帳單 PDF／截圖（可多選）</label><input type='file' id='imp-file' accept='application/pdf,image/*' multiple style='width:280px'></div>
    <button class='btn btn-sm' id='imp-btn' onclick='parseStatement()'>開始辨識</button>
    <span id='imp-status' style='font-size:.85rem;color:#888'></span>
    <a href='#' onclick='clearPdfPasswords();return false' style='font-size:.8rem;color:#888;margin-left:auto'>清除記住的 PDF 密碼</a>
  </div>
  <div id='imp-raw'></div>
  <div id='imp-result'></div>
</div>

<div class='cards' id='summaryCards'>
  <div class='card'><div class='lbl'>借款金額合計</div><div class='val pos' id='s-principal'>計算中…</div></div>
  <div class='card'><div class='lbl'>市值合計</div><div class='val' id='s-mv'>—</div></div>
  <div class='card'><div class='lbl'>應償還金額合計</div><div class='val pos' id='s-owed'>—</div></div>
  <div class='card'><div class='lbl'>整體維持率<small style='color:#aaa'>（市值÷應償還）</small></div><div class='val' id='s-mr'>—</div></div>
</div>

<div class='table-wrap'>
<table id='pledge-table'>
<thead><tr>
  <th>股票</th><th class='num'>張數</th><th class='num'>即時股價</th><th class='num'>市值</th>
  <th class='num'>借款金額</th><th class='num'>目前利率</th><th class='num'>日利息</th>
  <th class='num'>累計利息</th><th class='num'>應償還金額</th><th class='num'>維持率</th>
  <th>起息(結算)日</th><th>集保帳號</th><th></th>
</tr></thead>
<tbody>
<tr id='row-new' style='display:none' data-nofilter='1'></tr>
<tr id='panel-new' style='display:none' data-nofilter='1'>
  <td colspan='13' style='background:#f0f4ff;border-bottom:2px solid #c0d0f0;padding:0'>
    <div style='padding:14px 18px' id='panel-body-new'></div>
  </td>
</tr>
{rows}
</tbody>
<tfoot><tr>
  <td>篩選合計</td>
  <td class='num' id='ft-shares'>—</td>
  <td></td>
  <td class='num' id='ft-mv'>—</td>
  <td class='num' id='ft-principal'>—</td>
  <td></td>
  <td class='num' id='ft-daily'>—</td>
  <td class='num' id='ft-interest'>—</td>
  <td class='num' id='ft-owed'>—</td>
  <td class='num' id='ft-mr'>—</td>
  <td></td><td></td><td></td>
</tr></tfoot>
</table>
</div>
{(pledges.Count == 0 ? "<div class='empty-state'><div class='icon'>🔒</div><p>尚無質借資料</p></div>" : "")}

<p style='font-size:.8rem;color:#999;margin-top:8px'>維持率 = 市值 ÷ 應償還金額 × 100%。一般追繳線：130%，警戒線：140%，請依各券商規定為準。利率變更、還款皆會先依目前利率結算利息並累加，再套用新利率／扣減本金。</p>
<div id='msg' style='margin-top:16px'></div>

<style>
th.num,td.num{{text-align:right}}
.mr-ok{{color:#080;font-weight:700}}
.mr-warn{{color:#d80;font-weight:700}}
.mr-danger{{color:#c00;font-weight:700}}
.closed-row{{opacity:.55}}
#pledge-table>tbody>tr:not([data-nofilter])>td{{white-space:nowrap;padding-top:6px;padding-bottom:6px}}
.act-btn{{width:auto;margin:0;padding:3px 10px}}
.act-menu{{position:absolute;z-index:50;background:#fff;border:1px solid #d5dbe8;border-radius:8px;box-shadow:0 6px 18px rgba(0,0,0,.12);padding:4px 0;min-width:120px}}
.act-menu button{{display:block;width:100%;margin:0;padding:7px 14px;border:0;background:none;text-align:left;font-size:.88rem;color:#333;cursor:pointer;border-radius:0}}
.act-menu button:hover:not(:disabled){{background:#f0f4ff}}
.act-menu button:disabled{{color:#bbb;cursor:default}}
.act-menu button.danger{{color:#c0392b}}
.mini-field{{margin-bottom:0}}
.mini-row{{display:flex;gap:10px;align-items:flex-end;flex-wrap:wrap;margin-bottom:10px}}
.mini-row .field{{margin:0}}
.mini-row label{{font-size:.82rem}}
.mini-row input,.mini-row select{{width:150px}}
.hist-table{{width:auto;font-size:.83rem;border-collapse:collapse;margin:6px 0;background:transparent;box-shadow:none}}
.hist-table th{{padding:4px 10px;text-align:left;background:#f0f4ff}}
.hist-table td{{padding:4px 10px;border-bottom:1px solid #eee}}
</style>

<script>
const pledges = {pledgesJson};
const today = new Date();
const todayStr = today.toISOString().slice(0,10);
let pledgeTable = null;

function fmt(n) {{ return (isNaN(n) || n == null) ? '0' : Math.round(n).toLocaleString('zh-TW'); }}
function fmtD(n) {{ return n.toFixed(1) + '%'; }}
function daysDiff(from, to) {{ return Math.max(0, Math.floor((to - new Date(from)) / 86400000)); }}

// ── 利息計算：目前區段（自 interestSince 至今）尚未結算的利息 ──────────────
function currentSegmentInterest(p, asOf) {{
  const days = daysDiff(p.interestSince, asOf || today);
  return p.principal * p.rate / 100 / 365 * days;
}}
function totalInterest(p, asOf) {{ return (p.accumulatedInterest || 0) + currentSegmentInterest(p, asOf); }}

// ── 主表即時計算 ─────────────────────────────────────────────────────────
async function refreshTable() {{
  const codes = [...new Set(pledges.map(p => p.stockCode).filter(Boolean))].join(',');
  let prices = {{}};
  if (codes) {{
    try {{ const r = await fetch('/api/stocks/prices?codes=' + codes); prices = await r.json(); }} catch(e) {{}}
  }}

  for (const p of pledges) {{
    const price = prices[p.stockCode] || 0;
    const mv = price * p.shares * 1000;
    const interest = totalInterest(p);
    const owed = p.principal + interest;
    const dailyInt = p.principal * p.rate / 100 / 365;
    const mr = owed > 0 && mv > 0 ? mv / owed * 100 : 0;

    const set = (id, html) => {{ const el = document.getElementById(id); if (el) el.innerHTML = html; }};
    set('price-' + p.id, price ? price.toFixed(2) : '<span class=loading>—</span>');
    set('mv-' + p.id, mv > 0 ? fmt(mv) : '—');
    set('daily-' + p.id, dailyInt.toFixed(2));
    set('interest-' + p.id, '<strong>' + fmt(interest) + '</strong>');
    set('owed-' + p.id, '<strong style=\'color:#c00\'>' + fmt(owed) + '</strong>');
    if (p.closedDate) {{ set('mr-' + p.id, '—'); }}
    else if (mr > 0) {{
      const cls = mr < 130 ? 'mr-danger' : mr < 140 ? 'mr-warn' : 'mr-ok';
      const warn = mr < 130 ? ' ⚠️追繳' : mr < 140 ? ' ⚠️警戒' : '';
      set('mr-' + p.id, `<span class='${{cls}}'>${{fmtD(mr)}}${{warn}}</span>`);
    }} else set('mr-' + p.id, '—');
  }}
  // 個股市值等欄位為非同步填入，資料到位後重算篩選合計與摘要卡片
  if (pledgeTable) pledgeTable.run();
}}

// ── 篩選後加總（表尾合計 + 摘要卡片）───────────────────────────────────────
function pnum(s) {{ return parseFloat(String(s || '').replace(/,/g, '')) || 0; }}
function updatePledgeTotals(vis) {{
  let shares = 0, mv = 0, principal = 0, daily = 0, interest = 0, owed = 0;
  for (const r of vis) {{
    if (r.dataset.nofilter) continue;
    if (r.classList && r.classList.contains('closed-row')) continue; // 已結清不計入
    shares    += pnum(r.cells[1] ? r.cells[1].textContent : '');
    mv        += pnum(r.cells[3] ? r.cells[3].textContent : '');
    principal += pnum(r.cells[4] ? r.cells[4].textContent : '');
    daily     += pnum(r.cells[6] ? r.cells[6].textContent : '');
    interest  += pnum(r.cells[7] ? r.cells[7].textContent : '');
    owed      += pnum(r.cells[8] ? r.cells[8].textContent : '');
  }}
  const setInt = (id, v) => {{ const el = document.getElementById(id); if (el) el.textContent = fmt(v); }};
  setInt('ft-shares', shares);
  setInt('ft-mv', mv);
  setInt('ft-principal', principal);
  const dEl = document.getElementById('ft-daily'); if (dEl) dEl.textContent = daily.toFixed(2);
  setInt('ft-interest', interest);
  setInt('ft-owed', owed);

  const mr = owed > 0 && mv > 0 ? mv / owed * 100 : 0;
  const cls = mr < 130 ? 'mr-danger' : mr < 140 ? 'mr-warn' : 'mr-ok';
  const ftMr = document.getElementById('ft-mr');
  if (ftMr) ftMr.innerHTML = mr > 0 ? `<span class='${{cls}}'>${{fmtD(mr)}}</span>` : '—';

  // 摘要卡片同步篩選；整體維持率 = 市值合計 ÷ 應償還金額合計
  document.getElementById('s-principal').textContent = fmt(principal) + ' 元';
  document.getElementById('s-mv').textContent = fmt(mv) + ' 元';
  document.getElementById('s-owed').textContent = fmt(owed) + ' 元';
  const smr = document.getElementById('s-mr');
  if (mr > 0) {{ smr.className = 'val ' + cls; smr.textContent = fmtD(mr); }}
  else {{ smr.className = 'val'; smr.textContent = '—'; }}
}}

// ── 面板切換：同一筆同時只顯示一個面板 ──────────────────────────────────
let openPanelId = null;
function togglePanel(id, type) {{
  const row = document.getElementById('panel-' + id);
  const wasOpenSame = openPanelId === id && row.style.display !== 'none';
  if (openPanelId && openPanelId !== id) {{
    const prevRow = document.getElementById('panel-' + openPanelId);
    if (prevRow) prevRow.style.display = 'none';
  }}
  if (wasOpenSame) {{
    row.style.display = 'none';
    openPanelId = null;
    return;
  }}
  renderPanel(id, type);
  row.style.display = '';
  openPanelId = id;
}}

function renderPanel(id, type) {{
  const body = document.getElementById('panel-body-' + id);
  if (id === 'new') {{ body.innerHTML = newForm(); return; }}
  const p = pledges.find(x => x.id === id);
  if (type === 'edit') body.innerHTML = editForm(p);
  else if (type === 'rate') body.innerHTML = rateForm(p);
  else if (type === 'sell') body.innerHTML = sellForm(p);
  else if (type === 'cash') body.innerHTML = cashForm(p);
  else if (type === 'hist') body.innerHTML = histView(p);
}}

function newForm() {{
  return `
  <strong style='font-size:.9rem'>📊 新增質借</strong>
  <div class='mini-row' style='margin-top:10px'>
    <div class='field'><label>股號</label><input id='n-code' placeholder='如：0050' oninput='newStockLookup()'></div>
    <div class='field'><label>股名</label><input id='n-name' placeholder='自動帶入'></div>
    <div class='field'><label>張數</label><input type='number' id='n-shares' placeholder='如：30'></div>
    <div class='field'><label>集保帳號</label><input id='n-custody' placeholder='如：國泰敦南'></div>
  </div>
  <div class='mini-row'>
    <div class='field'><label>借款金額（元）</label><input type='number' id='n-principal' placeholder='如：1000000'></div>
    <div class='field'><label>借款利率（%）</label><input type='number' step='0.001' id='n-rate' placeholder='如：2.480'></div>
    <div class='field'><label>起息日</label><input type='date' id='n-since' value='${{todayStr}}'></div>
  </div>
  <div class='field' style='max-width:400px'><label>備註</label><input id='n-notes' placeholder='選填'></div>
  <div class='actions' style='margin-top:10px'>
    <button class='btn btn-sm' onclick='submitNew()'>確認新增</button>
    <button class='btn btn-sm btn-outline' onclick=""togglePanel('new','new')"">取消</button>
  </div>`;
}}

async function newStockLookup() {{
  const code = document.getElementById('n-code').value.trim();
  if (code.length < 4) return;
  try {{
    const r = await fetch('/api/stocks/info?codes=' + code);
    const data = await r.json();
    if (data[code] && data[code].name) document.getElementById('n-name').value = data[code].name;
  }} catch(e) {{}}
}}

async function submitNew() {{
  const gv = id => document.getElementById(id).value;
  const req = {{
    stockCode: gv('n-code').trim(), stockName: gv('n-name').trim(),
    shares: parseInt(gv('n-shares')) || 0, principal: parseFloat(gv('n-principal')) || 0,
    rate: parseFloat(gv('n-rate')) || 0, interestSince: gv('n-since'),
    custodyAccount: gv('n-custody').trim(), notes: gv('n-notes').trim()
  }};
  if (!req.stockCode || req.shares <= 0 || req.principal <= 0 || req.rate <= 0 || !req.interestSince) {{
    showMsg('請完整填寫股號、張數、借款金額、利率與起息日', 'err'); return;
  }}
  const r = await fetch('/api/stocks/pledge', {{method:'POST', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) location.reload(); else showMsg(await r.text() || '新增失敗', 'err');
}}

function editForm(p) {{
  return `
  <strong style='font-size:.9rem'>✏️ 編輯（僅供修正錯誤資料，不會結算利息；正常利率變更／還款請用對應按鈕）</strong>
  <div class='mini-row' style='margin-top:10px'>
    <div class='field'><label>股號</label><input id='e-code' value=""${{p.stockCode}}""></div>
    <div class='field'><label>股名</label><input id='e-name' value=""${{p.stockName}}""></div>
    <div class='field'><label>張數</label><input type='number' id='e-shares' value=""${{p.shares}}""></div>
    <div class='field'><label>集保帳號</label><input id='e-custody' value=""${{p.custodyAccount || ''}}""></div>
  </div>
  <div class='mini-row'>
    <div class='field'><label>借款金額（元）</label><input type='number' id='e-principal' value=""${{p.principal}}""></div>
    <div class='field'><label>借款利率（%）</label><input type='number' step='0.001' id='e-rate' value=""${{p.rate}}""></div>
    <div class='field'><label>起息(結算)日</label><input type='date' id='e-since' value=""${{p.interestSince}}""></div>
    <div class='field'><label>累計利息（元）</label><input type='number' id='e-accint' value=""${{Math.round(p.accumulatedInterest || 0)}}""></div>
  </div>
  <div class='field' style='max-width:400px'><label>備註</label><input id='e-notes' value=""${{p.notes || ''}}""></div>
  <div class='actions' style='margin-top:10px'>
    <button class='btn btn-sm' onclick=""submitEdit('${{p.id}}')"">儲存修正</button>
    <button class='btn btn-sm btn-outline' onclick=""togglePanel('${{p.id}}','edit')"">取消</button>
  </div>`;
}}

async function submitEdit(id) {{
  const gv = eid => document.getElementById(eid).value;
  const req = {{
    stockCode: gv('e-code').trim(), stockName: gv('e-name').trim(),
    shares: parseInt(gv('e-shares')) || 0, principal: parseFloat(gv('e-principal')) || 0,
    rate: parseFloat(gv('e-rate')) || 0, interestSince: gv('e-since'),
    accumulatedInterest: parseFloat(gv('e-accint')) || 0,
    custodyAccount: gv('e-custody').trim(), notes: gv('e-notes').trim()
  }};
  const r = await fetch('/api/stocks/pledge/' + id, {{method:'PUT', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) location.reload(); else showMsg(await r.text() || '儲存失敗', 'err');
}}

function rateForm(p) {{
  const interest = currentSegmentInterest(p);
  return `
  <strong style='font-size:.9rem'>📈 更改利率（自生效日起，先以目前利率 ${{p.rate}}% 結算 ${{p.interestSince}} 至生效日的利息，加總至累計利息）</strong>
  <p style='font-size:.82rem;color:#888;margin:6px 0'>目前區段（自 ${{p.interestSince}} 起）預估待結算利息：<strong>${{fmt(interest)}} 元</strong>（以生效日重新計算）</p>
  <div class='mini-row'>
    <div class='field'><label>新利率（%）</label><input type='number' step='0.001' id='r-rate-${{p.id}}' placeholder='如：2.500'></div>
    <div class='field'><label>生效日期</label><input type='date' id='r-date-${{p.id}}' value=""${{todayStr}}""></div>
  </div>
  <div class='actions'>
    <button class='btn btn-sm' onclick=""submitRate('${{p.id}}')"">確認變更</button>
    <button class='btn btn-sm btn-outline' onclick=""togglePanel('${{p.id}}','rate')"">取消</button>
  </div>`;
}}

async function submitRate(id) {{
  const rate = parseFloat(document.getElementById('r-rate-' + id).value);
  const effectiveDate = document.getElementById('r-date-' + id).value;
  if (!rate || rate <= 0) {{ showMsg('請輸入有效的新利率', 'err'); return; }}
  if (!effectiveDate) {{ showMsg('請輸入生效日期', 'err'); return; }}
  const r = await fetch('/api/stocks/pledge/' + id + '/rate', {{method:'POST', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify({{rate, effectiveDate}})}});
  if (r.ok) location.reload(); else showMsg(await r.text() || '變更失敗（請確認生效日不早於起息／結算日）', 'err');
}}

function sellForm(p) {{
  return `
  <strong style='font-size:.9rem'>💹 賣出還款（先結算目前利息，再以還款金額扣減借款本金，並扣減張數）</strong>
  <div class='mini-row' style='margin-top:10px'>
    <div class='field'><label>賣出張數</label><input type='number' id='s-shares-${{p.id}}' placeholder='最多 ${{p.shares}} 張' oninput=""sellPreview('${{p.id}}')""></div>
    <div class='field'><label>賣出成交價</label><input type='number' step='0.01' id='s-price-${{p.id}}' placeholder='選填，用於試算金額' oninput=""sellPreview('${{p.id}}')""></div>
    <div class='field'><label>還款金額（元）</label><input type='number' id='s-amount-${{p.id}}' placeholder='可用左方試算或手動輸入'></div>
    <div class='field'><label>日期</label><input type='date' id='s-date-${{p.id}}' value=""${{todayStr}}""></div>
  </div>
  <div class='actions'>
    <button class='btn btn-sm' onclick=""submitRepay('${{p.id}}','sell')"">確認還款</button>
    <button class='btn btn-sm btn-outline' onclick=""togglePanel('${{p.id}}','sell')"">取消</button>
  </div>`;
}}

function sellPreview(id) {{
  const shares = parseFloat(document.getElementById('s-shares-' + id).value) || 0;
  const price = parseFloat(document.getElementById('s-price-' + id).value) || 0;
  if (shares > 0 && price > 0) document.getElementById('s-amount-' + id).value = Math.round(shares * price * 1000);
}}

function cashForm(p) {{
  return `
  <strong style='font-size:.9rem'>💵 現金還款（先結算目前利息，再以還款金額扣減借款本金）</strong>
  <div class='mini-row' style='margin-top:10px'>
    <div class='field'><label>還款金額（元）</label><input type='number' id='c-amount-${{p.id}}' placeholder='部分或全額皆可，最多 ${{Math.round(p.principal)}}'></div>
    <div class='field'><label>日期</label><input type='date' id='c-date-${{p.id}}' value=""${{todayStr}}""></div>
  </div>
  <div class='actions'>
    <button class='btn btn-sm' onclick=""submitRepay('${{p.id}}','cash')"">確認還款</button>
    <button class='btn btn-sm btn-outline' onclick=""togglePanel('${{p.id}}','cash')"">取消</button>
  </div>`;
}}

async function submitRepay(id, type) {{
  const amount = parseFloat(document.getElementById((type === 'sell' ? 's-amount-' : 'c-amount-') + id).value) || 0;
  const date = document.getElementById((type === 'sell' ? 's-date-' : 'c-date-') + id).value;
  const sharesSold = type === 'sell' ? (parseInt(document.getElementById('s-shares-' + id).value) || 0) : 0;
  if (amount <= 0) {{ showMsg('請輸入還款金額', 'err'); return; }}
  if (type === 'sell' && sharesSold <= 0) {{ showMsg('請輸入賣出張數', 'err'); return; }}
  if (!date) {{ showMsg('請輸入日期', 'err'); return; }}
  const r = await fetch('/api/stocks/pledge/' + id + '/repay', {{
    method:'POST', headers:{{'Content-Type':'application/json'}},
    body:JSON.stringify({{type, amount, sharesSold, date}})
  }});
  if (r.ok) location.reload(); else showMsg(await r.text() || '還款失敗（請確認日期不早於起息／結算日）', 'err');
}}

function histView(p) {{
  const events = p.events || [];
  if (events.length === 0) return `<p style='color:#888;font-size:.85rem;margin:0'>尚無異動紀錄。</p>`;
  let html = `<strong style='font-size:.9rem'>📜 異動歷史</strong>
    <table class='hist-table'><thead><tr>
      <th>日期</th><th>類型</th><th style='text-align:right'>利率</th>
      <th style='text-align:right'>金額</th><th style='text-align:right'>賣出張數</th>
      <th style='text-align:right'>結算利息</th><th style='text-align:right'>異動後本金</th>
    </tr></thead><tbody>`;
  for (const e of events) {{
    html += `<tr>
      <td>${{e.date}}</td><td>${{e.type}}</td>
      <td style='text-align:right'>${{e.rate != null ? e.rate + '%' : '—'}}</td>
      <td style='text-align:right'>${{e.amount != null ? fmt(e.amount) : '—'}}</td>
      <td style='text-align:right'>${{e.sharesSold != null && e.sharesSold > 0 ? e.sharesSold : '—'}}</td>
      <td style='text-align:right'>${{fmt(e.settledInterest)}}</td>
      <td style='text-align:right'>${{fmt(e.principalAfter)}}</td>
    </tr>`;
  }}
  html += '</tbody></table>';
  return html;
}}

// 每列只放一個「操作」按鈕，點開後才列出各動作，避免一排按鈕把列撐高
function closeActMenu() {{ document.querySelector('.act-menu')?.remove(); }}
function openActMenu(ev, id) {{
  ev.stopPropagation();
  const wasOpen = document.querySelector('.act-menu')?.dataset.id === id;
  closeActMenu();
  if (wasOpen) return;
  const closed = !!pledges.find(p => p.id === id)?.closedDate;
  const items = [
    ['編輯', () => togglePanel(id, 'edit')],
    ['改利率', () => togglePanel(id, 'rate'), closed],
    ['賣出還款', () => togglePanel(id, 'sell'), closed],
    ['現金還款', () => togglePanel(id, 'cash'), closed],
    ['歷史', () => togglePanel(id, 'hist')],
    ['刪除', () => delPledge(id), false, 'danger'],
  ];
  const m = document.createElement('div');
  m.className = 'act-menu'; m.dataset.id = id;
  for (const [label, fn, disabled, cls] of items) {{
    const b = document.createElement('button');
    b.textContent = label; b.disabled = !!disabled; if (cls) b.className = cls;
    b.onclick = () => {{ closeActMenu(); fn(); }};
    m.appendChild(b);
  }}
  document.body.appendChild(m);
  const r = ev.currentTarget.getBoundingClientRect();
  m.style.top = (window.scrollY + r.bottom + 4) + 'px';
  m.style.left = (window.scrollX + Math.max(8, r.right - m.offsetWidth)) + 'px';
}}
document.addEventListener('click', closeActMenu);
window.addEventListener('resize', closeActMenu);

async function delPledge(id) {{
  if (!confirm('確定刪除此質借紀錄？此動作無法復原。')) return;
  const r = await fetch('/api/stocks/pledge/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload(); else showMsg('刪除失敗', 'err');
}}

// ── 對帳單匯入 ───────────────────────────────────────────────────────────
let importStatement = null;
const IMPORT_ACTIONS = {{
  'update':        ['更新', '#0066cc'],
  'close':         ['已還款結清', '#c60'],
  'create':        ['新借款', '#080'],
  'create-closed': ['新增（已還清）', '#888'],
  'unchanged':     ['無變動', '#aaa']
}};

function toggleImport() {{
  const box = document.getElementById('importBox');
  box.style.display = box.style.display === 'none' ? '' : 'none';
}}

function esc(s) {{ return String(s ?? '').replace(/[&<>""']/g, c => ({{'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}})[c]); }}

// 對帳單是「Microsoft Print to PDF」轉印檔，字已成圖形，所以在瀏覽器內 OCR：
// PDF 以 pdf.js 放大 4 倍繪成圖、截圖直接使用 → tesseract.js 辨識 → 依數字排列規則拆成各欄
{SharedLayout.PdfPasswordJs}
const PDFJS = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js';
const PDFJS_WORKER = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js';
const TESSERACT = 'https://cdn.jsdelivr.net/npm/tesseract.js@5.1.1/dist/tesseract.min.js';
let ocrWorker = null, ocrProgress = () => {{}};

function loadScript(src) {{
  return new Promise((ok, fail) => {{
    if (document.querySelector(`script[src=""${{src}}""]`)) return ok();
    const s = document.createElement('script');
    s.src = src; s.onload = ok; s.onerror = () => fail(new Error('無法載入 ' + src));
    document.head.appendChild(s);
  }});
}}

function impStatus(t) {{ document.getElementById('imp-status').textContent = t; }}

// 回傳每一頁的 canvas
async function fileToCanvases(file) {{
  if (file.type === 'application/pdf' || /\.pdf$/i.test(file.name || '')) {{
    await loadScript(PDFJS);
    pdfjsLib.GlobalWorkerOptions.workerSrc = PDFJS_WORKER;
    const pdf = await openPdfDocument(await file.arrayBuffer(), file.name);
    const pages = [];
    for (let n = 1; n <= pdf.numPages; n++) {{
      const page = await pdf.getPage(n);
      const vp = page.getViewport({{ scale: 4 }});
      const c = document.createElement('canvas');
      c.width = vp.width; c.height = vp.height;
      const ctx = c.getContext('2d');
      ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, c.width, c.height);
      await page.render({{ canvasContext: ctx, viewport: vp }}).promise;
      pages.push(c);
    }}
    return pages;
  }}
  const img = await createImageBitmap(file);
  // 截圖字太小時先放大，OCR 較準
  const k = Math.max(1, Math.min(4, 5000 / Math.max(img.width, img.height)));
  const c = document.createElement('canvas');
  c.width = Math.round(img.width * k); c.height = Math.round(img.height * k);
  const ctx = c.getContext('2d');
  ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, c.width, c.height);
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(img, 0, 0, c.width, c.height);
  return [c];
}}

function rotateCanvas(src, deg) {{
  if (!deg) return src;
  const c = document.createElement('canvas');
  const swap = deg % 180 !== 0;
  c.width = swap ? src.height : src.width; c.height = swap ? src.width : src.height;
  const ctx = c.getContext('2d');
  ctx.translate(c.width / 2, c.height / 2); ctx.rotate(deg * Math.PI / 180);
  ctx.drawImage(src, -src.width / 2, -src.height / 2);
  return c;
}}

async function getOcrWorker() {{
  await loadScript(TESSERACT);
  if (!ocrWorker) {{
    ocrWorker = await Tesseract.createWorker('eng', 1, {{
      logger: m => {{ if (m.status === 'recognizing text') ocrProgress(m.progress); }}
    }});
    // 整頁當成單一文字區塊，表格每列才會是一行
    await ocrWorker.setParameters({{ tessedit_pageseg_mode: '6', preserve_interword_spaces: '1' }});
  }}
  return ocrWorker;
}}

// 每列：序號 股號 名稱 借款日 │ 餘額張數 應繳 已繳 未繳 融資餘額 │ [沖銷方式 償還日] │ 沖銷張數 沖銷已繳 (A) (B) (C) (D)…(H)
// 合計列：對帳日 115/09/30 之後依序 15 個數字（無償還日）
function parseOcrText(text) {{
  const rows = [], problems = [];
  let statementDate = '', totals = null;
  const isRocDate = t => /^1[01]\d(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])$/.test(t);
  const num = t => {{ const s = t.replace(/[,.]/g, ''); return /^\d+$/.test(s) ? +s : null; }};
  // OCR 常把序號黏在股號前（1.0050、50050、352892）：去掉「序號.」，非 00 開頭的 5～6 碼取末 4 碼
  const normCode = t => {{
    t = t.replace(/^\d{{1,2}}[.,]/, '');
    return /^\d{{5,6}}$/.test(t) && !t.startsWith('00') ? t.slice(-4) : t;
  }};
  for (const line of text.split('\n')) {{
    const toks = line.replace(/[|\[\]]/g, ' ').trim().split(/\s+/).filter(Boolean);
    if (toks.length < 5) continue;

    // 對帳日偶爾被讀成 1157/09/30（斜線前多一個字），故允許多一位數
    const dm = line.match(/(1[01]\d)\d?\s*\/\s*(\d{{2}})\s*\/\s*(\d{{2}})/);
    const di = toks.findIndex(isRocDate);
    if (dm && di < 0) {{
      statementDate = dm[1] + dm[2] + dm[3];
      const after = line.slice(line.indexOf(dm[0]) + dm[0].length).split(/\s+/).map(num).filter(v => v != null);
      if (after.length >= 15) totals = {{ shares: after[0], interestDue: after[1], balance: after[4], repaidShares: after[5], repaidUnpaidInterest: after[7], repaidPrincipal: after[8] }};
      continue;
    }}
    if (di < 1) continue;

    let code = '';
    for (let k = di - 1; k >= 0 && !code; k--) {{ const c = normCode(toks[k]); if (/^\d{{4,6}}[A-Z]?$/.test(c)) code = c; }}
    if (!code) continue;
    // 序號以列的順序編，不採用 OCR 讀到的序號（常有誤讀、重複）
    const seq = rows.length + 1;

    const rest = toks.slice(di + 1);
    const fixed = [];
    let i = 0;
    for (; i < rest.length && fixed.length < 5; i++) {{ const v = num(rest[i]); if (v != null) fixed.push(v); }}
    // 個位數張數（如「1」）常被 OCR 漏掉，造成整列左移；以「應繳＝已繳＋未繳」判斷，左移時補空張數
    const relOk = (a, b, c) => a === b + c;
    if (!relOk(fixed[1], fixed[2], fixed[3]) && relOk(fixed[0], fixed[1], fixed[2])) {{
      fixed.unshift(0);
      fixed.pop();
      i--; // 被多讀進來的第 5 個數字其實是沖銷張數，退回給後段
      problems.push(`序號 ${{rows.length + 1}} 的餘額張數讀不到，請手動填入`);
    }}
    let repayDate = '';
    const tail = [];
    for (; i < rest.length; i++) {{
      if (!repayDate && !tail.length && isRocDate(rest[i])) {{ repayDate = rest[i]; continue; }}
      const v = num(rest[i]); if (v != null) tail.push(v);
    }}
    if (fixed.length < 5 || tail.length < 10) problems.push(`序號 ${{seq}} 欄位數不足，請檢查`);
    while (tail.length < 10) tail.push(0);
    rows.push({{
      seq, stockCode: code, stockName: '', loanDate: toks[di],
      shares: fixed[0] || 0, interestDue: fixed[1] || 0, interestPaid: fixed[2] || 0, interestUnpaid: fixed[3] || 0, balance: fixed[4] || 0,
      repayMethod: repayDate ? '還款' : '', repayDate,
      repaidShares: tail[0], repaidPaidInterest: tail[1], repaidUnpaidInterest: tail[2], repaidPrincipal: tail[3], repaidFee: tail[4]
    }});
  }}
  return {{ statementDate, rows, totals, problems }};
}}

async function parseStatement(fileArg) {{
  const files = fileArg ? [fileArg] : [...document.getElementById('imp-file').files];
  const out = document.getElementById('imp-result');
  document.getElementById('imp-raw').innerHTML = '';
  if (!files.length) {{ out.innerHTML = `<div class='alert err'>請選擇對帳單 PDF 或截圖</div>`; return; }}
  const btn = document.getElementById('imp-btn');
  btn.disabled = true;
  out.innerHTML = '';
  try {{
    impStatus('讀取檔案…');
    const pages = [];
    for (const f of files) pages.push(...await fileToCanvases(f));
    impStatus('載入辨識模型…');
    const worker = await getOcrWorker();
    let all = {{ statementDate: '', rows: [], totals: null, problems: [] }};
    const images = [];
    for (let p = 0; p < pages.length; p++) {{
      // 方向不確定（對帳單常是橫印），依序試轉，讀得到借款列的方向即採用
      let best = null;
      for (const deg of [0, 90, 270, 180]) {{
        const c = rotateCanvas(pages[p], deg);
        ocrProgress = v => impStatus(`辨識第 ${{p + 1}}/${{pages.length}} 頁（旋轉 ${{deg}}°）${{Math.round(v * 100)}}%`);
        const {{ data }} = await worker.recognize(c);
        const parsed = parseOcrText(data.text);
        if (!best || parsed.rows.length > best.parsed.rows.length) best = {{ parsed, c }};
        if (parsed.rows.length >= 3 || (parsed.rows.length && parsed.totals)) break;
      }}
      images.push(best.c.toDataURL('image/jpeg', 0.7));
      // 多張截圖重疊的部分只保留一次
      const key = r => [r.stockCode, r.loanDate, r.balance, r.repaidPrincipal].join('|');
      const seen = new Set(all.rows.map(key));
      all.rows.push(...best.parsed.rows.filter(r => !seen.has(key(r))));
      all.problems.push(...best.parsed.problems);
      all.statementDate = all.statementDate || best.parsed.statementDate;
      all.totals = all.totals || best.parsed.totals;
    }}
    impStatus('');
    all.rows.forEach((r, i) => r.seq = i + 1); // 多頁時接續編號
    if (!all.rows.length) {{ out.innerHTML = `<div class='alert err'>讀不到任何借款列，請改用較清晰的截圖或原始 PDF</div>`; return; }}
    if (!all.totals) all.problems.push('讀不到「合計」列，無法自動核對加總，請逐筆核對');
    importStatement = {{
      statementDate: all.statementDate, rows: all.rows,
      totals: all.totals || {{ shares: 0, interestDue: 0, balance: 0, repaidShares: 0, repaidUnpaidInterest: 0, repaidPrincipal: 0 }}
    }};
    importProblems = all.problems;
    importImages = images;
    renderRawRows();
    await refreshImportPreview();
  }} catch(e) {{
    impStatus('');
    out.innerHTML = `<div class='alert err'>辨識失敗：${{esc(e.message)}}</div>`;
  }} finally {{
    btn.disabled = false;
  }}
}}

// ── 辨識結果（可直接修正）────────────────────────────────────────────────
let importProblems = [], importImages = [], previewTimer = null;
const RAW_COLS = [
  ['stockCode', '股號', 70], ['loanDate', '借款日', 80], ['shares', '餘額張數', 70], ['interestDue', '應繳利息', 90],
  ['balance', '融資餘額', 110], ['repayDate', '償還日', 80], ['repaidShares', '沖銷張數', 70],
  ['repaidUnpaidInterest', '沖銷未繳利息(A)', 100], ['repaidPrincipal', '沖銷融資金(B)', 110]
];
const TOTAL_COLS = ['shares', 'interestDue', 'balance', 'repaidShares', 'repaidUnpaidInterest', 'repaidPrincipal'];

function renderRawRows() {{
  const s = importStatement;
  const cell = (k, v, w, attrs) => `<td><input ${{attrs}} data-k='${{k}}' value='${{esc(v)}}' style='width:${{w}}px;padding:2px 4px;font-size:.8rem;text-align:right' oninput='onRawEdit(this)'></td>`;
  let html = `<details id='imp-raw-box' style='margin:12px 0' ${{importProblems.length ? 'open' : ''}}>
    <summary style='cursor:pointer;font-size:.88rem'>🔍 辨識結果（可直接修正數字，預覽會自動更新）</summary>
    <div style='display:flex;gap:16px;flex-wrap:wrap;align-items:flex-start;margin-top:8px'>
    <div class='table-wrap' style='flex:1;min-width:320px'>
    <div style='font-size:.85rem;margin-bottom:6px'>對帳日（民國）<input data-k='statementDate' value='${{esc(s.statementDate)}}' style='width:90px;padding:2px 4px' oninput='onRawEdit(this)'> 例：1150930</div>
    <table class='hist-table'><thead><tr><th>序號</th>${{RAW_COLS.map(c => `<th>${{c[1]}}</th>`).join('')}}</tr></thead><tbody>`;
  s.rows.forEach((r, idx) => {{
    html += `<tr><td>${{r.seq}}</td>${{RAW_COLS.map(([k, , w]) => cell(k, r[k], w, `data-row='${{idx}}'`)).join('')}}</tr>`;
  }});
  html += `<tr style='background:#f6f6f6'><td>合計</td>${{RAW_COLS.map(([k, , w]) => TOTAL_COLS.includes(k) ? cell(k, s.totals[k], w, `data-total='1'`) : '<td></td>').join('')}}</tr>`;
  html += `</tbody></table>
    <button class='btn btn-sm btn-outline' style='margin-top:6px' onclick='addRawRow()'>＋ 加一列（補辨識漏掉的借款）</button></div>`;
  if (importImages.length)
    html += `<div style='flex:1;min-width:320px;max-height:520px;overflow:auto;border:1px solid #ddd'>${{importImages.map(u => `<img src='${{u}}' style='width:100%;display:block'>`).join('')}}</div>`;
  html += `</div></details>`;
  document.getElementById('imp-raw').innerHTML = html;
}}

function addRawRow() {{
  const rows = importStatement.rows;
  rows.push({{ seq: rows.length + 1, stockCode: '', stockName: '', loanDate: '', shares: 0, interestDue: 0, interestPaid: 0, interestUnpaid: 0, balance: 0,
    repayMethod: '', repayDate: '', repaidShares: 0, repaidPaidInterest: 0, repaidUnpaidInterest: 0, repaidPrincipal: 0, repaidFee: 0 }});
  renderRawRows();
  document.getElementById('imp-raw-box').open = true;
}}

function onRawEdit(el) {{
  const k = el.dataset.k, v = el.value.trim();
  const isText = ['stockCode', 'loanDate', 'repayDate', 'statementDate'].includes(k);
  const val = isText ? v : (parseInt(v.replace(/,/g, '')) || 0);
  if (k === 'statementDate') importStatement.statementDate = val;
  else if (el.dataset.total) importStatement.totals[k] = val;
  else {{
    const r = importStatement.rows[+el.dataset.row];
    r[k] = val;
    importProblems = importProblems.filter(p => !p.startsWith(`序號 ${{r.seq}} `)); // 該列已手動修正
    if (k === 'interestDue') r.interestUnpaid = val - r.interestPaid;
    if (k === 'repayDate') r.repayMethod = val ? '還款' : '';
  }}
  clearTimeout(previewTimer);
  previewTimer = setTimeout(refreshImportPreview, 400);
}}

// 預覽推測股號辨識錯誤時，一鍵改正辨識結果並重新比對
function fixStockCode(seq, code) {{
  const r = importStatement.rows.find(x => x.seq === seq);
  if (!r) return;
  r.stockCode = code;
  importProblems = importProblems.filter(p => !p.startsWith(`序號 ${{seq}} `));
  renderRawRows();
  refreshImportPreview();
}}

async function refreshImportPreview() {{
  const r = await fetch('/api/stocks/pledge/statement/preview', {{
    method: 'POST', headers: {{'Content-Type': 'application/json'}}, body: JSON.stringify(importStatement)
  }});
  if (!r.ok) {{ document.getElementById('imp-result').innerHTML = `<div class='alert err'>產生預覽失敗</div>`; return; }}
  const pv = await r.json();
  pv.warnings = [...importProblems, ...pv.warnings];
  renderImportPreview(pv);
}}

// 匯入面板開著時可直接 Ctrl+V 貼上截圖
document.addEventListener('paste', e => {{
  if (document.getElementById('importBox').style.display === 'none') return;
  const item = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
  if (!item) return;
  e.preventDefault();
  parseStatement(item.getAsFile());
}});

function renderImportPreview(pv) {{
  const out = document.getElementById('imp-result');
  const changed = pv.items.filter(i => i.action !== 'unchanged');
  const hasNew = pv.items.some(i => i.action === 'create' || i.action === 'create-closed');
  const diffCell = (before, after, isInt) => {{
    const f = v => v == null ? '—' : fmt(v);
    if (before == null) return `<td class='num'>${{f(after)}}</td>`;
    if (Math.round(before) === Math.round(after)) return `<td class='num' style='color:#999'>${{f(after)}}</td>`;
    return `<td class='num'><span style='color:#999'>${{f(before)}}</span> → <strong>${{f(after)}}</strong></td>`;
  }};

  let html = `<div style='margin:12px 0 6px'>對帳日 <strong>${{esc(pv.statementDate || '?')}}</strong>，共 ${{pv.items.length}} 筆借款，其中 <strong>${{changed.length}}</strong> 筆有變動。</div>`;
  if (pv.warnings.length)
    html += `<div class='alert err' style='font-size:.85rem'>⚠️ 辨識結果可能有誤，請先核對對帳單：<br>${{pv.warnings.map(esc).join('<br>')}}</div>`;
  else
    html += `<div class='alert ok' style='font-size:.85rem'>✓ 各欄逐列加總與對帳單合計列一致</div>`;

  html += `<div class='table-wrap'><table class='hist-table' style='width:100%'><thead><tr>
    <th><input type='checkbox' id='imp-all' checked onchange='document.querySelectorAll("".imp-chk"").forEach(c => c.checked = this.checked)'></th>
    <th>序號</th><th>動作</th><th>股票</th><th>借款日</th>
    <th style='text-align:right'>張數</th><th style='text-align:right'>借款金額</th><th style='text-align:right'>累計利息</th><th>備註</th>
  </tr></thead><tbody>`;
  for (const i of pv.items) {{
    const [label, color] = IMPORT_ACTIONS[i.action] || [i.action, '#333'];
    const isChange = i.action !== 'unchanged';
    const note = [i.closedDate ? i.closedDate + ' 結清' : '', i.note].filter(Boolean).join(' ');
    html += `<tr style='${{isChange ? '' : 'color:#aaa'}}'>
      <td>${{isChange ? `<input type='checkbox' class='imp-chk' value='${{i.seq}}' ${{i.suspect ? '' : 'checked'}}>` : ''}}</td>
      <td>${{i.seq}}</td>
      <td><span style='color:${{color}};font-weight:600'>${{label}}</span></td>
      <td style='white-space:nowrap'>${{i.suspect ? `<span style='color:#c0392b;font-weight:600' title='股號可能辨識錯誤，預設不勾選'>⚠️ ${{esc(i.stockCode)}}</span>` : esc(i.stockCode)}} ${{esc(i.stockName)}}
        ${{i.suggestCode ? `<button class='btn btn-sm btn-outline' style='width:auto;margin:0 0 0 6px;padding:1px 8px' onclick='fixStockCode(${{i.seq}},""${{esc(i.suggestCode)}}"")'>改成 ${{esc(i.suggestCode)}}</button>` : ''}}</td>
      <td>${{esc(i.loanDate)}}</td>
      ${{diffCell(i.sharesBefore, i.sharesAfter)}}
      ${{diffCell(i.principalBefore, i.principalAfter)}}
      ${{diffCell(i.interestBefore, i.interestAfter)}}
      <td style='font-size:.8rem'>${{esc(note)}}</td>
    </tr>`;
  }}
  html += `</tbody></table></div>`;

  if (pv.orphans.length) {{
    html += `<p style='font-size:.85rem;color:#888;margin:10px 0 4px'>以下未結清質借在對帳單上找不到（可能屬於其他機構），不會變動：</p><ul style='font-size:.85rem;color:#888;margin:0'>`;
    for (const o of pv.orphans)
      html += `<li>${{esc(o.stockCode)}} ${{esc(o.stockName)}}　${{o.shares}} 張　${{fmt(o.principal)}} 元　借款日 ${{esc(o.loanDate)}}　${{esc(o.custodyAccount)}}</li>`;
    html += `</ul>`;
  }}

  html += `<div class='mini-row' style='margin-top:12px'>`;
  if (hasNew) html += `<div class='field'><label>新借款利率（%）</label><input type='number' step='0.001' id='imp-rate' value='${{pv.defaultRate}}'></div>`;
  html += changed.length
    ? `<button class='btn btn-sm' onclick='applyStatement()'>套用勾選項目</button>`
    : `<span style='color:#888'>資料已與對帳單一致，不需更新。</span>`;
  html += `</div>`;
  out.innerHTML = html;
}}

async function applyStatement() {{
  const seqs = [...document.querySelectorAll('.imp-chk:checked')].map(c => parseInt(c.value));
  if (!seqs.length) {{ alert('請至少勾選一筆'); return; }}
  const rateEl = document.getElementById('imp-rate');
  const rate = rateEl ? parseFloat(rateEl.value) || 0 : 0;
  if (!confirm('確定依對帳單更新 ' + seqs.length + ' 筆質借？')) return;
  const r = await fetch('/api/stocks/pledge/statement/apply', {{
    method: 'POST', headers: {{'Content-Type': 'application/json'}},
    body: JSON.stringify({{ statement: importStatement, seqs, rate }})
  }});
  if (r.ok) location.reload();
  else showMsg(esc(await r.text()) || '套用失敗', 'err');
}}

function showMsg(m, t) {{ document.getElementById('msg').innerHTML = `<div class='alert ${{t}}'>${{m}}</div>`; }}

pledgeTable = initTable('pledge-table', {{
  cols: 13,
  noFilter: [2, 3, 6, 7, 8, 9, 12],
  onFilter: updatePledgeTotals
}});
refreshTable();
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("股票質借", "stocks", "pledge", body));
    }

    // ── API ──────────────────────────────────────────────────────────────

    static async Task<IResult> AddPledge(PledgeAddRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.StockCode) || req.Shares <= 0 || req.Principal <= 0 ||
            req.Rate <= 0 || string.IsNullOrWhiteSpace(req.InterestSince) || !DateTime.TryParse(req.InterestSince, out _))
            return Results.BadRequest("請完整填寫股號、張數、借款金額、利率與起息日");

        var pledge = new PledgeLoan(
            Guid.NewGuid().ToString("N")[..8],
            req.StockCode.Trim(), (req.StockName ?? "").Trim(),
            req.Shares, req.Principal, req.Rate, req.InterestSince, 0,
            (req.CustodyAccount ?? "").Trim(),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            [new PledgeEvent(req.InterestSince, "新增", req.Rate, null, null, 0, req.Principal)],
            null, (req.Notes ?? "").Trim(), req.InterestSince);

        var pledges = LoadPledges();
        pledges.Add(pledge);
        SavePledges(pledges);
        return Results.Ok();
    }

    static async Task<IResult> UpdatePledge(string id, PledgeEditRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.StockCode) || req.Shares <= 0 || req.Principal < 0 ||
            req.Rate <= 0 || string.IsNullOrWhiteSpace(req.InterestSince) || !DateTime.TryParse(req.InterestSince, out _))
            return Results.BadRequest("請完整填寫股號、張數、借款金額、利率與起息(結算)日");

        var pledges = LoadPledges();
        var idx = pledges.FindIndex(p => p.Id == id);
        if (idx < 0) return Results.NotFound("質借紀錄不存在");

        pledges[idx] = pledges[idx] with
        {
            StockCode = req.StockCode.Trim(),
            StockName = (req.StockName ?? "").Trim(),
            Shares = req.Shares,
            Principal = req.Principal,
            Rate = req.Rate,
            InterestSince = req.InterestSince,
            AccumulatedInterest = req.AccumulatedInterest,
            CustodyAccount = (req.CustodyAccount ?? "").Trim(),
            Notes = (req.Notes ?? "").Trim()
        };
        SavePledges(pledges);
        return Results.Ok();
    }

    static IResult ChangeRate(string id, PledgeRateRequest req)
    {
        if (req.Rate <= 0) return Results.BadRequest("利率必須大於 0");
        if (string.IsNullOrWhiteSpace(req.EffectiveDate) || !DateTime.TryParse(req.EffectiveDate, out var effDate))
            return Results.BadRequest("請輸入有效的生效日期");

        var pledges = LoadPledges();
        var idx = pledges.FindIndex(p => p.Id == id);
        if (idx < 0) return Results.NotFound();
        var p = pledges[idx];
        if (p.ClosedDate != null) return Results.BadRequest("此質借已結清");
        if (effDate.Date < DateTime.Parse(p.InterestSince).Date) return Results.BadRequest("生效日期不可早於目前起息(結算)日");

        var settled = SettleInterest(p, effDate);
        var events = (p.Events ?? []).ToList();
        events.Add(new PledgeEvent(req.EffectiveDate, "利率變更", req.Rate, null, null, settled, p.Principal));

        pledges[idx] = p with
        {
            Rate = req.Rate,
            InterestSince = req.EffectiveDate,
            AccumulatedInterest = p.AccumulatedInterest + settled,
            Events = events
        };
        SavePledges(pledges);
        return Results.Ok();
    }

    static IResult Repay(string id, PledgeRepayRequest req)
    {
        if (req.Amount <= 0) return Results.BadRequest("還款金額必須大於 0");
        if (req.Type != "cash" && req.Type != "sell") return Results.BadRequest("還款類型錯誤");
        if (req.Type == "sell" && req.SharesSold <= 0) return Results.BadRequest("請輸入賣出張數");
        if (string.IsNullOrWhiteSpace(req.Date) || !DateTime.TryParse(req.Date, out var repayDate))
            return Results.BadRequest("請輸入有效的還款日期");

        var pledges = LoadPledges();
        var idx = pledges.FindIndex(p => p.Id == id);
        if (idx < 0) return Results.NotFound();
        var p = pledges[idx];
        if (p.ClosedDate != null) return Results.BadRequest("此質借已結清");
        if (repayDate.Date < DateTime.Parse(p.InterestSince).Date) return Results.BadRequest("還款日期不可早於目前起息(結算)日");
        if (req.Type == "sell" && req.SharesSold > p.Shares) return Results.BadRequest("賣出張數不可超過目前質押張數");

        var settled = SettleInterest(p, repayDate);
        var newPrincipal = Math.Max(0, p.Principal - req.Amount);
        var newShares = req.Type == "sell" ? p.Shares - req.SharesSold : p.Shares;

        var events = (p.Events ?? []).ToList();
        events.Add(new PledgeEvent(req.Date, req.Type == "sell" ? "賣出還款" : "現金還款", null, req.Amount,
            req.Type == "sell" ? req.SharesSold : null, settled, newPrincipal));

        pledges[idx] = p with
        {
            Principal = newPrincipal,
            Shares = newShares,
            InterestSince = req.Date,
            AccumulatedInterest = p.AccumulatedInterest + settled,
            Events = events,
            ClosedDate = newPrincipal <= 0 ? req.Date : null
        };
        SavePledges(pledges);
        return Results.Ok();
    }

    static IResult DeletePledge(string id)
    {
        var pledges = LoadPledges();
        var target = pledges.FirstOrDefault(p => p.Id == id);
        if (target == null) return Results.NotFound();
        pledges.Remove(target);
        SavePledges(pledges);
        return Results.Ok();
    }

    // ── Data helpers ─────────────────────────────────────────────────────

    static decimal SettleInterest(PledgeLoan p, DateTime asOf)
    {
        var since = DateTime.Parse(p.InterestSince);
        var days = Math.Max(0, (asOf.Date - since.Date).Days);
        return p.Principal * p.Rate / 100m / 365m * days;
    }

    // 已結算累計利息 + 尚未結算的目前區段利息（已結清者本金為 0，不再生息）
    public static decimal TotalInterest(PledgeLoan p, DateTime asOf) =>
        p.AccumulatedInterest + (p.ClosedDate != null ? 0 : SettleInterest(p, asOf));

    public static List<PledgeLoan> LoadPledges()
    {
        if (!File.Exists(PledgesFile)) return [];
        return JsonSerializer.Deserialize<List<PledgeLoan>>(File.ReadAllText(PledgesFile), JsonOpts) ?? [];
    }

    public static void SavePledges(List<PledgeLoan> pledges) =>
        File.WriteAllText(PledgesFile, JsonSerializer.Serialize(pledges, JsonOpts));
}

// ── Models ───────────────────────────────────────────────────────────────

public record PledgeLoan(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("stockCode")] string StockCode,
    [property: JsonPropertyName("stockName")] string StockName,
    [property: JsonPropertyName("shares")] int Shares,
    [property: JsonPropertyName("principal")] decimal Principal,
    [property: JsonPropertyName("rate")] decimal Rate,
    [property: JsonPropertyName("interestSince")] string InterestSince,
    [property: JsonPropertyName("accumulatedInterest")] decimal AccumulatedInterest,
    [property: JsonPropertyName("custodyAccount")] string CustodyAccount,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("events")] List<PledgeEvent> Events,
    [property: JsonPropertyName("closedDate")] string? ClosedDate,
    [property: JsonPropertyName("notes")] string Notes,
    // 原始借款日，用來對應元大對帳單的「借款日」；舊資料為 null 時以第一筆事件日期代替
    [property: JsonPropertyName("loanDate")] string? LoanDate = null);

public record PledgeEvent(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("rate")] decimal? Rate,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("sharesSold")] int? SharesSold,
    [property: JsonPropertyName("settledInterest")] decimal SettledInterest,
    [property: JsonPropertyName("principalAfter")] decimal PrincipalAfter);

public record PledgeAddRequest(
    string StockCode, string? StockName, int Shares, decimal Principal, decimal Rate,
    string InterestSince, string? CustodyAccount, string? Notes);

public record PledgeEditRequest(
    string StockCode, string? StockName, int Shares, decimal Principal, decimal Rate,
    string InterestSince, decimal AccumulatedInterest, string? CustodyAccount, string? Notes);

public record PledgeRateRequest(decimal Rate, string EffectiveDate);
public record PledgeRepayRequest(string Type, decimal Amount, int SharesSold, string Date);
