using System.Text.Json;

// 集保 E 存摺交易明細匯入：
// 存摺 PDF 有文字層，由瀏覽器端 pdf.js 抽出每列（見 StocksModule 新增交易頁腳本），
// 這裡負責與現有交易紀錄比對、帶入當日收盤價產生預覽，使用者勾選確認後才寫入。
// 存摺同一天同股票同方向會合併成一列（例如分兩筆各 1,000 股買進 → 一列 2,000 股），
// 所以以「日期＋股號＋買賣別」加總股數比對，已登錄的股數不會再匯入第二次。
public static class PassbookImport
{
    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // 系統裡日期不同但股數相同的交易，相差幾天內視為「疑似同一筆」（手動登錄時日期填錯）
    const int SuspectDays = 5;

    public static void MapRoutes(WebApplication app)
    {
        app.MapPost("/api/stocks/trade/passbook/preview", Preview);
        app.MapPost("/api/stocks/trade/passbook/apply", Apply);
    }

    // ── 1. 存摺列 → 預覽（比對現有交易）──────────────────────────────────

    static async Task<IResult> Preview(PassbookRequest req, IHttpClientFactory httpFactory)
    {
        if (req.Rows == null) return Results.BadRequest("沒有存摺明細");
        var preview = BuildPreview(req.Rows, StocksModule.LoadTrades());
        await FillPrices(preview.Items, httpFactory);
        return Results.Json(preview, WebJson);
    }

    static string? TradeType(string memo) =>
        memo.Contains("買進") ? "現買"
        : memo.Contains("賣出") ? "現賣"
        : memo.Contains("配股") || memo.Contains("除權") ? "配股"
        : null;

    static string Key(string date, string code, string type) => $"{date}|{code}|{type}";

    static readonly System.Text.RegularExpressions.Regex CodePattern = new(@"^\d{4,6}[A-Z]?$");

    // 股名會原樣輸出在其他頁面，拿掉 HTML 特殊字元
    static string CleanName(string? s) =>
        new string((s ?? "").Trim().Where(c => c is not ('<' or '>' or '"' or '\'' or '&')).Take(30).ToArray());

    static PassbookPreview BuildPreview(List<PassbookRow> rows, List<Trade> trades)
    {
        var warnings = new List<string>();
        var ignored = new Dictionary<string, int>();
        var groups = new Dictionary<string, (string Date, string Code, string Name, string Type, long Shares, int Rows)>();
        foreach (var r in rows)
        {
            var type = TradeType(r.Memo ?? "");
            if (type == null)
            {
                ignored[r.Memo ?? ""] = ignored.GetValueOrDefault(r.Memo ?? "") + 1;
                continue;
            }
            if (!DateTime.TryParseExact(r.Date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _)
                || !CodePattern.IsMatch(r.Code?.Trim() ?? "") || r.Shares <= 0 || r.Shares > 1_000_000_000)
            {
                warnings.Add($"略過無法辨識的列：{r.Date} {r.Code} {r.Memo} {r.Shares}");
                continue;
            }
            var key = Key(r.Date, r.Code.Trim(), type);
            groups.TryGetValue(key, out var g);
            groups[key] = (r.Date, r.Code.Trim(), g.Name ?? CleanName(r.Name), type, g.Shares + r.Shares, g.Rows + 1);
        }

        var existing = trades.GroupBy(t => Key(t.Date, t.StockCode, t.TradeType))
            .ToDictionary(g => g.Key, g => g.ToList());
        var names = trades.GroupBy(t => t.StockCode).ToDictionary(g => g.Key, g => g.Last().StockName);

        // 系統裡某日的股數比存摺多（或存摺根本沒有這天）→ 多出來的部分可能是日期填錯的同一筆
        var surplus = existing
            .Select(kv => (Trades: kv.Value, Extra: kv.Value.Sum(t => t.Shares) - (groups.TryGetValue(kv.Key, out var g) ? g.Shares : 0)))
            .Where(x => x.Extra > 0)
            .ToList();

        var items = new List<PassbookItem>();
        var matchedAccounts = new List<string>();
        foreach (var (key, g) in groups.OrderByDescending(kv => kv.Value.Date).ThenBy(kv => kv.Value.Code))
        {
            var mine = existing.GetValueOrDefault(key) ?? [];
            var have = mine.Sum(t => t.Shares);
            matchedAccounts.AddRange(mine.Select(t => t.AccountType));
            var remaining = g.Shares - have;
            var name = names.GetValueOrDefault(g.Code) ?? g.Name;

            string status, note = "";
            if (remaining <= 0)
            {
                status = "duplicate";
                note = remaining < 0 ? $"系統已有 {have:N0} 股，多於存摺" : $"系統已有 {mine.Count} 筆";
            }
            else if (have > 0)
            {
                status = "partial";
                note = $"系統已有 {have:N0} 股，補登差額";
            }
            else
            {
                var day = DateTime.Parse(g.Date);
                var near = surplus
                    .Where(s => s.Trades[0].StockCode == g.Code && s.Trades[0].TradeType == g.Type
                        && Math.Abs((DateTime.Parse(s.Trades[0].Date) - day).TotalDays) <= SuspectDays)
                    .ToList();
                status = near.Count > 0 ? "suspect" : "new";
                if (near.Count > 0)
                    note = "系統另有 " + string.Join("、", near.Select(s => $"{s.Trades[0].Date} {s.Extra:N0} 股")) + "（存摺無此筆），可能是同一筆日期不同"
                        + "；若有多筆疑似對到同一天，最多只會有一筆是同一筆";
            }
            items.Add(new PassbookItem(key, g.Date, g.Code, name, g.Type, g.Shares, have, Math.Max(0, remaining), status, null, note));
        }

        var account = matchedAccounts.Where(a => !string.IsNullOrWhiteSpace(a))
            .GroupBy(a => a).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "";
        return new PassbookPreview(items, ignored.Select(kv => new IgnoredMemo(kv.Key, kv.Value)).ToList(), warnings, account);
    }

    // 存摺沒有成交價，預設帶入當日收盤價（使用者可在預覽中修改）
    static async Task FillPrices(List<PassbookItem> items, IHttpClientFactory httpFactory)
    {
        var need = items.Where(i => i.Status != "duplicate" && i.TradeType != "配股").ToList();
        var client = httpFactory.CreateClient("finmind");
        var tasks = need.GroupBy(i => i.Code).Select(async g =>
        {
            var start = g.Min(i => i.Date)!;
            var end = g.Max(i => i.Date)!;
            var closes = new Dictionary<string, decimal>();
            try
            {
                var json = await client.GetStringAsync(
                    $"/api/v4/data?dataset=TaiwanStockPrice&data_id={Uri.EscapeDataString(g.Key)}&start_date={start}&end_date={end}");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var arr))
                    foreach (var d in arr.EnumerateArray())
                        if (d.TryGetProperty("date", out var de) && d.TryGetProperty("close", out var c) && c.ValueKind == JsonValueKind.Number)
                            closes[de.GetString()!] = c.GetDecimal();
            }
            catch { }
            return (g.Key, closes);
        });
        var prices = (await Task.WhenAll(tasks)).ToDictionary(x => x.Key, x => x.closes);
        for (int i = 0; i < items.Count; i++)
            if (prices.TryGetValue(items[i].Code, out var closes) && closes.TryGetValue(items[i].Date, out var close))
                items[i] = items[i] with { Price = close };
    }

    // ── 2. 套用勾選的項目 ─────────────────────────────────────────────────

    static async Task<IResult> Apply(PassbookApplyRequest req)
    {
        if (req.Rows == null || req.Keys == null) return Results.BadRequest("沒有要匯入的交易");
        if (string.IsNullOrWhiteSpace(req.AccountType)) return Results.BadRequest("請填寫帳戶");
        if (StocksModule.ExcelLocked()) return Results.BadRequest("請先關閉 Excel 檔案「股票總張數統計.xlsx」再匯入");
        var selected = req.Keys.ToHashSet();

        await StocksModule.Lock.WaitAsync();
        try
        {
            // 以寫入當下的交易紀錄重新比對，重複送出或別處剛新增過也不會重複匯入
            var trades = StocksModule.LoadTrades();
            var preview = BuildPreview(req.Rows, trades);
            var added = new List<Trade>();
            var skipped = 0;
            foreach (var item in preview.Items.Where(i => selected.Contains(i.Key)))
            {
                var price = req.Prices?.GetValueOrDefault(item.Key) ?? 0;
                if (item.Status == "duplicate" || item.ImportShares <= 0) { skipped++; continue; }
                if (item.TradeType == "配股" ? price < 0 : price <= 0)
                    return Results.BadRequest($"{item.Date} {item.Code} {item.Name} 請填寫成交價");
                added.Add(StocksModule.BuildTrade(Guid.NewGuid().ToString("N")[..8], item.Name, item.Code, item.Date,
                    item.ImportShares, price, item.TradeType, "", req.AccountType.Trim()));
            }
            if (added.Count > 0)
            {
                trades.AddRange(added);
                StocksModule.SaveTrades(trades);
                if (StocksModule._enableExcel)
                    foreach (var t in added) StocksModule.AppendTradeToExcel(t);
            }
            return Results.Json(new { imported = added.Count, skipped }, WebJson);
        }
        finally { StocksModule.Lock.Release(); }
    }
}

// 瀏覽器端已把民國日期轉成 yyyy-MM-dd
public record PassbookRow(string Date, string Code, string? Name, string? Memo, long Shares);

public record PassbookRequest(List<PassbookRow> Rows);

// status：new 新交易｜partial 系統已有部分股數，補差額｜suspect 疑似已登錄（日期不同）｜duplicate 已存在
public record PassbookItem(
    string Key, string Date, string Code, string Name, string TradeType,
    long PassbookShares, long ExistingShares, long ImportShares, string Status, decimal? Price, string Note);

public record IgnoredMemo(string Memo, int Count);

public record PassbookPreview(List<PassbookItem> Items, List<IgnoredMemo> Ignored, List<string> Warnings, string SuggestedAccount);

public record PassbookApplyRequest(List<PassbookRow> Rows, List<string> Keys, Dictionary<string, decimal> Prices, string AccountType);
