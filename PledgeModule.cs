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
            var closedTag = p.ClosedDate != null ? $"<span class='tag' style='background:#eee;color:#888;margin-left:4px'>已結清 {p.ClosedDate}</span>" : "";
            return $@"
<tr id='row-{p.Id}' class='{(p.ClosedDate != null ? "closed-row" : "")}'>
  <td>{System.Net.WebUtility.HtmlEncode(p.StockCode)} <span style='color:#666'>{System.Net.WebUtility.HtmlEncode(p.StockName)}</span>{closedTag}</td>
  <td class='num'>{p.Shares:N0}</td>
  <td class='num' id='price-{p.Id}'><span class='loading'>—</span></td>
  <td class='num' id='mv-{p.Id}'>—</td>
  <td class='num'>{p.Principal:N0}</td>
  <td class='num'>{p.Rate:F3}%</td>
  <td class='num' id='daily-{p.Id}'>—</td>
  <td class='num' id='interest-{p.Id}'>—</td>
  <td class='num' id='owed-{p.Id}'>—</td>
  <td class='num' id='mr-{p.Id}'>—</td>
  <td>{p.InterestSince}</td>
  <td>{System.Net.WebUtility.HtmlEncode(p.CustodyAccount)}</td>
  <td>
    <div class='actions'>
      <button class='btn btn-sm btn-outline' onclick=""togglePanel('{p.Id}','edit')"">編輯</button>
      <button class='btn btn-sm btn-outline' onclick=""togglePanel('{p.Id}','rate')"" {(p.ClosedDate != null ? "disabled" : "")}>改利率</button>
      <button class='btn btn-sm btn-outline' onclick=""togglePanel('{p.Id}','sell')"" {(p.ClosedDate != null ? "disabled" : "")}>賣出還款</button>
      <button class='btn btn-sm btn-outline' onclick=""togglePanel('{p.Id}','cash')"" {(p.ClosedDate != null ? "disabled" : "")}>現金還款</button>
      <button class='btn btn-sm btn-outline' onclick=""togglePanel('{p.Id}','hist')"">歷史</button>
      <button class='btn btn-sm btn-danger' onclick='delPledge(""{p.Id}"")'>刪除</button>
    </div>
  </td>
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
  <button class='btn' onclick=""togglePanel('new','new')"">＋ 新增質借</button>
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

async function delPledge(id) {{
  if (!confirm('確定刪除此質借紀錄？此動作無法復原。')) return;
  const r = await fetch('/api/stocks/pledge/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload(); else showMsg('刪除失敗', 'err');
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
            null, (req.Notes ?? "").Trim());

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

    static List<PledgeLoan> LoadPledges()
    {
        if (!File.Exists(PledgesFile)) return [];
        return JsonSerializer.Deserialize<List<PledgeLoan>>(File.ReadAllText(PledgesFile), JsonOpts) ?? [];
    }

    static void SavePledges(List<PledgeLoan> pledges) =>
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
    [property: JsonPropertyName("notes")] string Notes);

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
