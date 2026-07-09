using ClosedXML.Excel;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class StocksModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string TradesFile = Path.Combine(DataDir, "trades.json");
    static readonly string DividendsCacheFile = Path.Combine(DataDir, "dividends_cache.json");
    static readonly string SnapshotsFile = Path.Combine(DataDir, "stock_snapshots.json");
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
        app.MapGet("/stocks/history/{id}/edit", EditTradePage);
        app.MapGet("/stocks/dividends", DividendsHistoryPage);
        app.MapGet("/stocks/snapshots", SnapshotsPage);
        app.MapPost("/api/stocks/trade", AddTrade);
        app.MapPut("/api/stocks/trade/{id}", UpdateTrade);
        app.MapGet("/api/stocks/prices", GetPrices);
        app.MapGet("/api/stocks/info", GetStockInfo);
        app.MapGet("/api/stocks/holdings", GetHoldings);
        app.MapGet("/api/stocks/dividends/history", GetDividendsHistory);
        app.MapPost("/api/stocks/snapshot", AddSnapshot);
        app.MapDelete("/api/stocks/snapshot/{id}", DeleteSnapshot);
    }

    static async Task InventoryPage(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildInventoryHtml());
    }

    static async Task TradePage(HttpContext ctx)
    {
        var trades = LoadTrades();
        var accountTypes = trades.Select(t => t.AccountType).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().Order().ToList();
        var knownStocks = trades.Select(t => new { t.StockCode, t.StockName }).DistinctBy(x => x.StockCode).ToList();
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildTradeHtml(accountTypes, knownStocks.Select(x => $"{x.StockCode} {x.StockName}").ToList()));
    }

    static async Task HistoryPage(HttpContext ctx)
    {
        var trades = LoadTrades();
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildHistoryHtml(trades));
    }

    static async Task EditTradePage(string id, HttpContext ctx)
    {
        var trade = LoadTrades().FirstOrDefault(t => t.Id == id);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        if (trade == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("交易紀錄不存在"); return; }
        var accountTypes = LoadTrades().Select(t => t.AccountType).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().Order().ToList();
        await ctx.Response.WriteAsync(BuildTradeEditHtml(trade, accountTypes));
    }

    static async Task DividendsHistoryPage(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildDividendsHistoryHtml());
    }

    static async Task SnapshotsPage(HttpContext ctx)
    {
        var snapshots = LoadSnapshots().OrderBy(s => s.Date).ToList();
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildSnapshotsHtml(snapshots));
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

            // FinMind（資料源自公開資訊觀測站）若抓不到最新年度或仍有缺漏年度，
            // 退而改抓 Goodinfo 台灣股市資訊網補齊。
            var stillMissing = pastYears.Where(y => !codeCache.ContainsKey(y)).ToList();
            // FinMind 有時會回一筆「已宣告但金額尚未填入」的當年度佔位資料（現金、股票皆為 0），
            // 這種空殼也視為缺漏，照樣去 Goodinfo 補。
            static bool IsEmpty(DivEntry? e) => e == null || (e.CashDiv == 0m && e.StockDiv == 0m);
            if (IsEmpty(currentEntry) || stillMissing.Count > 0)
            {
                var goodinfo = await FetchGoodinfoDividends(code, httpFactory);
                foreach (var (gy, gv) in goodinfo)
                {
                    if (gy < startYear || gy > currentYear) continue;
                    var entry = new DivEntry(gv.cash, gv.stock, "", "", "");
                    if (gy == currentYear)
                    {
                        if (IsEmpty(currentEntry)) currentEntry = entry;
                    }
                    else if (!codeCache.ContainsKey(gy))
                    {
                        codeCache[gy] = entry;
                        cacheUpdated = true;
                    }
                }
            }

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
        var trade = BuildTrade(Guid.NewGuid().ToString("N")[..8], req.StockName, req.StockCode, date,
            req.Shares, req.Price, req.TradeType, req.OrderNo ?? "", req.AccountType ?? "");

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
        }
        finally { Lock.Release(); }

        return Results.Ok(new { message = "交易紀錄已儲存", netAmount = trade.NetAmount });
    }

    static async Task<IResult> UpdateTrade(string id, EditTradeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.StockCode) || req.Price <= 0 || req.Shares <= 0)
            return Results.BadRequest("請填寫完整交易資料");

        await Lock.WaitAsync();
        try
        {
            var trades = LoadTrades();
            var idx = trades.FindIndex(t => t.Id == id);
            if (idx < 0) return Results.NotFound("交易紀錄不存在");

            var date = string.IsNullOrEmpty(req.Date) ? trades[idx].Date : req.Date;
            // 僅重算此筆交易紀錄（成交金額、手續費、交易稅、淨收付），不調整庫存
            trades[idx] = BuildTrade(id, req.StockName, req.StockCode, date,
                req.Shares, req.Price, req.TradeType, req.OrderNo ?? "", req.AccountType ?? "");
            SaveTrades(trades);
        }
        finally { Lock.Release(); }

        return Results.Ok(new { message = "交易紀錄已更新" });
    }

    // 依買賣別、股數、成交價計算成交金額、手續費、交易稅、淨收付，產生 Trade。
    static Trade BuildTrade(string id, string stockName, string stockCode, string date,
        long shares, decimal price, string tradeType, string orderNo, string accountType)
    {
        var isEtf = stockCode.StartsWith("00");
        var totalAmount = price * shares;
        var commission = Math.Max(20, (long)Math.Floor(totalAmount * 0.001425m));
        long tax = tradeType == "現賣" ? (long)Math.Floor(totalAmount * (isEtf ? 0.001m : 0.003m)) : 0;
        var netAmount = tradeType == "現買"
            ? -(long)(totalAmount + commission)
            : (long)(totalAmount - commission - tax);
        return new Trade(id, stockName, stockCode, date, shares, netAmount,
            tradeType, price, (long)totalAmount, commission, tax, orderNo, accountType);
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

    // 庫存改由交易紀錄即時推算（不再另外維護 holdings.json），確保庫存頁與交易紀錄一致
    static IResult GetHoldings()
    {
        var holdings = AggregateHoldings(LoadTrades())
            .Select(h => new Holding("", h.Code, h.Name, h.Shares, h.Cost))
            .ToList();
        return Results.Json(holdings);
    }

    // 依交易紀錄回推指定日期當下、各檔股票的持有張數與成本
    static List<(string Code, string Name, int Shares, long Cost)> ComputeHoldingsAsOf(string cutoffDate) =>
        AggregateHoldings(LoadTrades().Where(t => string.Compare(t.Date, cutoffDate, StringComparison.Ordinal) <= 0));

    // 股數以原始股數（而非張）累加，避免逐筆交易各自無條件捨去到「張」造成零股誤差累積消失
    static List<(string Code, string Name, int Shares, long Cost)> AggregateHoldings(IEnumerable<Trade> trades)
    {
        var agg = new Dictionary<string, (string Name, long Shares, long Cost)>();
        foreach (var t in trades.OrderBy(t => t.Date)) // 穩定排序：同一天交易維持原始建立順序
        {
            agg.TryGetValue(t.StockCode, out var cur);
            if (string.IsNullOrEmpty(cur.Name)) cur.Name = t.StockName;

            if (t.TradeType == "現買" || t.TradeType == "配股")
            {
                cur.Shares += t.Shares;
                cur.Cost += t.Cost;
            }
            else if (t.TradeType == "現賣" && cur.Shares > 0)
            {
                var costPerShare = (decimal)cur.Cost / cur.Shares;
                var newShares = cur.Shares - t.Shares;
                cur.Cost = newShares <= 0 ? 0 : cur.Cost - (long)Math.Round(costPerShare * t.Shares);
                cur.Shares = Math.Max(0, newShares);
            }
            agg[t.StockCode] = cur;
        }

        return agg.Where(kv => kv.Value.Shares > 0)
            .Select(kv => (Code: kv.Key, kv.Value.Name, Shares: (int)(kv.Value.Shares / 1000), kv.Value.Cost))
            .OrderBy(x => x.Code)
            .ToList();
    }

    // 取得指定股票在截止日（或之前最近交易日）的收盤價；查無歷史資料時，若截止日為今天則退而使用即時報價。
    static async Task<decimal?> GetPriceAsOf(string code, string cutoffDate, IHttpClientFactory httpFactory)
    {
        try
        {
            var client = httpFactory.CreateClient("finmind");
            var start = DateTime.Parse(cutoffDate).AddDays(-10).ToString("yyyy-MM-dd");
            var response = await client.GetStringAsync(
                $"/api/v4/data?dataset=TaiwanStockPrice&data_id={code}&start_date={start}&end_date={cutoffDate}");
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("data", out var arr))
            {
                string? bestDate = null;
                decimal? bestPrice = null;
                foreach (var item in arr.EnumerateArray())
                {
                    var d = item.TryGetProperty("date", out var de) ? de.GetString() : null;
                    if (string.IsNullOrEmpty(d) || string.Compare(d, cutoffDate, StringComparison.Ordinal) > 0) continue;
                    if (bestDate == null || string.Compare(d, bestDate, StringComparison.Ordinal) > 0)
                    {
                        bestDate = d;
                        bestPrice = item.TryGetProperty("close", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
                    }
                }
                if (bestPrice is > 0) return bestPrice;
            }
        }
        catch { }

        if (cutoffDate == DateTime.Today.ToString("yyyy-MM-dd"))
        {
            try
            {
                var client = httpFactory.CreateClient("twse");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://mis.twse.com.tw/");
                var response = await client.GetStringAsync($"/stock/api/getStockInfo.jsp?ex_ch=tse_{code}.tw&json=1&delay=0");
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("msgArray", out var arr) && arr.GetArrayLength() > 0)
                {
                    var item = arr[0];
                    var zStr = item.TryGetProperty("z", out var zEl) ? zEl.GetString() : null;
                    var yStr = item.TryGetProperty("y", out var yEl) ? yEl.GetString() : null;
                    var priceStr = (zStr != null && zStr != "-") ? zStr : yStr;
                    if (decimal.TryParse(priceStr, out var price) && price > 0) return price;
                }
            }
            catch { }
        }
        return null;
    }

    static async Task<IResult> AddSnapshot(BuildSnapshotRequest req, IHttpClientFactory httpFactory)
    {
        if (string.IsNullOrWhiteSpace(req.Date) || !DateTime.TryParse(req.Date, out _))
            return Results.BadRequest("請填寫正確的日期");

        var asOf = ComputeHoldingsAsOf(req.Date);
        var priceResults = await Task.WhenAll(asOf.Select(async h => (Holding: h, Price: await GetPriceAsOf(h.Code, req.Date, httpFactory))));

        var breakdown = new List<StockSnapshotHolding>();
        var estimatedCodes = new List<string>();
        int totalShares = 0;
        long totalCost = 0;
        decimal totalValue = 0;

        foreach (var (h, price) in priceResults)
        {
            decimal finalPrice;
            if (price is > 0) finalPrice = price.Value;
            else
            {
                // 查無歷史股價（如個股尚未上市或資料源缺漏）時，以持有成本均價估算市值，並標記於回應中
                finalPrice = h.Shares > 0 ? Math.Round((decimal)h.Cost / (h.Shares * 1000), 2) : 0;
                estimatedCodes.Add(h.Code);
            }
            var value = finalPrice * h.Shares * 1000;
            breakdown.Add(new StockSnapshotHolding(h.Code, h.Name, h.Shares, finalPrice, value, h.Cost));
            totalShares += h.Shares;
            totalCost += h.Cost;
            totalValue += value;
        }

        var snapshots = LoadSnapshots();
        // 同一天重複記錄則覆蓋，避免重複快照
        var existing = snapshots.FirstOrDefault(s => s.Date == req.Date);
        var snap = new StockSnapshot(existing?.Id ?? Guid.NewGuid().ToString("N")[..8], req.Date,
            totalShares, totalCost, totalValue, breakdown, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (existing != null) snapshots[snapshots.IndexOf(existing)] = snap;
        else snapshots.Add(snap);
        SaveSnapshots(snapshots);

        return Results.Ok(new { totalShares, totalCost, totalValue, holdings = breakdown, estimated = estimatedCodes });
    }

    static IResult DeleteSnapshot(string id)
    {
        var snapshots = LoadSnapshots();
        var target = snapshots.FirstOrDefault(s => s.Id == id);
        if (target == null) return Results.NotFound();
        snapshots.Remove(target);
        SaveSnapshots(snapshots);
        return Results.Ok();
    }

    // ── Data helpers ──────────────────────────────────────────────────────

    static List<Trade> LoadTrades()
    {
        if (!File.Exists(TradesFile)) return [];
        var json = File.ReadAllText(TradesFile);
        var trades = JsonSerializer.Deserialize<List<Trade>>(json, JsonOpts) ?? [];
        // 舊資料沒有 id：補上並寫回，編輯時才有穩定識別碼
        var changed = false;
        for (int i = 0; i < trades.Count; i++)
            if (string.IsNullOrEmpty(trades[i].Id))
            {
                trades[i] = trades[i] with { Id = Guid.NewGuid().ToString("N")[..8] };
                changed = true;
            }
        if (changed) SaveTrades(trades);
        return trades;
    }

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

    static List<StockSnapshot> LoadSnapshots()
    {
        if (!File.Exists(SnapshotsFile)) return [];
        return JsonSerializer.Deserialize<List<StockSnapshot>>(File.ReadAllText(SnapshotsFile), JsonOpts) ?? [];
    }

    static void SaveSnapshots(List<StockSnapshot> snapshots) =>
        File.WriteAllText(SnapshotsFile, JsonSerializer.Serialize(snapshots, JsonOpts));

    // 從 Goodinfo 台灣股市資訊網的股利政策頁抓年度配息，作為 FinMind 抓不到時的備援。
    // 回傳 { 發放年度: (現金股利合計, 股票股利合計) }
    static async Task<Dictionary<int, (decimal cash, decimal stock)>> FetchGoodinfoDividends(
        string code, IHttpClientFactory httpFactory)
    {
        var result = new Dictionary<int, (decimal, decimal)>();
        try
        {
            var client = httpFactory.CreateClient("goodinfo");
            var html = await client.GetStringAsync($"/tw/StockDividendPolicy.asp?STOCK_ID={code}");

            // 逐列解析：把每個 <tr> 內的標籤換成分隔符後取出純文字欄位。
            foreach (System.Text.RegularExpressions.Match row in
                System.Text.RegularExpressions.Regex.Matches(html, "<tr.*?</tr>",
                    System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var cells = System.Text.RegularExpressions.Regex
                    .Replace(row.Value, "<[^>]*>", "|")
                    .Replace("&nbsp;", " ")
                    .Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim())
                    .Where(c => c.Length > 0)
                    .ToArray();

                // 資料列格式：發放年度 | 所屬年度 | 現金-盈餘 | 現金-公積 | 現金合計 | 股票-盈餘 | 股票-公積 | 股票合計 | …
                if (cells.Length < 8) continue;
                if (!int.TryParse(cells[0], out var year) || year < 2000 || year > 2099) continue;
                if (!int.TryParse(cells[1], out var belongYear) || belongYear < 1990 || belongYear > 2099) continue;

                var cash = decimal.TryParse(cells[4], out var c) ? c : 0m;
                var stock = decimal.TryParse(cells[7], out var s) ? s : 0m;
                if (cash == 0m && stock == 0m) continue;

                // 同一發放年度若出現多列（季配/半年配）則累計
                result.TryGetValue(year, out var prev);
                result[year] = (prev.Item1 + cash, prev.Item2 + stock);
            }
        }
        catch { }
        return result;
    }

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
</style><script>{SharedLayout.TableJs}{SharedLayout.ExportJs}</script></head><body>
{NavBar("stocks")}
<div class='actions' style='margin-bottom:8px'>
  <h1 style='margin:0;flex:1'>📈 股票庫存統計 <span class='refresh' id='ts'></span></h1>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('inv-table','股票庫存',{{btn:this}})"">⬇ 下載Excel</button>
</div>
<div class='summary' id='summary'>
  <div class='card'><div class='label'>總成本</div><div class='value' id='s-cost'>載入中…</div></div>
  <div class='card'><div class='label'>現值</div><div class='value' id='s-value'>—</div></div>
  <div class='card'><div class='label'>損益</div><div class='value' id='s-pnl'>—</div></div>
  <div class='card'><div class='label'>損益率</div><div class='value' id='s-pct'>—</div></div>
</div>
<div class='table-wrap'>
<table id='inv-table'>
<thead><tr>
  <th>股號</th><th>股名</th>
  <th>張數</th><th>成本(元)</th><th>成本均價</th>
  <th>即時股價</th><th>現值(元)</th><th>損益(元)</th><th>損益率</th>
</tr></thead>
<tbody id='tbody'><tr><td colspan='9' style='text-align:center;padding:20px;color:#999'>載入中…</td></tr></tbody>
<tfoot><tr>
  <td colspan='3'>合計</td>
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

  // 不分帳戶，依股票代號加總張數與成本
  const agg = {{}};
  for (const h of holdings) {{
    if (!agg[h.code]) agg[h.code] = {{ code: h.code, name: h.name, shares: 0, cost: 0 }};
    agg[h.code].shares += h.shares;
    agg[h.code].cost += h.cost;
  }}
  const list = Object.values(agg).sort((a, b) => a.code.localeCompare(b.code));

  for (const h of list) {{
    const costPerShare = h.shares > 0 ? h.cost / (h.shares * 1000) : 0;
    const price = prices[h.code];
    const value = price ? price * h.shares * 1000 : null;
    const pnl = value != null ? value - h.cost : null;
    const pct = pnl != null && h.cost > 0 ? pnl / h.cost * 100 : null;
    totCost += h.cost;
    if (value != null) totValue += value;

    rows += `<tr data-cost='${{h.cost}}' data-value='${{value || 0}}'>
      <td>${{h.code}}</td><td>${{h.name}}</td>
      <td>${{h.shares}}</td><td>${{fmt(h.cost)}}</td>
      <td>${{costPerShare.toFixed(2)}}</td>
      <td>${{price != null ? price.toFixed(2) : '<span class=loading>—</span>'}}</td>
      <td>${{value != null ? fmt(value) : '<span class=loading>—</span>'}}</td>
      <td class='${{pnl != null ? cls(pnl) : ''}}'>${{pnl != null ? fmt(pnl) : '—'}}</td>
      <td class='${{pct != null ? cls(pct) : ''}}'>${{pct != null ? fmtP(pct) : '—'}}</td>
    </tr>`;
  }}

  tbody.innerHTML = rows || '<tr><td colspan=9 style=text-align:center>尚無庫存</td></tr>';

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

let _invReapply = null;
function updateInvTotals(vis) {{
  let totCost = 0, totValue = 0, hasVal = false;
  for (const r of vis) {{
    totCost += +r.dataset.cost || 0;
    const v = +r.dataset.value || 0;
    if (v > 0) {{ totValue += v; hasVal = true; }}
  }}
  document.getElementById('f-cost').textContent = fmt(totCost);
  document.getElementById('f-value').textContent = hasVal ? fmt(totValue) : '—';
  const pnl = totValue - totCost;
  const pct = totCost > 0 ? pnl / totCost * 100 : 0;
  document.getElementById('f-pnl').textContent = hasVal ? fmt(pnl) : '—';
  document.getElementById('f-pct').textContent = hasVal ? fmtP(pct) : '—';
  if (hasVal) {{
    document.getElementById('f-pnl').className = cls(pnl);
    document.getElementById('f-pct').className = cls(pnl);
  }}
  document.getElementById('s-cost').textContent = fmt(totCost);
  if (hasVal) {{
    document.getElementById('s-value').textContent = fmt(totValue);
    document.getElementById('s-pnl').textContent = (pnl >= 0 ? '+' : '') + fmt(pnl);
    document.getElementById('s-pnl').className = 'value ' + cls(pnl);
    document.getElementById('s-pct').textContent = fmtP(pct);
    document.getElementById('s-pct').className = 'value ' + cls(pnl);
  }}
}}

load().then(() => {{
  const inv = initTable('inv-table', {{ cols: 9, onFilter: updateInvTotals }});
  if (inv) _invReapply = inv.run;
}});
setInterval(() => load().then(() => {{ if (_invReapply) _invReapply(); }}), 60000);
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

    static string BuildTradeEditHtml(Trade t, List<string> accountTypes)
    {
        string V(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        string Sel(string type) => t.TradeType == type ? " selected" : "";
        var acOptions = string.Join("", accountTypes.Select(a => $"<option value='{V(a)}'>"));
        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>編輯交易</title>
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
.btn-submit{{width:100%;padding:11px;background:#0066cc;color:#fff;border:none;border-radius:6px;font-size:1rem;cursor:pointer;font-weight:500}}
.btn-submit:hover{{background:#0055aa}}
.alert{{padding:10px 14px;border-radius:6px;margin-bottom:16px;font-size:.9rem}}
.alert.ok{{background:#e6f4ea;color:#1e7e34;border:1px solid #b8dfc0}}
.alert.err{{background:#fde8e8;color:#c0392b;border:1px solid #f5b8b8}}
.note{{font-size:.8rem;color:#999;margin-bottom:16px;line-height:1.5}}
</style></head><body>
{NavBar("history")}
<div style='margin-bottom:8px'>
  <a href='/stocks/history' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回交易紀錄</a>
</div>
<h1>✏️ 編輯交易</h1>
<div class='form-card'>
<div id='msg'></div>
<p class='note'>※ 僅更新此筆交易紀錄（成交金額、手續費、交易稅、淨收付會自動重算），不會調整股票庫存。</p>
<div class='field'>
  <label>買賣別</label>
  <select id='tradeType' onchange='calc()'>
    <option value='現買'{Sel("現買")}>現買</option>
    <option value='現賣'{Sel("現賣")}>現賣</option>
    <option value='配股'{Sel("配股")}>配股</option>
  </select>
</div>
<div class='row2'>
  <div class='field'><label>股號</label><input id='code' value='{V(t.StockCode)}' oninput='calc()'></div>
  <div class='field'><label>股名</label><input id='name' value='{V(t.StockName)}'></div>
</div>
<div class='row2'>
  <div class='field'><label>成交股數</label><input type='number' id='shares' value='{t.Shares}' min='1' oninput='calc()'></div>
  <div class='field'><label>成交價</label><input type='number' id='price' value='{t.Price}' step='0.01' min='0' oninput='calc()'></div>
</div>
<div class='row2'>
  <div class='field'>
    <label>帳戶</label>
    <input id='accountType' list='acList' value='{V(t.AccountType)}' placeholder='如: 已質借元大'>
    <datalist id='acList'>{acOptions}</datalist>
  </div>
  <div class='field'><label>日期</label><input type='date' id='date' value='{V(t.Date)}'></div>
</div>
<div class='field'><label>委託書號 (選填)</label><input id='orderNo' value='{V(t.OrderNo)}'></div>
<div class='calc-box' id='calcBox'>
  <div><span>成交金額</span><span id='c-amount'>—</span></div>
  <div><span>手續費 (0.1425%)</span><span id='c-fee'>—</span></div>
  <div id='c-tax-row'><span>交易稅</span><span id='c-tax'>—</span></div>
  <div class='total'><span id='c-dir'>應付金額</span><span id='c-net'>—</span></div>
</div>
<button class='btn-submit' onclick='submit()'>儲存變更</button>
</div>
<script>
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
calc();

async function submit() {{
  const req = {{
    stockCode: document.getElementById('code').value.trim(),
    stockName: document.getElementById('name').value.trim(),
    tradeType: document.getElementById('tradeType').value,
    price: parseFloat(document.getElementById('price').value),
    shares: parseInt(document.getElementById('shares').value),
    date: document.getElementById('date').value,
    orderNo: document.getElementById('orderNo').value.trim(),
    accountType: document.getElementById('accountType').value.trim()
  }};
  if (!req.stockCode || !req.shares || !req.price) {{
    showMsg('請填寫股號、股數及成交價', 'err'); return;
  }}
  try {{
    const res = await fetch('/api/stocks/trade/{t.Id}', {{
      method: 'PUT', headers: {{'Content-Type': 'application/json'}},
      body: JSON.stringify(req)
    }});
    if (res.ok) {{
      showMsg('✓ 已更新！', 'ok');
      setTimeout(() => location.href = '/stocks/history', 1000);
    }} else {{ const t = await res.text(); showMsg(t || '更新失敗', 'err'); }}
  }} catch(e) {{ showMsg('網路錯誤', 'err'); }}
}}

function showMsg(msg, type) {{
  document.getElementById('msg').innerHTML = `<div class='alert ${{type}}'>${{msg}}</div>`;
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
        var exportHeaders = string.Join(",", new[] { "股號", "股名", "帳戶" }.Concat(years.SelectMany(y =>
            y == currentYear
                ? new[] { $"{y}年-現金(元)", $"{y}年-股票(股)", $"{y}年-除息交易日", $"{y}年-除權交易日", $"{y}年-現金股利發放日", $"{y}年-股票股利發放日" }
                : new[] { $"{y}年-現金(元)", $"{y}年-股票(股)" }))
            .Select(h => $"'{h}'"));

        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>歷史配息</title>
<style>{CommonCss}
body{{max-width:1800px}}
th{{white-space:nowrap}}
thead tr:first-child th{{text-align:center}}
thead tr:first-child th:first-child{{text-align:left}}
td.year-cash{{text-align:right;color:#d00}}
td.year-stock{{text-align:right;color:#080}}
td.empty{{color:#ccc;text-align:right}}
td.date-col{{text-align:center;font-size:.82rem;color:#555}}
th.cur-year{{background:#fffbe6;color:#7a5500}}
td.cur-year{{background:#fffef5}}
</style><script>{SharedLayout.TableJs}{SharedLayout.ExportJs}</script></head><body>
{NavBar("dividends")}
<div class='actions' style='margin-bottom:8px'>
  <h1 style='margin:0;flex:1'>💰 歷史配息紀錄（近五年）</h1>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('div-table','歷史配息',{{headers:[{exportHeaders}],btn:this}})"">⬇ 下載Excel</button>
</div>
<div class='table-wrap'>
<table id='div-table'>
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
<p style='font-size:.8rem;color:#999;margin-top:12px'>資料來源：FinMind（公開資訊觀測站）；最新年度若尚未更新則改抓 Goodinfo 台灣股市資訊網。季配息或半年配自動累計為年度總額。當年度尚無資料時顯示空白。</p>
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

let _divReapply = null;
load().then(() => {{
  const noFilt = [];
  for (let i = 3; i < {colCount}; i++) noFilt.push(i);
  const dv = initTable('div-table', {{ cols: {colCount}, noFilter: noFilt }});
  if (dv) _divReapply = dv.run;
}});
</script>
</body></html>";
    }

    // 依起訖快照計算期間報酬率與年化報酬率（採簡單市值成長率，未計入期間內資金進出）
    static (double periodPct, double annualPct)? CalcReturn(StockSnapshot prev, StockSnapshot cur)
    {
        if (prev.TotalValue <= 0) return null;
        var days = (DateTime.Parse(cur.Date) - DateTime.Parse(prev.Date)).TotalDays;
        if (days <= 0) return null;
        var ratio = (double)(cur.TotalValue / prev.TotalValue);
        var periodPct = (ratio - 1) * 100;
        var annualPct = (Math.Pow(ratio, 365.0 / days) - 1) * 100;
        return (periodPct, annualPct);
    }

    static string PeriodTable(List<StockSnapshot> series, string periodLabel)
    {
        if (series.Count == 0)
            return "<p style='color:#999'>尚無足夠快照資料</p>";

        var rows = new List<string>();
        for (int i = 0; i < series.Count; i++)
        {
            var cur = series[i];
            var changeCell = "—";
            var annualCell = "—";
            if (i > 0)
            {
                var r = CalcReturn(series[i - 1], cur);
                if (r != null)
                {
                    var cls = r.Value.periodPct >= 0 ? "pos" : "neg";
                    changeCell = $"<span class='{cls}'>{(r.Value.periodPct >= 0 ? "+" : "")}{r.Value.periodPct:F2}%</span>";
                    annualCell = $"<span class='{cls}'>{(r.Value.annualPct >= 0 ? "+" : "")}{r.Value.annualPct:F2}%</span>";
                }
            }
            rows.Add($@"<tr>
  <td>{cur.Date}</td>
  <td>{cur.TotalShares:N0}</td>
  <td>{cur.TotalCost:N0}</td>
  <td>{cur.TotalValue:N0}</td>
  <td>{changeCell}</td>
  <td>{annualCell}</td>
</tr>");
        }

        return $@"<div class='table-wrap'><table>
<thead><tr><th>{periodLabel}</th><th>張數</th><th>成本(元)</th><th>市值(元)</th><th>期間報酬率</th><th>年化報酬率</th></tr></thead>
<tbody>{string.Join("", rows)}</tbody>
</table></div>";
    }

    static string BuildSnapshotsHtml(List<StockSnapshot> snapshots)
    {
        var todayFirst = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyy-MM-dd");

        var monthly = snapshots
            .GroupBy(s => s.Date.Length >= 7 ? s.Date[..7] : s.Date)
            .Select(g => g.OrderBy(s => s.Date).First())
            .OrderBy(s => s.Date).ToList();
        var quarterly = snapshots
            .GroupBy(s => { var d = DateTime.Parse(s.Date); return $"{d.Year}-Q{(d.Month - 1) / 3 + 1}"; })
            .Select(g => g.OrderBy(s => s.Date).First())
            .OrderBy(s => s.Date).ToList();
        var yearly = snapshots
            .GroupBy(s => DateTime.Parse(s.Date).Year)
            .Select(g => g.OrderBy(s => s.Date).First())
            .OrderBy(s => s.Date).ToList();

        string summaryCards;
        if (snapshots.Count >= 2)
        {
            var first = snapshots.First();
            var last = snapshots.Last();
            var r = CalcReturn(first, last);
            if (r != null)
            {
                var cls = r.Value.periodPct >= 0 ? "pos" : "neg";
                summaryCards = $@"
<div class='card'><div class='label'>統計區間</div><div class='value' style='font-size:1rem'>{first.Date} ~ {last.Date}</div></div>
<div class='card'><div class='label'>總報酬率</div><div class='value {cls}'>{(r.Value.periodPct >= 0 ? "+" : "")}{r.Value.periodPct:F2}%</div></div>
<div class='card'><div class='label'>年化報酬率 (CAGR)</div><div class='value {cls}'>{(r.Value.annualPct >= 0 ? "+" : "")}{r.Value.annualPct:F2}%</div></div>";
            }
            else
                summaryCards = "<div class='card'><div class='label'>年化報酬率</div><div class='value'>資料不足</div></div>";
        }
        else
            summaryCards = "<div class='card'><div class='label'>年化報酬率</div><div class='value' style='font-size:1rem'>至少需 2 筆快照才能計算</div></div>";

        var rawRows = snapshots.Count == 0
            ? "<tr><td colspan='6' style='text-align:center;padding:20px;color:#999'>尚無快照紀錄</td></tr>"
            : string.Join("", snapshots.OrderByDescending(s => s.Date).Select(s =>
            {
                var pnlPct = s.TotalCost > 0 ? (double)(s.TotalValue - s.TotalCost) / s.TotalCost * 100 : (double?)null;
                var pnlCell = pnlPct == null ? "—" : $"<span class='{(pnlPct >= 0 ? "pos" : "neg")}'>{(pnlPct >= 0 ? "+" : "")}{pnlPct:F2}%</span>";
                var holdings = s.Holdings ?? [];
                var detailRows = holdings.Count == 0
                    ? "<tr><td colspan='6' style='text-align:center;color:#999'>無個股明細</td></tr>"
                    : string.Join("", holdings.OrderBy(h => h.Code).Select(h => $@"<tr>
  <td>{System.Net.WebUtility.HtmlEncode(h.Code)}</td><td>{System.Net.WebUtility.HtmlEncode(h.Name)}</td>
  <td>{h.Shares:N0}</td><td>{h.Price:N2}</td><td>{h.Value:N0}</td><td>{h.Cost:N0}</td>
</tr>"));
                return $@"<tr>
  <td>{s.Date}</td><td>{s.TotalShares:N0}</td><td>{s.TotalCost:N0}</td><td>{s.TotalValue:N0}</td>
  <td>{pnlCell}</td>
  <td><div class='actions'>
    <button class='btn btn-outline btn-sm' onclick='toggleDetail(""{s.Id}"")'>明細</button>
    <button class='btn btn-danger btn-sm' onclick='delSnap(""{s.Id}"")'>刪除</button>
  </div></td>
</tr>
<tr id='detail-{s.Id}' style='display:none'>
  <td colspan='6' style='padding:0 0 12px 24px;background:#fafbfc'>
  <table style='width:100%'>
  <thead><tr><th>股號</th><th>股名</th><th>張數</th><th>單價</th><th>市值(元)</th><th>成本(元)</th></tr></thead>
  <tbody>{detailRows}</tbody>
  </table>
  </td>
</tr>";
            }));

        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>市值快照</title>
<style>{CommonCss}
.form-card{{background:#fff;border-radius:8px;padding:20px;box-shadow:0 1px 4px rgba(0,0,0,.08);max-width:680px;margin-bottom:20px}}
.field{{margin-bottom:14px}}
label{{display:block;font-size:.85rem;color:#555;margin-bottom:4px;font-weight:500}}
input{{width:100%;padding:9px 12px;border:1px solid #ddd;border-radius:6px;font-size:.95rem;box-sizing:border-box}}
.alert{{padding:10px 14px;border-radius:6px;margin-bottom:16px;font-size:.9rem}}
.alert.ok{{background:#e6f4ea;color:#1e7e34;border:1px solid #b8dfc0}}
.alert.err{{background:#fde8e8;color:#c0392b;border:1px solid #f5b8b8}}
.section{{background:#fff;border-radius:8px;padding:16px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08);margin-bottom:20px}}
</style></head><body>
{NavBar("snapshots")}
<h1 style='margin:0 0 16px'>📅 股票市值快照</h1>
<div class='summary' id='summary'>{summaryCards}</div>

<div class='form-card'>
<h3 style='margin-top:0'>新增 / 更新快照</h3>
<div id='msg'></div>
<div class='field'><label>日期（一般為月初或年初，如 {todayFirst}）</label><input type='date' id='snapDate' value='{todayFirst}'></div>
<div class='actions'>
  <button class='btn' id='buildBtn' onclick='buildSnap()' type='button'>依交易紀錄計算並儲存</button>
</div>
<p style='font-size:.78rem;color:#999;margin-top:8px'>系統會依交易紀錄回推該日期當下各檔股票的持有張數，並查詢當日（或最近交易日）收盤價計算市值。同一天已有紀錄時，再次儲存會覆蓋原本的快照。年化報酬率採市值成長率簡化計算，未扣除期間內買賣資金進出的影響。<br>※ 若某檔股票在加入「交易紀錄」功能前就已持有，回推張數會少算未登錄的部分（可對照「庫存」頁確認）；只要之後的買賣都透過「新增交易」登錄，回推的準確度會逐步提升。</p>
</div>

<div class='section'>
  <h2 style='margin-top:0'>月度比較</h2>
  {PeriodTable(monthly, "月份")}
</div>
<div class='section'>
  <h2 style='margin-top:0'>季度比較</h2>
  {PeriodTable(quarterly, "季度")}
</div>
<div class='section'>
  <h2 style='margin-top:0'>年度比較</h2>
  {PeriodTable(yearly, "年度")}
</div>

<div class='section'>
  <h2 style='margin-top:0'>所有快照紀錄</h2>
  <div class='table-wrap'>
  <table>
  <thead><tr><th>日期</th><th>張數</th><th>成本(元)</th><th>市值(元)</th><th>損益率</th><th></th></tr></thead>
  <tbody>{rawRows}</tbody>
  </table>
  </div>
</div>

<script>
async function buildSnap() {{
  const date = document.getElementById('snapDate').value;
  if (!date) {{ showMsg('請選擇日期', 'err'); return; }}
  const btn = document.getElementById('buildBtn');
  const oldText = btn.textContent;
  btn.disabled = true; btn.textContent = '計算中…（依股票數量可能需數秒）';
  try {{
    const r = await fetch('/api/stocks/snapshot', {{method:'POST', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify({{ date }})}});
    if (!r.ok) {{ const t = await r.text(); showMsg(t || '儲存失敗', 'err'); return; }}
    const data = await r.json();
    if (data.estimated && data.estimated.length) {{
      showMsg('已儲存，但以下股票查無歷史股價，暫以持有成本估算市值：' + data.estimated.join('、'), 'ok');
      setTimeout(() => location.reload(), 2500);
    }} else {{
      location.reload();
    }}
  }} catch(e) {{
    showMsg('計算失敗，請確認網路連線', 'err');
  }} finally {{
    btn.disabled = false; btn.textContent = oldText;
  }}
}}

function toggleDetail(id) {{
  const el = document.getElementById('detail-' + id);
  if (el) el.style.display = el.style.display === 'none' ? '' : 'none';
}}

async function delSnap(id) {{
  if (!confirm('確定刪除此快照？')) return;
  const r = await fetch('/api/stocks/snapshot/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload();
  else showMsg('刪除失敗', 'err');
}}

function showMsg(m, t) {{ document.getElementById('msg').innerHTML = `<div class='alert ${{t}}'>${{m}}</div>`; }}
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
<td class='{netCls}'>{t.NetAmount:N0}</td><td>{t.OrderNo}</td><td>{t.AccountType}</td>
<td><a href='/stocks/history/{t.Id}/edit' class='btn btn-sm btn-outline'>編輯</a></td></tr>";
        });

        var totCost = sorted.Sum(t => t.Cost);
        var totFee = sorted.Sum(t => t.Commission);
        var totTax = sorted.Sum(t => t.Tax);
        var totNet = sorted.Sum(t => t.NetAmount);

        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>交易紀錄</title>
<style>{CommonCss}
th,td{{white-space:nowrap}}
th:nth-child(n+5),td:nth-child(n+5){{text-align:right}}
th:nth-child(1),th:nth-child(2),th:nth-child(3),th:nth-child(4){{text-align:left}}
td:nth-child(1),td:nth-child(2),td:nth-child(3),td:nth-child(4){{text-align:left}}
tfoot td{{text-align:right}}
tfoot td:first-child{{text-align:left}}
</style><script>{SharedLayout.TableJs}{SharedLayout.ExportJs}</script></head><body>
{NavBar("history")}
<div class='actions' style='margin-bottom:8px'>
  <h1 style='margin:0;flex:1'>📋 交易紀錄 ({sorted.Count} 筆)</h1>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('hist-table','交易紀錄',{{btn:this,skipCols:[12]}})"">⬇ 下載Excel</button>
</div>
<div class='table-wrap'>
<table id='hist-table'>
<thead><tr>
<th>日期</th><th>股號</th><th>股名</th><th>買賣別</th>
<th>股數</th><th>成交價</th><th>成交金額</th><th>手續費</th><th>交易稅</th>
<th>淨收付</th><th>委託書號</th><th>帳戶</th><th>操作</th>
</tr></thead>
<tbody>{string.Join("", rows)}</tbody>
<tfoot><tr>
  <td>合計</td><td></td><td></td><td></td>
  <td id='ht-shares'>{sorted.Sum(t => t.Shares):N0}</td><td></td>
  <td id='ht-cost'>{totCost:N0}</td>
  <td id='ht-fee'>{totFee:N0}</td>
  <td id='ht-tax'>{totTax:N0}</td>
  <td id='ht-net'>{totNet:N0}</td><td></td><td></td><td></td>
</tr></tfoot>
</table>
</div>
<script>
initTable('hist-table', {{
  cols: 13,
  noFilter: [10, 12],
  sumCols: [
    {{col: 4, id: 'ht-shares'}},
    {{col: 6, id: 'ht-cost'}},
    {{col: 7, id: 'ht-fee'}},
    {{col: 8, id: 'ht-tax'}},
    {{col: 9, id: 'ht-net'}}
  ]
}});
</script>
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
    [property: JsonPropertyName("id")] string Id,
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
    [property: JsonPropertyName("orderNo")] string OrderNo,
    [property: JsonPropertyName("accountType")] string AccountType = "");

public record NewTradeRequest(
    string StockCode,
    string StockName,
    string AccountType,
    string TradeType,
    decimal Price,
    long Shares,
    string? Date,
    string? OrderNo);

public record StockSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("totalShares")] int TotalShares,
    [property: JsonPropertyName("totalCost")] long TotalCost,
    [property: JsonPropertyName("totalValue")] decimal TotalValue,
    [property: JsonPropertyName("holdings")] List<StockSnapshotHolding> Holdings,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

public record StockSnapshotHolding(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("shares")] int Shares,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("value")] decimal Value,
    [property: JsonPropertyName("cost")] long Cost);

public record BuildSnapshotRequest(string Date);

public record EditTradeRequest(
    string StockCode,
    string StockName,
    string TradeType,
    decimal Price,
    long Shares,
    string? Date,
    string? OrderNo,
    string? AccountType);
