using System.Text.Json;
using System.Text.Json.Serialization;

public static class DebtModule
{
    static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string DebtsFile = Path.Combine(DataDir, "debts.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/debt", ListPage);
        app.MapGet("/debt/add", AddPage);
        app.MapPost("/api/debt", AddLoan);
        app.MapDelete("/api/debt/{id}", DeleteLoan);
        app.MapPatch("/api/debt/{id}/balance", UpdateBalance);
        app.MapPost("/api/debt/{id}/rate", UpdateRate);
        app.MapGet("/api/debt/loans", GetLoans);
    }

    // ── List page ──────────────────────────────────────────────────────────

    static async Task ListPage(HttpContext ctx)
    {
        var loans = LoadLoans();
        var regularLoans = loans.Where(l => l.LoanType != "股票質押").ToList();
        var stockLoans = loans.Where(l => l.LoanType == "股票質押").ToList();
        var stockCodes = stockLoans.Select(l => l.StockCode).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();

        // ── Regular loans table ────────────────────────────────────────────
        var regRows = regularLoans.Count == 0
            ? "<tr><td colspan='10' style='text-align:center;padding:20px;color:#999'>尚無一般貸款資料</td></tr>"
            : string.Join("", regularLoans.Select(l =>
            {
                var repayTag = string.IsNullOrEmpty(l.RepaymentMethod) ? "" : $"<span class='tag' style='margin-left:4px'>{l.RepaymentMethod}</span>";
                var graceTag = l.LoanType == "房貸" && l.GracePeriodMonths > 0
                    ? $"<br><small style='color:#888'>寬限期 {l.GracePeriodMonths} 個月</small>" : "";
                var computedId = $"bal-{l.Id}";
                return $@"<tr>
  <td>{System.Net.WebUtility.HtmlEncode(l.Name)}{repayTag}{graceTag}</td>
  <td><span class='tag'>{l.LoanType}</span></td>
  <td>{System.Net.WebUtility.HtmlEncode(l.Bank)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(l.Borrower)}</td>
  <td class='num'>{l.Principal:N0}</td>
  <td class='num' id='{computedId}'><span class='loading'>計算中…</span></td>
  <td class='num'>{l.Rate:F2}%</td>
  <td class='num' id='mp-{l.Id}'>{(l.MonthlyPayment > 0 ? l.MonthlyPayment.ToString("N0") : "—")}</td>
  <td class='num' id='int-{l.Id}'>—</td>
  <td>{l.StartDate}{(l.LoanType == "房貸" && !string.IsNullOrEmpty(l.GracePeriodEndDate) ? $"<br><small style='color:#888'>寬限至 {l.GracePeriodEndDate}</small>" : "")}</td>
  <td>{l.EndDate}</td>
  <td>
    <div class='actions'>
      <button class='btn btn-sm btn-outline' onclick='saveBalance(""{l.Id}"")'>更新餘額</button>
      <button class='btn btn-sm btn-danger' onclick='del(""{l.Id}"")'>刪除</button>
    </div>
  </td>
</tr>";
            }));

        // ── Stock pledge section ───────────────────────────────────────────
        var stockRows = stockLoans.Count == 0 ? "" : string.Join("", stockLoans.Select(l => $@"
<tr id='srow-{l.Id}'>
  <td>{System.Net.WebUtility.HtmlEncode(l.Name)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(l.Bank)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(l.Borrower)}</td>
  <td>{l.StockCode} <span style='color:#666'>{System.Net.WebUtility.HtmlEncode(l.StockName ?? "")}</span></td>
  <td class='num'>{l.StockShares:N0}</td>
  <td class='num' id='sprice-{l.Id}'><span class='loading'>—</span></td>
  <td class='num' id='smv-{l.Id}'>—</td>
  <td class='num'>{l.Principal:N0}</td>
  <td class='num' id='scurrate-{l.Id}'>{l.Rate:F2}%</td>
  <td class='num' id='sdaily-{l.Id}'>—</td>
  <td class='num' id='sinterest-{l.Id}'>—</td>
  <td class='num' id='sowed-{l.Id}'>—</td>
  <td class='num' id='smr-{l.Id}'>—</td>
  <td>{l.InterestStartDate}</td>
  <td>
    <div class='actions'>
      <button class='btn btn-sm btn-outline' onclick='toggleRateForm(""{l.Id}"")'>更改利率</button>
      <button class='btn btn-sm btn-danger' onclick='del(""{l.Id}"")'>刪除</button>
    </div>
  </td>
</tr>
<tr id='rateDetail-{l.Id}' style='display:none'>
  <td colspan='15' style='background:#fffbe6;border-bottom:2px solid #e6d87a;padding:0'>
    <div style='padding:14px 18px'>
      <div id='rateHist-{l.Id}'></div>
      <div style='display:flex;gap:10px;align-items:flex-end;flex-wrap:wrap;margin-top:12px'>
        <div class='field' style='margin:0'>
          <label style='font-size:.82rem'>新利率（%）</label>
          <input type='number' id='nr-{l.Id}' step='0.001' placeholder='如：2.500' style='width:130px'>
        </div>
        <div class='field' style='margin:0'>
          <label style='font-size:.82rem'>生效日期</label>
          <input type='date' id='nrd-{l.Id}' style='width:150px'>
        </div>
        <button class='btn btn-sm' onclick='submitRate(""{l.Id}"")'>確認更改</button>
        <button class='btn btn-sm btn-outline' onclick='toggleRateForm(""{l.Id}"")'>取消</button>
      </div>
    </div>
  </td>
</tr>"));

        var loansJson = JsonSerializer.Serialize(loans, JsonOpts);

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>💳 貸款管理</h1>
  <a href='/debt/add' class='btn'>＋ 新增貸款</a>
</div>
<div class='cards' id='summaryCards'>
  <div class='card'><div class='lbl'>一般貸款餘額合計</div><div class='val pos' id='s-balance'>計算中…</div></div>
  <div class='card'><div class='lbl'>每月還款合計</div><div class='val' id='s-monthly'>—</div></div>
  <div class='card'><div class='lbl'>年利息合計</div><div class='val pos' id='s-interest'>—</div></div>
  {(stockLoans.Count > 0 ? @"<div class='card'><div class='lbl'>股票質押借款</div><div class='val pos' id='s-pledge'>—</div></div>
  <div class='card'><div class='lbl'>平均維持率</div><div class='val' id='s-mr'>—</div></div>" : "")}
</div>

{(regularLoans.Count > 0 ? $@"
<h2>💳 一般貸款</h2>
<div class='table-wrap'>
<table>
<thead><tr>
  <th>貸款名稱</th><th>類型</th><th>銀行</th><th>借款人</th>
  <th class='num'>原始金額</th><th class='num'>目前餘額</th>
  <th class='num'>年利率</th><th class='num'>月付金</th><th class='num'>年利息</th>
  <th>起始日</th><th>預計結清</th><th></th>
</tr></thead>
<tbody>{regRows}</tbody>
</table>
</div>" : "")}

{(stockLoans.Count > 0 ? $@"
<h2>📊 股票質押</h2>
<div class='table-wrap'>
<table>
<thead><tr>
  <th>名稱</th><th>機構</th><th>借款人</th><th>股票</th>
  <th class='num'>張數</th><th class='num'>即時股價</th><th class='num'>市值</th>
  <th class='num'>借款金額</th><th class='num'>目前利率</th>
  <th class='num'>日利息</th><th class='num'>累計利息</th><th class='num'>應償還金額</th>
  <th class='num'>維持率</th><th>起息日</th><th></th>
</tr></thead>
<tbody>{stockRows}</tbody>
</table>
</div>
<p style='font-size:.8rem;color:#999;margin-top:8px'>維持率 = 市值 ÷ 應償還金額 × 100%。一般追繳線：130%，警戒線：140%，請依各券商規定為準。</p>" : "")}

{(loans.Count == 0 ? "<div class='empty-state'><div class='icon'>💳</div><p>尚無貸款資料</p><a href='/debt/add' class='btn'>新增貸款</a></div>" : "")}

<div id='msg' style='margin-top:16px'></div>
<style>
th.num,td.num{{text-align:right}}
.mr-ok{{color:#080;font-weight:700}}
.mr-warn{{color:#d80;font-weight:700}}
.mr-danger{{color:#c00;font-weight:700}}
</style>
<script>
const loans = {loansJson};
const today = new Date();

function monthsDiff(from, to) {{
  const f = new Date(from);
  return (to.getFullYear() - f.getFullYear()) * 12 + (to.getMonth() - f.getMonth());
}}

function daysDiff(from, to) {{
  const f = new Date(from);
  return Math.max(0, Math.floor((to - f) / 86400000));
}}

function fmt(n) {{ return Math.round(n).toLocaleString('zh-TW'); }}
function fmtD(n) {{ return n.toFixed(1) + '%'; }}

// ── 計算一般貸款餘額 ─────────────────────────────────────────────────────
function calcBalance(loan) {{
  const r = loan.rate / 100 / 12;
  const elapsed = monthsDiff(loan.startDate, today);
  if (elapsed <= 0) return loan.principal;

  let P = loan.principal;
  let N = loan.totalMonths || 0;
  let n = elapsed;

  // 房貸寬限期處理
  if (loan.loanType === '房貸' && loan.gracePeriodMonths > 0) {{
    if (elapsed <= loan.gracePeriodMonths) return P; // 仍在寬限期，餘額不變
    n = elapsed - loan.gracePeriodMonths;
    N = N - loan.gracePeriodMonths;
  }}

  if (N <= 0) return Math.max(0, loan.balance || loan.principal);
  n = Math.min(n, N);

  const method = loan.repaymentMethod || '';
  if (method === '本息均攤') {{
    if (r < 0.000001) return Math.max(0, P * (1 - n / N));
    const pN = Math.pow(1 + r, N);
    const pn = Math.pow(1 + r, n);
    return Math.max(0, P * (pN - pn) / (pN - 1));
  }} else if (method === '本金均攤') {{
    return Math.max(0, P * (N - n) / N);
  }} else {{
    return loan.balance || loan.principal;
  }}
}}

// ── 計算月付金 ───────────────────────────────────────────────────────────
function calcMonthlyPayment(P, r_annual, N) {{
  const r = r_annual / 100 / 12;
  if (r < 0.000001) return P / N;
  return P * r * Math.pow(1 + r, N) / (Math.pow(1 + r, N) - 1);
}}

// ── 更新一般貸款顯示 ─────────────────────────────────────────────────────
const computedBalances = {{}};

function updateRegularLoans() {{
  let totalBal = 0, totalMonthly = 0, totalInt = 0;
  for (const l of loans.filter(x => x.loanType !== '股票質押')) {{
    const bal = calcBalance(l);
    computedBalances[l.id] = bal;
    const balEl = document.getElementById('bal-' + l.id);
    if (balEl) balEl.innerHTML = '<strong>' + fmt(bal) + '</strong>';

    const annualInt = bal * l.rate / 100;
    const intEl = document.getElementById('int-' + l.id);
    if (intEl) intEl.textContent = fmt(annualInt);

    let mp = l.monthlyPayment || 0;
    if (!mp && l.repaymentMethod === '本息均攤' && l.totalMonths > 0) {{
      const N = l.loanType === '房貸' && l.gracePeriodMonths > 0
        ? l.totalMonths - l.gracePeriodMonths : l.totalMonths;
      mp = calcMonthlyPayment(l.principal, l.rate, N);
      const mpEl = document.getElementById('mp-' + l.id);
      if (mpEl) mpEl.textContent = fmt(mp);
    }} else if (!mp && l.repaymentMethod === '本金均攤' && l.totalMonths > 0) {{
      const N = l.loanType === '房貸' && l.gracePeriodMonths > 0
        ? l.totalMonths - l.gracePeriodMonths : l.totalMonths;
      mp = l.principal / N + l.principal * l.rate / 100 / 12;
    }} else if (!mp && l.repaymentMethod === '只還利息') {{
      mp = l.principal * l.rate / 100 / 12;
      const mpEl = document.getElementById('mp-' + l.id);
      if (mpEl) mpEl.textContent = fmt(mp);
    }}

    totalBal += bal;
    totalMonthly += mp;
    totalInt += annualInt;
  }}
  document.getElementById('s-balance').textContent = fmt(totalBal) + ' 元';
  document.getElementById('s-monthly').textContent = fmt(totalMonthly) + ' 元';
  document.getElementById('s-interest').textContent = fmt(totalInt) + ' 元';
  return totalBal;
}}

// ── 分段利率累計利息計算 ─────────────────────────────────────────────────
function calcAccumulatedInterest(loan) {{
  const P = loan.principal;
  const hist = loan.rateHistory;
  if (!hist || hist.length === 0) {{
    const startDate = loan.interestStartDate || loan.startDate;
    if (!startDate) return {{ interest: 0, dailyRate: loan.rate }};
    const days = daysDiff(startDate, today);
    return {{ interest: P * loan.rate / 100 / 365 * days, dailyRate: loan.rate }};
  }}
  let total = 0;
  for (const h of hist) {{
    const start = new Date(h.startDate);
    const end = h.endDate ? new Date(h.endDate) : today;
    const days = Math.max(0, Math.floor((end - start) / 86400000));
    total += P * h.rate / 100 / 365 * days;
  }}
  const currentRate = hist[hist.length - 1].rate;
  return {{ interest: total, dailyRate: currentRate }};
}}

// ── 更新股票質押顯示 ─────────────────────────────────────────────────────
async function updateStockLoans() {{
  const sl = loans.filter(x => x.loanType === '股票質押');
  if (!sl.length) return;

  const codes = [...new Set(sl.map(l => l.stockCode).filter(Boolean))].join(',');
  let prices = {{}};
  try {{
    const r = await fetch('/api/stocks/prices?codes=' + codes);
    prices = await r.json();
  }} catch(e) {{}}

  let totalPledge = 0, totalMRNum = 0, mrCount = 0;

  for (const l of sl) {{
    const {{ interest, dailyRate }} = calcAccumulatedInterest(l);
    const dailyInt = l.principal * dailyRate / 100 / 365;
    const owed = l.principal + interest;
    const price = prices[l.stockCode] || l.stockPrice || 0;
    const mv = price * (l.stockShares || 0) * 1000;
    const mr = owed > 0 ? mv / owed * 100 : 0;

    const set = (id, val) => {{ const el = document.getElementById(id); if (el) el.innerHTML = val; }};
    set('sprice-' + l.id, price ? price.toFixed(2) : '<span class=loading>—</span>');
    set('smv-' + l.id, mv > 0 ? fmt(mv) : '—');
    set('scurrate-' + l.id, dailyRate.toFixed(3) + '%');
    set('sdaily-' + l.id, dailyInt.toFixed(2) + ' 元');
    set('sinterest-' + l.id, '<strong>' + fmt(interest) + '</strong>');
    set('sowed-' + l.id, '<strong style=\'color:#c00\'>' + fmt(owed) + '</strong>');

    if (mr > 0) {{
      const cls = mr < 130 ? 'mr-danger' : mr < 140 ? 'mr-warn' : 'mr-ok';
      const warn = mr < 130 ? ' ⚠️追繳' : mr < 140 ? ' ⚠️警戒' : '';
      set('smr-' + l.id, `<span class='${{cls}}'>${{fmtD(mr)}}${{warn}}</span>`);
      totalMRNum += mr; mrCount++;
    }}
    totalPledge += l.principal;
  }}

  const pledgeEl = document.getElementById('s-pledge');
  if (pledgeEl) pledgeEl.textContent = fmt(totalPledge) + ' 元';
  const mrEl = document.getElementById('s-mr');
  if (mrEl && mrCount > 0) {{
    const avgMR = totalMRNum / mrCount;
    mrEl.className = 'val ' + (avgMR < 130 ? 'mr-danger' : avgMR < 140 ? 'mr-warn' : 'mr-ok');
    mrEl.textContent = fmtD(avgMR);
  }}
}}

// ── 利率異動 UI ──────────────────────────────────────────────────────────
function toggleRateForm(id) {{
  const row = document.getElementById('rateDetail-' + id);
  const isHidden = row.style.display === 'none';
  if (isHidden) {{
    renderRateHistory(id);
    document.getElementById('nrd-' + id).value = today.toISOString().slice(0,10);
    row.style.display = '';
  }} else {{
    row.style.display = 'none';
  }}
}}

function renderRateHistory(id) {{
  const loan = loans.find(l => l.id === id);
  const hist = loan.rateHistory;
  const el = document.getElementById('rateHist-' + id);
  if (!hist || hist.length === 0) {{
    el.innerHTML = '<p style=\'color:#888;font-size:.85rem;margin:0 0 4px\'>' +
      '目前為初始利率 <strong>' + loan.rate + '%</strong>' +
      '（自 ' + (loan.interestStartDate || loan.startDate) + '），尚無異動記錄。</p>';
    return;
  }}
  let html = '<strong style=\'font-size:.88rem\'>利率異動紀錄</strong>' +
    '<table style=\'width:auto;font-size:.83rem;border-collapse:collapse;margin:6px 0;background:transparent;box-shadow:none\'>' +
    '<thead><tr style=\'background:#f0f4ff\'>' +
    '<th style=\'padding:4px 10px;text-align:left\'>生效日</th>' +
    '<th style=\'padding:4px 10px;text-align:left\'>結束日</th>' +
    '<th style=\'padding:4px 10px;text-align:right\'>利率</th>' +
    '<th style=\'padding:4px 10px;text-align:right\'>天數</th>' +
    '<th style=\'padding:4px 10px;text-align:right\'>期間利息</th>' +
    '</tr></thead><tbody>';
  for (const h of hist) {{
    const s = new Date(h.startDate), e = h.endDate ? new Date(h.endDate) : today;
    const days = Math.max(0, Math.floor((e - s) / 86400000));
    const pInt = loan.principal * h.rate / 100 / 365 * days;
    const endCell = h.endDate ? h.endDate : '<em style=\'color:#999\'>至今</em>';
    html += '<tr>' +
      '<td style=\'padding:4px 10px\'>' + h.startDate + '</td>' +
      '<td style=\'padding:4px 10px\'>' + endCell + '</td>' +
      '<td style=\'padding:4px 10px;text-align:right\'>' + h.rate + '%</td>' +
      '<td style=\'padding:4px 10px;text-align:right\'>' + days + '</td>' +
      '<td style=\'padding:4px 10px;text-align:right\'>' + fmt(pInt) + ' 元</td>' +
      '</tr>';
  }}
  html += '</tbody></table>';
  el.innerHTML = html;
}}

async function submitRate(id) {{
  const newRate = parseFloat(document.getElementById('nr-' + id).value);
  const effectiveDate = document.getElementById('nrd-' + id).value;
  if (!newRate || newRate <= 0) {{ alert('請輸入有效的新利率'); return; }}
  if (!effectiveDate) {{ alert('請輸入生效日期'); return; }}
  const r = await fetch('/api/debt/' + id + '/rate', {{
    method: 'POST',
    headers: {{'Content-Type': 'application/json'}},
    body: JSON.stringify({{ rate: newRate, effectiveDate }})
  }});
  if (r.ok) location.reload();
  else alert('更新失敗，請確認生效日期不早於起息日');
}}

async function saveBalance(id) {{
  const bal = computedBalances[id];
  if (bal == null) {{ alert('請等待餘額計算完成'); return; }}
  if (!confirm('將已計算的餘額 ' + fmt(bal) + ' 元儲存至資料庫？')) return;
  const r = await fetch('/api/debt/' + id + '/balance', {{
    method:'PATCH', headers:{{'Content-Type':'application/json'}},
    body: JSON.stringify({{balance: bal}})
  }});
  if (r.ok) {{
    document.getElementById('msg').innerHTML = '<div class=""alert ok"">✓ 餘額已更新</div>';
    setTimeout(() => document.getElementById('msg').innerHTML='', 3000);
  }}
}}

async function del(id) {{
  if (!confirm('確定刪除此貸款？')) return;
  const r = await fetch('/api/debt/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">刪除失敗</div>';
}}

updateRegularLoans();
updateStockLoans();
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("貸款管理", "debt", "list", body));
    }

    // ── Add page ──────────────────────────────────────────────────────────

    static async Task AddPage(HttpContext ctx)
    {
        var body = @"
<h1>💳 新增貸款</h1>
<div class='form-card' style='max-width:680px'>
<div id='msg'></div>

<div class='row2'>
  <div class='field'>
    <label>貸款類型</label>
    <select id='loanType' onchange='onTypeChange()'>
      <option value='房貸'>房貸</option>
      <option value='車貸'>車貸</option>
      <option value='信貸'>信貸</option>
      <option value='股票質押'>股票質押</option>
      <option value='其他'>其他</option>
    </select>
  </div>
  <div class='field'>
    <label>借款人</label>
    <input id='borrower' value='周俊佑' placeholder='借款人姓名'>
  </div>
</div>
<div class='row2'>
  <div class='field'>
    <label>貸款名稱</label>
    <input id='name' placeholder='如：自住房貸、元大質押'>
  </div>
  <div class='field'>
    <label>銀行／機構</label>
    <input id='bank' placeholder='如：國泰世華銀行、元大證金'>
  </div>
</div>

<!-- ═══ 一般貸款欄位 ═══ -->
<div id='regularFields'>
  <div class='row3'>
    <div class='field'>
      <label>原始貸款金額（元）</label>
      <input type='number' id='principal' placeholder='如：10000000' oninput='onRegularInput()'>
    </div>
    <div class='field'>
      <label>年利率（%）</label>
      <input type='number' id='rate' step='0.001' placeholder='如：1.800' oninput='onRegularInput()'>
    </div>
    <div class='field'>
      <label>貸款總期數（月）</label>
      <input type='number' id='totalMonths' placeholder='如：360' oninput='onRegularInput()'>
    </div>
  </div>
  <div class='row2'>
    <div class='field'>
      <label>還本方式</label>
      <select id='repaymentMethod' onchange='onRegularInput()'>
        <option value='本息均攤'>本息均攤（每月等額還款）</option>
        <option value='本金均攤'>本金均攤（每月等額還本）</option>
        <option value='只還利息'>只還利息（到期還本）</option>
        <option value='其他'>其他</option>
      </select>
    </div>
    <div class='field'>
      <label>每月還款金額（元）</label>
      <div style='display:flex;gap:6px'>
        <input type='number' id='monthlyPayment' placeholder='可自動計算' style='flex:1'>
        <button type='button' class='btn btn-sm btn-outline' onclick='calcMP()' id='calcMPBtn'>計算月付金</button>
      </div>
    </div>
  </div>

  <!-- 寬限期（僅房貸顯示） -->
  <div id='graceFields' style='display:none'>
    <div style='background:#fff9e6;border:1px solid #e6d87a;border-radius:6px;padding:12px 14px;margin-bottom:16px'>
      <strong>🏠 房貸寬限期設定</strong>
      <div class='row2' style='margin-top:10px'>
        <div class='field' style='margin-bottom:0'>
          <label>寬限期月數</label>
          <input type='number' id='gracePeriodMonths' placeholder='如：24' min='0' oninput='onGraceInput()'>
        </div>
        <div class='field' style='margin-bottom:0'>
          <label>寬限期結束日</label>
          <input type='date' id='gracePeriodEndDate'>
        </div>
      </div>
      <p style='font-size:.82rem;color:#888;margin:8px 0 0'>寬限期內只還利息，利率 × 借款金額 ÷ 12。寬限期結束後開始還本。</p>
    </div>
  </div>

  <!-- 計算預覽 -->
  <div class='calc-box' id='calcBox' style='display:none'>
    <div><span>月付金（本息均攤）</span><span id='c-mp'>—</span></div>
    <div id='c-grace-row' style='display:none'><span>寬限期月利息</span><span id='c-grace-int'>—</span></div>
    <div><span>計算目前餘額</span><span id='c-bal'><em>請填妥貸款日期</em></span></div>
    <div class='total'><span>估計年利息</span><span id='c-annual-int'>—</span></div>
  </div>

  <div class='row2'>
    <div class='field'>
      <label>起始日期</label>
      <input type='date' id='startDate' oninput='onRegularInput()'>
    </div>
    <div class='field'>
      <label>預計結清日期</label>
      <input type='date' id='endDate'>
    </div>
  </div>
  <div class='field'>
    <label>目前餘額（元）<small style='color:#888;font-weight:400'>　可自動計算後填入</small></label>
    <div style='display:flex;gap:6px'>
      <input type='number' id='balance' placeholder='可由下方自動計算' style='flex:1'>
      <button type='button' class='btn btn-sm btn-outline' onclick='fillBalance()'>帶入計算值</button>
    </div>
  </div>
</div>

<!-- ═══ 股票質押欄位 ═══ -->
<div id='stockFields' style='display:none'>
  <div style='background:#f0f4ff;border:1px solid #c0d0f0;border-radius:6px;padding:12px 14px;margin-bottom:16px'>
    <strong>📊 股票資訊</strong>
    <div class='row3' style='margin-top:10px'>
      <div class='field' style='margin-bottom:0'>
        <label>股號</label>
        <input id='stockCode' placeholder='如：0050' oninput='onStockInput()'>
      </div>
      <div class='field' style='margin-bottom:0'>
        <label>股名</label>
        <input id='stockName' placeholder='自動帶入'>
      </div>
      <div class='field' style='margin-bottom:0'>
        <label>張數</label>
        <input type='number' id='stockShares' placeholder='如：30' oninput='calcStockPreview()'>
      </div>
    </div>
    <div class='row3' style='margin-top:10px'>
      <div class='field' style='margin-bottom:0'>
        <label>即時股價</label>
        <div style='display:flex;gap:4px'>
          <input type='number' id='stockPrice' step='0.01' placeholder='自動帶入' style='flex:1' oninput='calcStockPreview()'>
          <button type='button' class='btn btn-sm btn-outline' onclick='fetchStockPrice()' id='fetchPriceBtn'>抓取</button>
        </div>
      </div>
      <div class='field' style='margin-bottom:0'>
        <label>市值（元）</label>
        <input id='stockMV' readonly style='background:#f8f8f8'>
      </div>
      <div class='field' style='margin-bottom:0'>
        <label>起息日</label>
        <input type='date' id='interestStartDate'>
      </div>
    </div>
  </div>
  <div style='background:#fff5f5;border:1px solid #f5b8b8;border-radius:6px;padding:12px 14px;margin-bottom:16px'>
    <strong>💰 借款資訊</strong>
    <div class='row3' style='margin-top:10px'>
      <div class='field' style='margin-bottom:0'>
        <label>借款金額（元）</label>
        <input type='number' id='s-principal' placeholder='如：3000000' oninput='calcStockPreview()'>
      </div>
      <div class='field' style='margin-bottom:0'>
        <label>借款利率（%）</label>
        <input type='number' id='s-rate' step='0.001' placeholder='如：2.500' oninput='calcStockPreview()'>
      </div>
      <div class='field' style='margin-bottom:0'>
        <label>起始日期</label>
        <input type='date' id='s-startDate'>
      </div>
    </div>
  </div>
  <div class='calc-box' id='stockCalcBox' style='display:none'>
    <div><span>日利息</span><span id='sc-daily'>—</span></div>
    <div><span>累計利息（自起息日）</span><span id='sc-interest'>—</span></div>
    <div><span>應償還金額</span><span id='sc-owed'>—</span></div>
    <div><span>維持率</span><span id='sc-mr'>—</span></div>
    <div class='total'><span>市值</span><span id='sc-mv2'>—</span></div>
  </div>
</div>

<div class='field'>
  <label>備註</label>
  <textarea id='notes' rows='2' placeholder='選填'></textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submitForm()'>確認新增</button>
  <a href='/debt' class='btn btn-outline'>取消</a>
</div>
</div>

<script>
const isStock = () => document.getElementById('loanType').value === '股票質押';

function onTypeChange() {
  const type = document.getElementById('loanType').value;
  const isS = type === '股票質押';
  document.getElementById('regularFields').style.display = isS ? 'none' : '';
  document.getElementById('stockFields').style.display = isS ? '' : 'none';
  document.getElementById('graceFields').style.display = (type === '房貸' && !isS) ? '' : 'none';
  const nameEl = document.getElementById('name');
  const bankEl = document.getElementById('bank');
  nameEl.value = '';
  bankEl.value = '';
  if (isS) {
    nameEl.value = '元大質借';
    bankEl.value = '元大證金';
  }
}

function onRegularInput() {
  const P = parseFloat(document.getElementById('principal').value) || 0;
  const r = parseFloat(document.getElementById('rate').value) || 0;
  const N = parseInt(document.getElementById('totalMonths').value) || 0;
  const method = document.getElementById('repaymentMethod').value;
  const startDate = document.getElementById('startDate').value;
  const graceMo = parseInt(document.getElementById('gracePeriodMonths')?.value) || 0;

  if (P > 0 && r > 0 && N > 0) {
    document.getElementById('calcBox').style.display = '';
    const rMo = r / 100 / 12;

    // 月付金
    let mp = 0;
    const Nadjust = (document.getElementById('loanType').value === '房貸' && graceMo > 0) ? N - graceMo : N;
    if (method === '本息均攤' && rMo > 0) {
      mp = P * rMo * Math.pow(1+rMo, Nadjust) / (Math.pow(1+rMo, Nadjust) - 1);
    } else if (method === '本金均攤') {
      mp = P / Nadjust + P * rMo; // first payment (decreases over time)
    } else if (method === '只還利息') {
      mp = P * rMo;
    }
    document.getElementById('c-mp').textContent = mp > 0 ? Math.round(mp).toLocaleString() + ' 元' : '—';

    // 寬限期利息
    const graceRow = document.getElementById('c-grace-row');
    const graceInt = document.getElementById('c-grace-int');
    if (document.getElementById('loanType').value === '房貸' && graceMo > 0) {
      graceRow.style.display = '';
      graceInt.textContent = Math.round(P * rMo).toLocaleString() + ' 元/月';
    } else {
      graceRow.style.display = 'none';
    }

    // 計算目前餘額
    const balEl = document.getElementById('c-bal');
    if (startDate) {
      const elapsed = monthsDiff(startDate);
      let bal = P;
      if (elapsed > 0) {
        let n = elapsed, Neff = Nadjust;
        if (document.getElementById('loanType').value === '房貸' && graceMo > 0) {
          if (elapsed <= graceMo) { bal = P; }
          else { n = elapsed - graceMo; Neff = N - graceMo; }
        }
        n = Math.min(n, Neff);
        if (method === '本息均攤' && rMo > 0) {
          const pN = Math.pow(1+rMo, Neff), pn = Math.pow(1+rMo, n);
          bal = Math.max(0, P * (pN - pn) / (pN - 1));
        } else if (method === '本金均攤') {
          bal = Math.max(0, P * (Neff - n) / Neff);
        }
      }
      balEl.textContent = Math.round(bal).toLocaleString() + ' 元';
      balEl.dataset.computed = bal;
    } else {
      balEl.textContent = '（請填入起始日期）';
      balEl.dataset.computed = '';
    }

    const annualInt = P * r / 100;
    document.getElementById('c-annual-int').textContent = Math.round(annualInt).toLocaleString() + ' 元';
  } else {
    document.getElementById('calcBox').style.display = 'none';
  }

  // 自動填入預計結清日期
  const sd = document.getElementById('startDate').value;
  const nm = parseInt(document.getElementById('totalMonths').value) || 0;
  if (sd && nm > 0) {
    const ed = new Date(sd);
    ed.setMonth(ed.getMonth() + nm);
    document.getElementById('endDate').value = ed.toISOString().slice(0,10);
  }
}

function onGraceInput() {
  const months = parseInt(document.getElementById('gracePeriodMonths').value) || 0;
  const start = document.getElementById('startDate').value;
  if (start && months > 0) {
    const d = new Date(start);
    d.setMonth(d.getMonth() + months);
    document.getElementById('gracePeriodEndDate').value = d.toISOString().slice(0,10);
  }
  onRegularInput();
}

function calcMP() {
  const P = parseFloat(document.getElementById('principal').value) || 0;
  const r = parseFloat(document.getElementById('rate').value) || 0;
  const N = parseInt(document.getElementById('totalMonths').value) || 0;
  const method = document.getElementById('repaymentMethod').value;
  const graceMo = parseInt(document.getElementById('gracePeriodMonths')?.value) || 0;
  const Neff = (document.getElementById('loanType').value === '房貸' && graceMo > 0) ? N - graceMo : N;
  if (!P || !r || !Neff) { alert('請填入借款金額、利率、期數'); return; }
  const rMo = r / 100 / 12;
  let mp = 0;
  if (method === '本息均攤') mp = P * rMo * Math.pow(1+rMo,Neff) / (Math.pow(1+rMo,Neff)-1);
  else if (method === '本金均攤') mp = P / Neff + P * rMo;
  if (mp > 0) document.getElementById('monthlyPayment').value = Math.round(mp);
}

function fillBalance() {
  const computed = document.getElementById('c-bal').dataset.computed;
  if (computed) document.getElementById('balance').value = Math.round(parseFloat(computed));
  else alert('請先填入起始日期及借款資料');
}

function monthsDiff(from) {
  const f = new Date(from); const t = new Date();
  return (t.getFullYear()-f.getFullYear())*12+(t.getMonth()-f.getMonth());
}

// ── 股票質押計算 ────────────────────────────────────────────────────────
async function fetchStockPrice() {
  const code = document.getElementById('stockCode').value.trim();
  if (!code) { alert('請輸入股號'); return; }
  const btn = document.getElementById('fetchPriceBtn');
  btn.textContent = '抓取中…'; btn.disabled = true;
  try {
    const r = await fetch('/api/stocks/info?codes=' + code);
    const data = await r.json();
    if (data[code]) {
      if (data[code].name) document.getElementById('stockName').value = data[code].name;
      if (data[code].price) document.getElementById('stockPrice').value = data[code].price;
      calcStockPreview();
    } else { alert('查無此股票，請手動填入股名與股價'); }
  } catch(e) { alert('抓取失敗，請手動填入'); }
  btn.textContent = '抓取'; btn.disabled = false;
}

async function onStockInput() {
  const code = document.getElementById('stockCode').value.trim();
  if (code.length < 4) return;
  try {
    const r = await fetch('/api/stocks/info?codes=' + code);
    const data = await r.json();
    if (data[code]) {
      if (data[code].name) document.getElementById('stockName').value = data[code].name;
      if (data[code].price) document.getElementById('stockPrice').value = data[code].price;
      calcStockPreview();
    }
  } catch(e) {}
}

function calcStockPreview() {
  const P = parseFloat(document.getElementById('s-principal').value) || 0;
  const r = parseFloat(document.getElementById('s-rate').value) || 0;
  const shares = parseFloat(document.getElementById('stockShares').value) || 0;
  const price = parseFloat(document.getElementById('stockPrice').value) || 0;
  const startDate = document.getElementById('interestStartDate').value || document.getElementById('s-startDate').value;

  const mv = price * shares * 1000;
  document.getElementById('stockMV').value = mv > 0 ? Math.round(mv).toLocaleString() + ' 元' : '';

  if (P > 0 && r > 0) {
    document.getElementById('stockCalcBox').style.display = '';
    const dailyInt = P * r / 100 / 365;
    const days = startDate ? Math.max(0, Math.floor((new Date()-new Date(startDate))/86400000)) : 0;
    const interest = dailyInt * days;
    const owed = P + interest;
    const mratio = owed > 0 && mv > 0 ? mv / owed * 100 : 0;

    document.getElementById('sc-daily').textContent = dailyInt.toFixed(2) + ' 元/天';
    document.getElementById('sc-interest').textContent = Math.round(interest).toLocaleString() + ' 元';
    document.getElementById('sc-owed').textContent = Math.round(owed).toLocaleString() + ' 元';
    document.getElementById('sc-mv2').textContent = mv > 0 ? Math.round(mv).toLocaleString() + ' 元' : '—';
    if (mratio > 0) {
      const cls = mratio < 130 ? 'mr-danger' : mratio < 140 ? 'mr-warn' : 'mr-ok';
      document.getElementById('sc-mr').innerHTML = `<span class='${cls}'>${mratio.toFixed(1)}%${mratio<130?' ⚠️追繳':mratio<140?' ⚠️警戒':''}</span>`;
    } else {
      document.getElementById('sc-mr').textContent = '—';
    }
  } else {
    document.getElementById('stockCalcBox').style.display = 'none';
  }
}

// ── 送出 ───────────────────────────────────────────────────────────────
async function submitForm() {
  const type = document.getElementById('loanType').value;
  const isS = type === '股票質押';

  const gv = id => { const el = document.getElementById(id); return el ? el.value : ''; };
  const gn = id => parseFloat(gv(id)) || 0;
  const gi = id => parseInt(gv(id)) || 0;

  const principal = isS ? gn('s-principal') : gn('principal');
  const rate      = isS ? gn('s-rate')      : gn('rate');
  const startDate = isS ? gv('s-startDate') : gv('startDate');

  const req = {
    name: gv('name').trim(),
    bank: gv('bank').trim(),
    borrower: gv('borrower').trim() || '周俊佑',
    loanType: type,
    principal,
    rate,
    startDate,
    endDate: gv('endDate'),
    notes: gv('notes').trim(),
    repaymentMethod: isS ? '只還利息' : gv('repaymentMethod'),
    monthlyPayment: gn('monthlyPayment'),
    totalMonths: gi('totalMonths'),
    gracePeriodMonths: gi('gracePeriodMonths'),
    gracePeriodEndDate: gv('gracePeriodEndDate'),
    balance: gn('balance'),
    stockCode: isS ? gv('stockCode').trim() : '',
    stockName: isS ? gv('stockName').trim() : '',
    stockPrice: isS ? gn('stockPrice') : 0,
    stockShares: isS ? gi('stockShares') : 0,
    interestStartDate: isS ? (gv('interestStartDate') || startDate) : ''
  };

  if (!req.name || !req.bank || req.principal <= 0) {
    showMsg('請填寫貸款名稱、銀行及借款金額', 'err'); return;
  }
  if (isS && !req.stockCode) { showMsg('請填寫股號', 'err'); return; }

  const r = await fetch('/api/debt', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(req)});
  if (r.ok) { showMsg('✓ 已新增！', 'ok'); setTimeout(() => location.href='/debt', 1200); }
  else { const t = await r.text(); showMsg(t || '新增失敗', 'err'); }
}
function showMsg(m,t){document.getElementById('msg').innerHTML=`<div class='alert ${t}'>${m}</div>`;}

// 預設今天日期
const todayStr = new Date().toISOString().slice(0,10);
document.getElementById('interestStartDate').value = todayStr;
document.getElementById('s-startDate').value = todayStr;
</script>
<style>
.mr-ok{color:#080;font-weight:700}
.mr-warn{color:#d80;font-weight:700}
.mr-danger{color:#c00;font-weight:700}
</style>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("新增貸款", "debt", "add", body));
    }

    // ── API ───────────────────────────────────────────────────────────────

    static async Task<IResult> AddLoan(LoanRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Bank) || req.Principal <= 0)
            return Results.BadRequest("請填寫必要資料");

        var balance = req.Balance > 0 ? req.Balance : req.Principal;
        var loan = new Loan(
            Guid.NewGuid().ToString("N")[..8],
            req.Name, req.Bank, req.LoanType ?? "其他",
            req.Principal, balance, req.Rate,
            req.StartDate ?? "", req.EndDate ?? "", req.Notes ?? "",
            req.RepaymentMethod ?? "其他",
            req.MonthlyPayment, req.TotalMonths,
            req.GracePeriodMonths, req.GracePeriodEndDate ?? "",
            req.StockCode ?? "", req.StockName ?? "",
            req.StockPrice, req.StockShares,
            req.InterestStartDate ?? "",
            string.IsNullOrWhiteSpace(req.Borrower) ? "周俊佑" : req.Borrower);

        var loans = LoadLoans();
        loans.Add(loan);
        SaveLoans(loans);
        return Results.Ok();
    }

    static IResult DeleteLoan(string id)
    {
        var loans = LoadLoans();
        var target = loans.FirstOrDefault(l => l.Id == id);
        if (target == null) return Results.NotFound();
        loans.Remove(target);
        SaveLoans(loans);
        return Results.Ok();
    }

    static async Task<IResult> UpdateBalance(string id, BalanceUpdateRequest req)
    {
        var loans = LoadLoans();
        var idx = loans.FindIndex(l => l.Id == id);
        if (idx < 0) return Results.NotFound();
        loans[idx] = loans[idx] with { Balance = req.Balance };
        SaveLoans(loans);
        return Results.Ok();
    }

    static IResult UpdateRate(string id, RateUpdateRequest req)
    {
        if (req.Rate <= 0) return Results.BadRequest("利率必須大於 0");
        var loans = LoadLoans();
        var idx = loans.FindIndex(l => l.Id == id);
        if (idx < 0) return Results.NotFound();

        var loan = loans[idx];
        var effectiveDate = req.EffectiveDate ?? DateTime.Today.ToString("yyyy-MM-dd");
        var history = loan.RateHistory?.ToList() ?? [];

        if (history.Count == 0)
        {
            // 從起息日到生效日建立初始紀錄
            var originStart = !string.IsNullOrEmpty(loan.InterestStartDate)
                ? loan.InterestStartDate
                : loan.StartDate;
            if (!string.IsNullOrEmpty(originStart))
                history.Add(new RateEntry(loan.Rate, originStart, effectiveDate));
        }
        else
        {
            // 關閉最後一筆開放紀錄
            var last = history[^1];
            if (last.EndDate == null)
                history[^1] = last with { EndDate = effectiveDate };
        }

        history.Add(new RateEntry(req.Rate, effectiveDate, null));
        loans[idx] = loan with { Rate = req.Rate, RateHistory = history };
        SaveLoans(loans);
        return Results.Ok();
    }

    static IResult GetLoans() => Results.Json(LoadLoans());

    // ── Data helpers ──────────────────────────────────────────────────────

    static List<Loan> LoadLoans()
    {
        if (!File.Exists(DebtsFile)) return [];
        return JsonSerializer.Deserialize<List<Loan>>(File.ReadAllText(DebtsFile), JsonOpts) ?? [];
    }

    static void SaveLoans(List<Loan> loans) =>
        File.WriteAllText(DebtsFile, JsonSerializer.Serialize(loans, JsonOpts));
}

// ── Models ────────────────────────────────────────────────────────────────────

public record Loan(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bank")] string Bank,
    [property: JsonPropertyName("loanType")] string LoanType,
    [property: JsonPropertyName("principal")] decimal Principal,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("rate")] decimal Rate,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("endDate")] string EndDate,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("repaymentMethod")] string RepaymentMethod,
    [property: JsonPropertyName("monthlyPayment")] decimal MonthlyPayment,
    [property: JsonPropertyName("totalMonths")] int TotalMonths,
    [property: JsonPropertyName("gracePeriodMonths")] int GracePeriodMonths,
    [property: JsonPropertyName("gracePeriodEndDate")] string GracePeriodEndDate,
    [property: JsonPropertyName("stockCode")] string StockCode,
    [property: JsonPropertyName("stockName")] string StockName,
    [property: JsonPropertyName("stockPrice")] decimal StockPrice,
    [property: JsonPropertyName("stockShares")] long StockShares,
    [property: JsonPropertyName("interestStartDate")] string InterestStartDate,
    [property: JsonPropertyName("borrower")] string Borrower = "周俊佑",
    [property: JsonPropertyName("rateHistory")] List<RateEntry>? RateHistory = null);

public record RateEntry(
    [property: JsonPropertyName("rate")] decimal Rate,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("endDate")] string? EndDate);

public record LoanRequest(
    string Name, string Bank, string? LoanType,
    decimal Principal, decimal Balance, decimal Rate,
    string? StartDate, string? EndDate, string? Notes,
    string? RepaymentMethod, decimal MonthlyPayment, int TotalMonths,
    int GracePeriodMonths, string? GracePeriodEndDate,
    string? StockCode, string? StockName, decimal StockPrice,
    long StockShares, string? InterestStartDate, string? Borrower);

public record BalanceUpdateRequest(decimal Balance);
public record RateUpdateRequest(decimal Rate, string? EffectiveDate);
