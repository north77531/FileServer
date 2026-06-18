using ClosedXML.Excel;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class StocksModule
{
    static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string HoldingsFile = Path.Combine(DataDir, "holdings.json");
    static readonly string TradesFile = Path.Combine(DataDir, "trades.json");
    static readonly string DividendsCacheFile = Path.Combine(DataDir, "dividends_cache.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    static readonly SemaphoreSlim Lock = new(1, 1);
    const string ExcelFile = @"C:\Users\USER\OneDrive\文件\個人網站\股票總張數統計.xlsx";
    const string ExcelSheet = "交易紀錄";
    static bool _enableExcel;

    public static void MapRoutes(WebApplication app)
    {
        _enableExcel = !app.Environment.IsDevelopment();
        app.MapGet("/stocks", InventoryPage);
        app.MapGet("/stocks/trade", TradePage);
        app.MapGet("/stocks/history", HistoryPage);
        app.MapGet("/stocks/dividends", DividendsHistoryPage);
        app.MapPost("/api/stocks/trade", AddTrade);
        app.MapGet("/api/stocks/prices", GetPrices);
        app.MapGet("/api/stocks/info", GetStockInfo);
        app.MapGet("/api/stocks/holdings", GetHoldings);
        app.MapGet("/api/stocks/dividends/history", GetDividendsHistory);
    }

    static async Task InventoryPage(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildInventoryHtml());
    }

    static async Task TradePage(HttpContext ctx)
    {
        var holdings = LoadHoldings();
        var accountTypes = holdings.Select(h => h.Type).Distinct().Order().ToList();
        var knownStocks = holdings.Select(h => new { h.Code, h.Name }).DistinctBy(x => x.Code).ToList();
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildTradeHtml(accountTypes, knownStocks.Select(x => $"{x.Code} {x.Name}").ToList()));
    }

    static async Task HistoryPage(HttpContext ctx)
    {
        var trades = LoadTrades();
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildHistoryHtml(trades));
    }

    static async Task DividendsHistoryPage(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildDividendsHistoryHtml());
    }

    static async Task<IResult> GetDividendsHistory(string codes, IHttpClientFactory httpFactory)
    {
        var codeList = codes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var client = httpFactory.CreateClient("finmind");
        var today = DateTime.Today;
        var currentYear = today.Year;
        var startYear = currentYear - 4;
        var pastYears = Enumerable.Range(startYear, 4).ToList(); // currentYear 排除

        // 載入快取 { code: { year: DivEntry } }
        var cache = LoadDividendsCache();
        var cacheUpdated = false;
        var result = new Dictionary<string, Dictionary<string, DivEntry>>();

        foreach (var code in codeList)
        {
            // 從快取取出過去年度
            cache.TryGetValue(code, out var codeCache);
            codeCache ??= [];
            var missingPastYears = pastYears.Where(y => !codeCache.ContainsKey(y)).ToList();

            // 決定抓取起始日：有缺漏的過去年度 → 從最早缺漏年，否則只抓今年
            var fetchStart = missingPastYears.Count > 0
                ? $"{missingPastYears.Min()}-01-01"
                : $"{currentYear}-01-01";

            DivEntry? currentEntry = null;
            try
            {
                var response = await client.GetStringAsync(
                    $"/api/v4/data?dataset=TaiwanStockDividend&data_id={code}&start_date={fetchStart}");
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("data", out var dataArr))
                {
                    var byYear = new Dictionary<int, (decimal cash, decimal stock)>();
                    var lastDates = new Dictionary<int, (string cashEx, string stockEx, string cashPay)>();

                    foreach (var item in dataArr.EnumerateArray())
                    {
                        // derive year: prefer confirmed ex/payment dates (year >= 2000);
                        // "0000-xx-xx" placeholders mean the date isn't confirmed yet
                        static int? ExtractYear(JsonElement el) {
                            var s = el.GetString() ?? "";
                            return s.Length >= 4 && int.TryParse(s[..4], out var yr) && yr >= 2000 ? yr : null;
                        }
                        int? year = null;
                        bool allDatesUnconfirmed = true;
                        foreach (var prop in new[] { "CashExDividendTradingDate", "StockExDividendTradingDate", "CashDividendPaymentDate" })
                        {
                            if (!item.TryGetProperty(prop, out var el)) continue;
                            var ey = ExtractYear(el);
                            if (ey != null) { allDatesUnconfirmed = false; year ??= ey; }
                            else if ((el.GetString() ?? "").Length >= 4) allDatesUnconfirmed = false; // has a non-zero date, just not helpful
                        }
                        // fallback to "date" field; if all ex-dates were "0000-xx-xx", this is an unconfirmed current-year dividend
                        if (year == null)
                        {
                            if (item.TryGetProperty("date", out var dd))
                                year = allDatesUnconfirmed ? currentYear : ExtractYear(dd);
                            else if (allDatesUnconfirmed)
                                year = currentYear;
                        }
                        if (year == null || year < startYear) continue;

                        var cash = item.TryGetProperty("CashEarningsDistribution", out var ce) ? ce.GetDecimal() : 0m;
                        var stock = item.TryGetProperty("StockEarningsDistribution", out var se) ? se.GetDecimal() : 0m;
                        var y = year.Value;
                        byYear.TryGetValue(y, out var prev);
                        byYear[y] = (prev.cash + cash, prev.stock + stock);

                        var cashEx = item.TryGetProperty("CashExDividendTradingDate", out var cex) ? cex.GetString() ?? "" : "";
                        var stockEx = item.TryGetProperty("StockExDividendTradingDate", out var sex) ? sex.GetString() ?? "" : "";
                        var cashPay = item.TryGetProperty("CashDividendPaymentDate", out var cp) ? cp.GetString() ?? "" : "";
                        lastDates[y] = (cashEx, stockEx, cashPay);
                    }

                    foreach (var kv in byYear)
                    {
                        lastDates.TryGetValue(kv.Key, out var dates);
                        var entry = new DivEntry(kv.Value.cash, kv.Value.stock, dates.cashEx ?? "", dates.stockEx ?? "", dates.cashPay ?? "");
                        if (kv.Key == currentYear)
                            currentEntry = entry;
                        else
                        {
                            // 過去年度寫入快取
                            codeCache[kv.Key] = entry;
                            cacheUpdated = true;
                        }
                    }
                }
            }
            catch { }

            // 合併快取 + 今年即時資料
            var merged = new Dictionary<string, DivEntry>();
            foreach (var y in pastYears)
                if (codeCache.TryGetValue(y, out var e)) merged[y.ToString()] = e;
            if (currentEntry != null) merged[currentYear.ToString()] = currentEntry;

            result[code] = merged;
            cache[code] = codeCache;
        }

        if (cacheUpdated) SaveDividendsCache(cache);
        return Results.Json(result);
    }

    static async Task<IResult> AddTrade(NewTradeRequest req, HttpContext ctx)
    {
        if (string.IsNullOrWhiteSpace(req.StockCode) || req.Price <= 0 || req.Shares <= 0)
            return Results.BadRequest("請填寫完整交易資料");

        var date = string.IsNullOrEmpty(req.Date) ? DateTime.Today.ToString("yyyy-MM-dd") : req.Date;
        var isEtf = req.StockCode.StartsWith("00");
        var totalAmount = req.Price * req.Shares;
        var commission = Math.Max(20, (long)Math.Floor(totalAmount * 0.001425m));
        long tax = req.TradeType == "現賣" ? (long)Math.Floor(totalAmount * (isEtf ? 0.001m : 0.003m)) : 0;
        var netAmount = req.TradeType == "現買"
            ? -(long)(totalAmount + commission)
            : (long)(totalAmount - commission - tax);

        var trade = new Trade(
            req.StockName, req.StockCode, date, req.Shares, netAmount,
            req.TradeType, req.Price, (long)totalAmount, commission, tax, req.OrderNo ?? "");

        if (_enableExcel && File.Exists(ExcelFile))
        {
            try { using var fs = File.Open(ExcelFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                return Results.BadRequest("請先關閉 Excel 檔案「股票總張數統計.xlsx」再新增交易");
            }
        }

        await Lock.WaitAsync();
        try
        {
            var trades = LoadTrades();
            trades.Add(trade);
            SaveTrades(trades);
            if (_enableExcel) AppendTradeToExcel(trade);

            var holdings = LoadHoldings();
            var existing = holdings.FirstOrDefault(h => h.Type == req.AccountType && h.Code == req.StockCode);

            if (req.TradeType == "現買" || req.TradeType == "配股")
            {
                var addedZhang = (int)(req.Shares / 1000);
                if (existing != null)
                {
                    var idx = holdings.IndexOf(existing);
                    holdings[idx] = existing with { Shares = existing.Shares + addedZhang, Cost = existing.Cost + (long)totalAmount };
                }
                else
                {
                    holdings.Add(new Holding(req.AccountType, req.StockCode, req.StockName, addedZhang, (long)totalAmount));
                }
            }
            else if (req.TradeType == "現賣" && existing != null)
            {
                var soldZhang = (int)(req.Shares / 1000);
                var newShares = existing.Shares - soldZhang;
                if (newShares <= 0)
                    holdings.Remove(existing);
                else
                {
                    var costPerZhang = existing.Cost / existing.Shares;
                    holdings[holdings.IndexOf(existing)] = existing with
                    {
                        Shares = newShares,
                        Cost = existing.Cost - costPerZhang * soldZhang
                    };
                }
            }
            SaveHoldings(holdings);
        }
        finally { Lock.Release(); }

        return Results.Ok(new { message = "交易紀錄已儲存", netAmount });
    }

    static async Task<IResult> GetStockInfo(string codes, IHttpClientFactory httpFactory)
    {
        try
        {
            var codeList = codes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var tseStr = string.Join("|", codeList.Select(c => $"tse_{c}.tw"));
            var client = httpFactory.CreateClient("twse");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://mis.twse.com.tw/");

            var response = await client.GetStringAsync(
                $"/stock/api/getStockInfo.jsp?ex_ch={tseStr}&json=1&delay=0");

            using var doc = JsonDocument.Parse(response);
            var result = new Dictionary<string, object>();

            if (doc.RootElement.TryGetProperty("msgArray", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var code = item.GetProperty("c").GetString()!;
                    var name = item.TryGetProperty("n", out var nEl) ? nEl.GetString() ?? "" : "";
                    var zStr = item.TryGetProperty("z", out var zEl) ? zEl.GetString() : null;
                    var yStr = item.TryGetProperty("y", out var yEl) ? yEl.GetString() : null;
                    var priceStr = (zStr != null && zStr != "-") ? zStr : yStr;
                    decimal.TryParse(priceStr, out var price);
                    result[code] = new { name, price };
                }
            }
            return Results.Json(result);
        }
        catch
        {
            return Results.Json(new Dictionary<string, object>());
        }
    }

    static async Task<IResult> GetPrices(string codes, IHttpClientFactory httpFactory)
    {
        try
        {
            var codeList = codes.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var tseStr = string.Join("|", codeList.Select(c => $"tse_{c}.tw"));
            var client = httpFactory.CreateClient("twse");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://mis.twse.com.tw/");

            var response = await client.GetStringAsync(
                $"/stock/api/getStockInfo.jsp?ex_ch={tseStr}&json=1&delay=0");

            using var doc = JsonDocument.Parse(response);
            var result = new Dictionary<string, object>();

            if (doc.RootElement.TryGetProperty("msgArray", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var code = item.GetProperty("c").GetString()!;
                    var zStr = item.TryGetProperty("z", out var zEl) ? zEl.GetString() : null;
                    var yStr = item.TryGetProperty("y", out var yEl) ? yEl.GetString() : null;
                    var priceStr = (zStr != null && zStr != "-") ? zStr : yStr;
                    if (decimal.TryParse(priceStr, out var price))
                        result[code] = price;
                }
            }
            return Results.Json(result);
        }
        catch
        {
            return Results.Json(new Dictionary<string, object>());
        }
    }

    static IResult GetHoldings()
    {
        return Results.Json(LoadHoldings());
    }

    // ── Data helpers ──────────────────────────────────────────────────────

    static List<Holding> LoadHoldings()
    {
        if (!File.Exists(HoldingsFile)) return [];
        var json = File.ReadAllText(HoldingsFile);
        return JsonSerializer.Deserialize<List<Holding>>(json, JsonOpts) ?? [];
    }

    static List<Trade> LoadTrades()
    {
        if (!File.Exists(TradesFile)) return [];
        var json = File.ReadAllText(TradesFile);
        return JsonSerializer.Deserialize<List<Trade>>(json, JsonOpts) ?? [];
    }

    static void SaveHoldings(List<Holding> holdings) =>
        File.WriteAllText(HoldingsFile, JsonSerializer.Serialize(holdings, JsonOpts));

    static void SaveTrades(List<Trade> trades) =>
        File.WriteAllText(TradesFile, JsonSerializer.Serialize(trades, JsonOpts));

    static Dictionary<string, Dictionary<int, DivEntry>> LoadDividendsCache()
    {
        if (!File.Exists(DividendsCacheFile)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, DivEntry>>>(File.ReadAllText(DividendsCacheFile), JsonOpts) ?? []; }
        catch { return []; }
    }

    static void SaveDividendsCache(Dictionary<string, Dictionary<int, DivEntry>> cache) =>
        File.WriteAllText(DividendsCacheFile, JsonSerializer.Serialize(cache, JsonOpts));

    static void AppendTradeToExcel(Trade t)
    {
        try
        {
            using var wb = File.Exists(ExcelFile)
                ? new XLWorkbook(ExcelFile)
                : new XLWorkbook();

            var ws = wb.Worksheets.TryGetWorksheet(ExcelSheet, out var existing)
                ? existing
                : wb.Worksheets.Add(ExcelSheet);

            // 若工作表全新則補欄位標題
            if (ws.LastRowUsed() == null)
            {
                var h = ws.Row(1);
                string[] headers = ["股名","日期","成交股數","淨收付金額","買賣別","成交價","成本","手續費","交易稅","融資金額/券擔保品","資自借款/券保證金","利息","稅款","券手續費/標借費","委託書號"];
                for (int i = 0; i < headers.Length; i++)
                    h.Cell(i + 1).Value = headers[i];
            }

            var next = (ws.LastRowUsed()?.RowNumber() ?? 0) + 1;
            var r = ws.Row(next);
            r.Cell(1).Value = t.StockName;
            if (DateTime.TryParse(t.Date, out var d)) r.Cell(2).Value = d;
            else r.Cell(2).Value = t.Date;
            r.Cell(3).Value = t.Shares;
            r.Cell(4).Value = t.NetAmount;
            r.Cell(5).Value = t.TradeType;
            r.Cell(6).Value = (double)t.Price;
            r.Cell(7).Value = t.Cost;
            r.Cell(8).Value = t.Commission;
            r.Cell(9).Value = t.Tax;
            r.Cell(10).Value = 0;
            r.Cell(11).Value = 0;
            r.Cell(12).Value = 0;
            r.Cell(13).Value = 0;
            r.Cell(14).Value = 0;
            r.Cell(15).Value = t.OrderNo;

            wb.SaveAs(ExcelFile);
        }
        catch { /* Excel 寫入失敗不影響主流程 */ }
    }

    // ── HTML builders ────────────────────────────────────────────────────

    static string NavBar(string active) => SharedLayout.Nav("stocks", active);

    static string CommonCss => SharedLayout.Css + @"
.summary{display:flex;gap:12px;margin-bottom:20px;flex-wrap:wrap}
.summary .card{background:#fff;border-radius:8px;padding:14px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08);min-width:160px}
.summary .card .label{font-size:.78rem;color:#777;margin-bottom:4px}
.summary .card .value{font-size:1.3rem;font-weight:600}
th{text-align:right}
th:first-child,th:nth-child(2),th:nth-child(3){text-align:left}
td{text-align:right}
td:first-child,td:nth-child(2),td:nth-child(3){text-align:left}
.refresh{font-size:.8rem;color:#999;margin-left:8px}";

    static string BuildInventoryHtml()
    {
        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>股票庫存</title>
<style>{CommonCss}
body{{max-width:1200px}}
.summary{{display:flex;gap:12px;margin-bottom:20px;flex-wrap:wrap}}
.card{{background:#fff;border-radius:8px;padding:14px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08);min-width:160px}}
.card .label{{font-size:.78rem;color:#777;margin-bottom:4px}}
.card .value{{font-size:1.3rem;font-weight:600}}
.refresh{{font-size:.8rem;color:#999;margin-left:8px}}
.table-wrap{{overflow-x:auto}}
</style></head><body>
{NavBar("stocks")}
<h1>📈 股票庫存統計 <span class='refresh' id='ts'></span></h1>
<div class='summary' id='summary'>
  <div class='card'><div class='label'>總成本</div><div class='value' id='s-cost'>載入中…</div></div>
  <div class='card'><div class='label'>現值</div><div class='value' id='s-value'>—</div></div>
  <div class='card'><div class='label'>損益</div><div class='value' id='s-pnl'>—</div></div>
  <div class='card'><div class='label'>損益率</div><div class='value' id='s-pct'>—</div></div>
</div>
<div class='table-wrap'>
<table>
<thead><tr>
  <th>帳戶</th><th>股號</th><th>股名</th>
  <th>張數</th><th>成本(元)</th><th>成本均價</th>
  <th>即時股價</th><th>現值(元)</th><th>損益(元)</th><th>損益率</th>
</tr></thead>
<tbody id='tbody'><tr><td colspan='10' style='text-align:center;padding:20px;color:#999'>載入中…</td></tr></tbody>
<tfoot><tr>
  <td colspan='4'>合計</td>
  <td id='f-cost'>—</td><td>—</td><td>—</td>
  <td id='f-value'>—</td><td id='f-pnl'>—</td><td id='f-pct'>—</td>
</tr></tfoot>
</table>
</div>
<script>
const fmt = n => Math.round(n).toLocaleString('zh-TW');
const fmtP = n => (n >= 0 ? '+' : '') + n.toFixed(2) + '%';
const cls = n => n >= 0 ? 'pos' : 'neg';

async function load() {{
  const res = await fetch('/api/stocks/holdings');
  const holdings = await res.json();
  const codes = [...new Set(holdings.map(h => h.code))].join(',');

  let prices = {{}};
  try {{
    const pr = await fetch('/api/stocks/prices?codes=' + codes);
    prices = await pr.json();
  }} catch(e) {{}}

  const tbody = document.getElementById('tbody');
  let rows = '', totCost = 0, totValue = 0;

  for (const h of holdings) {{
    const costPerShare = h.shares > 0 ? h.cost / (h.shares * 1000) : 0;
    const price = prices[h.code];
    const value = price ? price * h.shares * 1000 : null;
    const pnl = value != null ? value - h.cost : null;
    const pct = pnl != null && h.cost > 0 ? pnl / h.cost * 100 : null;
    totCost += h.cost;
    if (value != null) totValue += value;

    rows += `<tr>
      <td>${{h.type}}</td><td>${{h.code}}</td><td>${{h.name}}</td>
      <td>${{h.shares}}</td><td>${{fmt(h.cost)}}</td>
      <td>${{costPerShare.toFixed(2)}}</td>
      <td>${{price != null ? price.toFixed(2) : '<span class=loading>—</span>'}}</td>
      <td>${{value != null ? fmt(value) : '<span class=loading>—</span>'}}</td>
      <td class='${{pnl != null ? cls(pnl) : ''}}'>${{pnl != null ? fmt(pnl) : '—'}}</td>
      <td class='${{pct != null ? cls(pct) : ''}}'>${{pct != null ? fmtP(pct) : '—'}}</td>
    </tr>`;
  }}

  tbody.innerHTML = rows || '<tr><td colspan=10 style=text-align:center>尚無庫存</td></tr>';

  const totPnl = totValue - totCost;
  const totPct = totCost > 0 ? totPnl / totCost * 100 : 0;
  document.getElementById('f-cost').textContent = fmt(totCost);
  document.getElementById('f-value').textContent = totValue > 0 ? fmt(totValue) : '—';
  document.getElementById('f-pnl').textContent = totValue > 0 ? fmt(totPnl) : '—';
  document.getElementById('f-pct').textContent = totValue > 0 ? fmtP(totPct) : '—';
  if (totValue > 0) {{
    document.getElementById('f-pnl').className = cls(totPnl);
    document.getElementById('f-pct').className = cls(totPnl);
  }}

  document.getElementById('s-cost').textContent = fmt(totCost);
  if (totValue > 0) {{
    document.getElementById('s-value').textContent = fmt(totValue);
    document.getElementById('s-pnl').textContent = (totPnl >= 0 ? '+' : '') + fmt(totPnl);
    document.getElementById('s-pnl').className = 'value ' + cls(totPnl);
    document.getElementById('s-pct').textContent = fmtP(totPct);
    document.getElementById('s-pct').className = 'value ' + cls(totPnl);
  }}
  document.getElementById('ts').textContent = '更新: ' + new Date().toLocaleTimeString('zh-TW');
}}

load();
setInterval(load, 60000);
</script>
</body></html>";
    }

    static string BuildTradeHtml(List<string> accountTypes, List<string> knownStocks)
    {
        var atOptions = string.Join("", accountTypes.Select(a => $"<option value='{a}'>{a}</option>"));
        var stockList = string.Join("", knownStocks.Select(s => $"<option value='{s.Split(' ')[0]}'>{s}</option>"));
        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>新增交易</title>
<style>{CommonCss}
.form-card{{background:#fff;border-radius:8px;padding:24px;box-shadow:0 1px 4px rgba(0,0,0,.08);max-width:520px}}
.field{{margin-bottom:16px}}
label{{display:block;font-size:.85rem;color:#555;margin-bottom:4px;font-weight:500}}
input,select{{width:100%;padding:9px 12px;border:1px solid #ddd;border-radius:6px;font-size:.95rem;box-sizing:border-box}}
input:focus,select:focus{{outline:none;border-color:#0066cc;box-shadow:0 0 0 2px rgba(0,102,204,.15)}}
.row2{{display:grid;grid-template-columns:1fr 1fr;gap:12px}}
.calc-box{{background:#f5f8ff;border:1px solid #dce6ff;border-radius:6px;padding:12px 14px;font-size:.88rem;margin-bottom:16px}}
.calc-box div{{display:flex;justify-content:space-between;padding:2px 0}}
.calc-box .total{{font-weight:600;border-top:1px solid #c0d0f0;margin-top:6px;padding-top:6px}}
.btn{{width:100%;padding:11px;background:#0066cc;color:#fff;border:none;border-radius:6px;font-size:1rem;cursor:pointer;font-weight:500}}
.btn:hover{{background:#0055aa}}
.alert{{padding:10px 14px;border-radius:6px;margin-bottom:16px;font-size:.9rem}}
.alert.ok{{background:#e6f4ea;color:#1e7e34;border:1px solid #b8dfc0}}
.alert.err{{background:#fde8e8;color:#c0392b;border:1px solid #f5b8b8}}
</style></head><body>
{NavBar("trade")}
<h1>💹 新增交易</h1>
<div class='form-card'>
<div id='msg'></div>
<div class='field'>
  <label>買賣別</label>
  <select id='tradeType' onchange='calc()'>
    <option value='現買'>現買</option>
    <option value='現賣'>現賣</option>
    <option value='配股'>配股</option>
  </select>
</div>
<div class='row2'>
  <div class='field'>
    <label>股號</label>
    <input id='code' list='stockList' placeholder='如: 0050' oninput='onCodeInput()'>
    <datalist id='stockList'>{stockList}</datalist>
  </div>
  <div class='field'>
    <label>股名</label>
    <input id='name' placeholder='自動帶入或手動填寫'>
  </div>
</div>
<div class='row2'>
  <div class='field'>
    <label>成交股數</label>
    <input type='number' id='shares' placeholder='如: 1000' min='1' oninput='calc()'>
  </div>
  <div class='field'>
    <label>成交價</label>
    <input type='number' id='price' placeholder='如: 100.5' step='0.01' min='0' oninput='calc()'>
  </div>
</div>
<div class='row2'>
  <div class='field'>
    <label>帳戶</label>
    <input id='accountType' list='acList' placeholder='如: 已質借元大'>
    <datalist id='acList'>{atOptions}</datalist>
  </div>
  <div class='field'>
    <label>日期</label>
    <input type='date' id='date'>
  </div>
</div>
<div class='field'>
  <label>委託書號 (選填)</label>
  <input id='orderNo' placeholder='選填'>
</div>
<div class='calc-box' id='calcBox' style='display:none'>
  <div><span>成交金額</span><span id='c-amount'>—</span></div>
  <div><span>手續費 (0.1425%)</span><span id='c-fee'>—</span></div>
  <div id='c-tax-row'><span>交易稅</span><span id='c-tax'>—</span></div>
  <div class='total'><span id='c-dir'>應付金額</span><span id='c-net'>—</span></div>
</div>
<button class='btn' onclick='submit()'>確認送出</button>
</div>
<script>
const today = new Date().toISOString().slice(0,10);
document.getElementById('date').value = today;

const stockMap = {{{string.Join(",", knownStocks.Select(s => { var p = s.Split(' ', 2); return $"'{p[0]}':'{(p.Length > 1 ? p[1] : p[0])}'"; }))}}};

function onCodeInput() {{
  const code = document.getElementById('code').value.trim();
  if (stockMap[code]) document.getElementById('name').value = stockMap[code];
  calc();
}}

function calc() {{
  const type = document.getElementById('tradeType').value;
  const shares = parseFloat(document.getElementById('shares').value) || 0;
  const price = parseFloat(document.getElementById('price').value) || 0;
  const code = document.getElementById('code').value.trim();
  if (!shares || !price) {{ document.getElementById('calcBox').style.display='none'; return; }}

  const amount = price * shares;
  const fee = Math.max(20, Math.floor(amount * 0.001425));
  const isEtf = code.startsWith('00');
  const tax = type === '現賣' ? Math.floor(amount * (isEtf ? 0.001 : 0.003)) : 0;
  const net = type === '現買' ? amount + fee : amount - fee - tax;

  document.getElementById('calcBox').style.display = '';
  document.getElementById('c-amount').textContent = Math.round(amount).toLocaleString();
  document.getElementById('c-fee').textContent = fee.toLocaleString();
  document.getElementById('c-tax-row').style.display = type === '現賣' ? '' : 'none';
  document.getElementById('c-tax').textContent = tax.toLocaleString();
  document.getElementById('c-dir').textContent = type === '現買' ? '應付金額' : '應收金額';
  document.getElementById('c-net').textContent = Math.round(net).toLocaleString();
}}

async function submit() {{
  const req = {{
    stockCode: document.getElementById('code').value.trim(),
    stockName: document.getElementById('name').value.trim(),
    accountType: document.getElementById('accountType').value.trim(),
    tradeType: document.getElementById('tradeType').value,
    price: parseFloat(document.getElementById('price').value),
    shares: parseInt(document.getElementById('shares').value),
    date: document.getElementById('date').value,
    orderNo: document.getElementById('orderNo').value.trim()
  }};
  if (!req.stockCode || !req.shares || !req.price) {{
    showMsg('請填寫股號、股數及成交價', 'err'); return;
  }}
  if (!req.accountType) {{ showMsg('請填寫帳戶', 'err'); return; }}
  try {{
    const res = await fetch('/api/stocks/trade', {{
      method: 'POST', headers: {{'Content-Type': 'application/json'}},
      body: JSON.stringify(req)
    }});
    const data = await res.json();
    if (res.ok) {{
      showMsg('✓ 交易紀錄已儲存！' + (data.netAmount ? ' 淨收付：' + Math.round(data.netAmount).toLocaleString() + ' 元' : ''), 'ok');
      document.getElementById('shares').value = '';
      document.getElementById('price').value = '';
      document.getElementById('orderNo').value = '';
      document.getElementById('calcBox').style.display = 'none';
    }} else {{ showMsg(data || '儲存失敗', 'err'); }}
  }} catch(e) {{ showMsg('網路錯誤', 'err'); }}
}}

function showMsg(msg, type) {{
  const el = document.getElementById('msg');
  el.innerHTML = `<div class='alert ${{type}}'>${{msg}}</div>`;
  setTimeout(() => el.innerHTML = '', 4000);
}}
</script></body></html>";
    }

    static string BuildDividendsHistoryHtml()
    {
        var currentYear = DateTime.Today.Year;
        var years = Enumerable.Range(currentYear - 4, 5).ToList();
        // past years: colspan=2, current year: colspan=6
        var yearHeaders = string.Join("", years.Select(y =>
            y == currentYear ? $"<th colspan='6'>{y} 年</th>" : $"<th colspan='2'>{y} 年</th>"));
        var yearSubHeaders = string.Join("", years.Select(y =>
            y == currentYear
                ? "<th>現金(元)</th><th>股票(股)</th><th>除息交易日</th><th>除權交易日</th><th>現金股利發放日</th><th>股票股利發放日</th>"
                : "<th>現金(元)</th><th>股票(股)</th>"));
        var colCount = 3 + (years.Count - 1) * 2 + 6;

        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>歷史配息</title>
<style>{CommonCss}
body{{max-width:1800px}}
.table-wrap{{overflow-x:auto}}
th{{white-space:nowrap}}
thead tr:first-child th{{text-align:center}}
thead tr:first-child th:first-child{{text-align:left}}
td.year-cash{{text-align:right;color:#d00}}
td.year-stock{{text-align:right;color:#080}}
td.empty{{color:#ccc;text-align:right}}
td.date-col{{text-align:center;font-size:.82rem;color:#555}}
th.cur-year{{background:#fffbe6;color:#7a5500}}
td.cur-year{{background:#fffef5}}
</style></head><body>
{NavBar("dividends")}
<h1>💰 歷史配息紀錄（近五年）</h1>
<div class='table-wrap'>
<table>
<thead>
  <tr>
    <th rowspan='2'>股號</th><th rowspan='2'>股名</th><th rowspan='2'>帳戶</th>
    {yearHeaders}
  </tr>
  <tr>{yearSubHeaders}</tr>
</thead>
<tbody id='tbody'><tr><td colspan='{colCount}' style='text-align:center;padding:20px;color:#999'>載入中…</td></tr></tbody>
</table>
</div>
<p style='font-size:.8rem;color:#999;margin-top:12px'>資料來源：FinMind。季配息或半年配自動累計為年度總額。當年度尚無資料時顯示空白。</p>
<script>
const years = [{string.Join(",", years)}];
const currentYear = {currentYear};

async function load() {{
  const res = await fetch('/api/stocks/holdings');
  const holdings = await res.json();
  if (!holdings.length) {{
    document.getElementById('tbody').innerHTML = '<tr><td colspan={colCount} style=text-align:center>尚無庫存</td></tr>';
    return;
  }}
  const codes = [...new Set(holdings.map(h => h.code))].join(',');
  let divs = {{}};
  try {{
    const dr = await fetch('/api/stocks/dividends/history?codes=' + codes);
    divs = await dr.json();
  }} catch(e) {{}}

  const tbody = document.getElementById('tbody');
  let rows = '';
  for (const h of holdings) {{
    const d = divs[h.code] || {{}};
    let cells = '';
    for (const y of years) {{
      const yr = d[y.toString()];
      const isCur = y === currentYear;
      if (yr == null) {{
        cells += isCur
          ? `<td class='empty cur-year'></td><td class='empty cur-year'></td><td class='cur-year'></td><td class='cur-year'></td><td class='cur-year'></td><td class='cur-year'></td>`
          : `<td class='empty'>—</td><td class='empty'>—</td>`;
      }} else {{
        const cash = yr.cashDiv > 0 ? yr.cashDiv.toFixed(2) : '—';
        const stock = yr.stockDiv > 0 ? yr.stockDiv.toFixed(4) : '—';
        cells += `<td class='${{yr.cashDiv > 0 ? 'year-cash' : 'empty'}}${{isCur ? ' cur-year' : ''}}'>${{cash}}</td>`;
        cells += `<td class='${{yr.stockDiv > 0 ? 'year-stock' : 'empty'}}${{isCur ? ' cur-year' : ''}}'>${{stock}}</td>`;
        if (isCur) {{
          cells += `<td class='date-col cur-year'>${{yr.cashExDate || ''}}</td>`;
          cells += `<td class='date-col cur-year'>${{yr.stockExDate || ''}}</td>`;
          cells += `<td class='date-col cur-year'>${{yr.cashPayDate || ''}}</td>`;
          cells += `<td class='date-col cur-year'></td>`;
        }}
      }}
    }}
    rows += `<tr><td>${{h.code}}</td><td>${{h.name}}</td><td>${{h.type}}</td>${{cells}}</tr>`;
  }}
  tbody.innerHTML = rows;
}}

load();
</script>
</body></html>";
    }

    static string BuildHistoryHtml(List<Trade> trades)
    {
        var sorted = trades.OrderByDescending(t => t.Date).ToList();
        var rows = sorted.Select(t => {
            var isBuy = t.TradeType == "現買" || t.TradeType == "配股";
            var netCls = isBuy ? "pos" : "neg";
            return $@"<tr>
<td>{t.Date}</td><td>{t.StockCode}</td><td>{t.StockName}</td>
<td>{t.TradeType}</td><td>{t.Shares:N0}</td><td>{t.Price:N2}</td>
<td>{t.Cost:N0}</td><td>{t.Commission:N0}</td><td>{t.Tax:N0}</td>
<td class='{netCls}'>{t.NetAmount:N0}</td><td>{t.OrderNo}</td></tr>";
        });

        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>交易紀錄</title>
<style>{CommonCss}
th,td{{white-space:nowrap}}
th:nth-child(n+5),td:nth-child(n+5){{text-align:right}}
th:nth-child(1),th:nth-child(2),th:nth-child(3),th:nth-child(4){{text-align:left}}
td:nth-child(1),td:nth-child(2),td:nth-child(3),td:nth-child(4){{text-align:left}}
.table-wrap{{overflow-x:auto}}
</style></head><body>
{NavBar("history")}
<h1>📋 交易紀錄 ({sorted.Count} 筆)</h1>
<div class='table-wrap'>
<table>
<thead><tr>
<th>日期</th><th>股號</th><th>股名</th><th>買賣別</th>
<th>股數</th><th>成交價</th><th>成交金額</th><th>手續費</th><th>交易稅</th>
<th>淨收付</th><th>委託書號</th>
</tr></thead>
<tbody>{string.Join("", rows)}</tbody>
</table>
</div>
</body></html>";
    }
}

// ── Models ───────────────────────────────────────────────────────────────────

public record DivEntry(
    [property: JsonPropertyName("cashDiv")] decimal CashDiv,
    [property: JsonPropertyName("stockDiv")] decimal StockDiv,
    [property: JsonPropertyName("cashExDate")] string CashExDate,
    [property: JsonPropertyName("stockExDate")] string StockExDate,
    [property: JsonPropertyName("cashPayDate")] string CashPayDate);

public record Holding(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shares")] int Shares,
    [property: JsonPropertyName("cost")] long Cost);

public record Trade(
    [property: JsonPropertyName("stockName")] string StockName,
    [property: JsonPropertyName("stockCode")] string StockCode,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("shares")] long Shares,
    [property: JsonPropertyName("netAmount")] long NetAmount,
    [property: JsonPropertyName("tradeType")] string TradeType,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("cost")] long Cost,
    [property: JsonPropertyName("commission")] long Commission,
    [property: JsonPropertyName("tax")] long Tax,
    [property: JsonPropertyName("orderNo")] string OrderNo);

public record NewTradeRequest(
    string StockCode,
    string StockName,
    string AccountType,
    string TradeType,
    decimal Price,
    long Shares,
    string? Date,
    string? OrderNo);
