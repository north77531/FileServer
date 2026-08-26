using ClosedXML.Excel;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class StocksModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string TradesFile = Path.Combine(DataDir, "trades.json");
    static readonly string DividendsCacheFile = Path.Combine(DataDir, "dividends_cache.json");
    static readonly string DividendsManualFile = Path.Combine(DataDir, "dividends_manual.json");
    static readonly string SnapshotsFile = Path.Combine(DataDir, "stock_snapshots.json");
    static readonly string WatchlistFile = Path.Combine(DataDir, "watchlist.json");
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
        app.MapGet("/stocks/backtest", BacktestPage);
        app.MapGet("/stocks/watchlist", WatchlistPage);
        app.MapGet("/stocks/batch", BatchPage);
        app.MapPost("/api/stocks/trade", AddTrade);
        app.MapPut("/api/stocks/trade/{id}", UpdateTrade);
        app.MapGet("/api/stocks/prices", GetPrices);
        app.MapGet("/api/stocks/backtest", RunBacktest);
        app.MapGet("/api/stocks/watchlist", GetWatchlist);
        app.MapPost("/api/stocks/watchlist", AddWatch);
        app.MapDelete("/api/stocks/watchlist/{id}", DeleteWatch);
        app.MapGet("/api/stocks/signals", GetSignals);
        app.MapGet("/api/stocks/batch", RunBatch);
        app.MapGet("/api/stocks/info", GetStockInfo);
        app.MapGet("/api/stocks/holdings", GetHoldings);
        app.MapGet("/api/stocks/dividends/history", GetDividendsHistory);
        app.MapGet("/api/stocks/dividends/manual", GetDividendsManual);
        app.MapPost("/api/stocks/dividends/manual", SaveDividendManual);
        app.MapDelete("/api/stocks/dividends/manual", DeleteDividendManual);
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

        // 載入快取 { code: { year: DivEntry } } 與手動維護資料 { code: { year: DivEntry } }
        var cache = LoadDividendsCache();
        var manual = LoadDividendsManual();
        var cacheUpdated = false;
        var result = new Dictionary<string, Dictionary<string, DivEntry>>();

        foreach (var code in codeList)
        {
            // 從快取取出過去年度
            cache.TryGetValue(code, out var codeCache);
            codeCache ??= [];
            // 手動維護的年度視為「已經有值」，不需即時抓取
            manual.TryGetValue(code, out var codeManual);
            codeManual ??= [];
            var missingPastYears = pastYears.Where(y => !codeCache.ContainsKey(y) && !codeManual.ContainsKey(y.ToString())).ToList();
            var currentInManual = codeManual.ContainsKey(currentYear.ToString());
            // 只有在仍有缺漏年度、或今年尚未手動維護時，才需要對外抓取
            var needFetch = missingPastYears.Count > 0 || !currentInManual;

            // 決定抓取起始日：有缺漏的過去年度 → 從最早缺漏年，否則只抓今年
            var fetchStart = missingPastYears.Count > 0
                ? $"{missingPastYears.Min()}-01-01"
                : $"{currentYear}-01-01";

            DivEntry? currentEntry = null;
            if (needFetch)
            try
            {
                var response = await client.GetStringAsync(
                    $"/api/v4/data?dataset=TaiwanStockDividend&data_id={code}&start_date={fetchStart}");
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("data", out var dataArr))
                {
                    var byYear = new Dictionary<int, (decimal cash, decimal stock)>();
                    var lastDates = new Dictionary<int, (string cashEx, string stockEx, string cashPay, string stockPay)>();

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
                        var stockPay = item.TryGetProperty("StockDividendPaymentDate", out var sp) ? sp.GetString() ?? "" : "";
                        lastDates[y] = (cashEx, stockEx, cashPay, stockPay);
                    }

                    foreach (var kv in byYear)
                    {
                        lastDates.TryGetValue(kv.Key, out var dates);
                        var entry = new DivEntry(kv.Value.cash, kv.Value.stock, dates.cashEx ?? "", dates.stockEx ?? "", dates.cashPay ?? "", dates.stockPay ?? "");
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
            var stillMissing = pastYears.Where(y => !codeCache.ContainsKey(y) && !codeManual.ContainsKey(y.ToString())).ToList();
            // FinMind 有時會回一筆「已宣告但金額尚未填入」的當年度佔位資料（現金、股票皆為 0），
            // 這種空殼也視為缺漏，照樣去 Goodinfo 補。
            static bool IsEmpty(DivEntry? e) => e == null || (e.CashDiv == 0m && e.StockDiv == 0m);
            if ((IsEmpty(currentEntry) && !currentInManual) || stillMissing.Count > 0)
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

            // 手動維護資料優先覆蓋（自動抓取結果之上）
            foreach (var kv in codeManual)
                if (int.TryParse(kv.Key, out var my) && my >= startYear && my <= currentYear)
                    merged[kv.Key] = kv.Value;

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

    // 手動維護的配息資料：{ 股號: { 年度: DivEntry } }
    static Dictionary<string, Dictionary<string, DivEntry>> LoadDividendsManual()
    {
        if (!File.Exists(DividendsManualFile)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DivEntry>>>(File.ReadAllText(DividendsManualFile), JsonOpts) ?? []; }
        catch { return []; }
    }

    static void SaveDividendsManual(Dictionary<string, Dictionary<string, DivEntry>> manual)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(DividendsManualFile, JsonSerializer.Serialize(manual, JsonOpts));
    }

    static IResult GetDividendsManual(string? codes)
    {
        var manual = LoadDividendsManual();
        if (string.IsNullOrWhiteSpace(codes)) return Results.Json(manual);
        var wanted = codes.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return Results.Json(manual.Where(kv => wanted.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    static IResult SaveDividendManual(DividendManualRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code) || req.Year < 2000 || req.Year > 2100)
            return Results.BadRequest("缺少股號或年度");
        var manual = LoadDividendsManual();
        if (!manual.TryGetValue(req.Code, out var byYear)) { byYear = []; manual[req.Code] = byYear; }
        byYear[req.Year.ToString()] = new DivEntry(req.CashDiv, req.StockDiv,
            req.CashExDate ?? "", req.StockExDate ?? "", req.CashPayDate ?? "", req.StockPayDate ?? "");
        SaveDividendsManual(manual);
        return Results.Ok();
    }

    static IResult DeleteDividendManual(HttpContext ctx)
    {
        var code = ctx.Request.Query["code"].ToString();
        var year = ctx.Request.Query["year"].ToString();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(year)) return Results.BadRequest("缺少股號或年度");
        var manual = LoadDividendsManual();
        if (manual.TryGetValue(code, out var byYear))
        {
            byYear.Remove(year);
            if (byYear.Count == 0) manual.Remove(code);
            SaveDividendsManual(manual);
        }
        return Results.Ok();
    }

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
td.year-stock{{color:#080}}
</style><script>{SharedLayout.TableJs}{SharedLayout.ExportJs}{SharedLayout.ColumnChooserJs}</script></head><body>
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
  <th>今年預計配息(元)</th><th>今年預計配股(股)</th>
</tr></thead>
<tbody id='tbody'><tr><td colspan='11' style='text-align:center;padding:20px;color:#999'>載入中…</td></tr></tbody>
<tfoot><tr>
  <td>合計</td><td></td><td></td>
  <td id='f-cost'>—</td><td>—</td><td>—</td>
  <td id='f-value'>—</td><td id='f-pnl'>—</td><td id='f-pct'>—</td>
  <td id='f-div'>—</td><td id='f-divstock'>—</td>
</tr></tfoot>
</table>
</div>
<script>
const fmt = n => Math.round(n).toLocaleString('zh-TW');
const fmtP = n => (n >= 0 ? '+' : '') + n.toFixed(2) + '%';
const cls = n => n >= 0 ? 'pos' : 'neg';
const curYear = new Date().getFullYear();

// 今年預計配息（元/股）與股票股利（元），來源同「歷史配息」頁；抓取一次後快取於前端
let divData = {{}};
async function loadDividends(codes) {{
  try {{
    const dr = await fetch('/api/stocks/dividends/history?codes=' + codes);
    divData = await dr.json();
  }} catch(e) {{}}
}}

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
  let rows = '', totCost = 0, totValue = 0, totDiv = 0, totDivStock = 0;

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

    // 今年預計配息 / 配股：cashDiv 為每股現金股利(元)、stockDiv 為每股股票股利(元，面額10元)
    const cur = (divData[h.code] || {{}})[curYear.toString()];
    const shareCount = h.shares * 1000;
    const cashDiv = cur && cur.cashDiv > 0 ? cur.cashDiv : 0;
    const stockDiv = cur && cur.stockDiv > 0 ? cur.stockDiv : 0;
    const divAmount = cashDiv > 0 ? cashDiv * shareCount : null;      // 預計現金股利金額(元)
    const divStock = stockDiv > 0 ? shareCount * stockDiv / 10 : null; // 預計配股數(股)
    if (divAmount != null) totDiv += divAmount;
    if (divStock != null) totDivStock += divStock;

    rows += `<tr data-cost='${{h.cost}}' data-value='${{value || 0}}' data-div='${{divAmount || 0}}' data-divstock='${{divStock || 0}}'>
      <td>${{h.code}}</td><td>${{h.name}}</td>
      <td>${{h.shares}}</td><td>${{fmt(h.cost)}}</td>
      <td>${{costPerShare.toFixed(2)}}</td>
      <td>${{price != null ? price.toFixed(2) : '<span class=loading>—</span>'}}</td>
      <td>${{value != null ? fmt(value) : '<span class=loading>—</span>'}}</td>
      <td class='${{pnl != null ? cls(pnl) : ''}}'>${{pnl != null ? fmt(pnl) : '—'}}</td>
      <td class='${{pct != null ? cls(pct) : ''}}'>${{pct != null ? fmtP(pct) : '—'}}</td>
      <td class='pos'>${{divAmount != null ? fmt(divAmount) : '<span class=loading>—</span>'}}</td>
      <td class='year-stock'>${{divStock != null ? fmt(divStock) : '<span class=loading>—</span>'}}</td>
    </tr>`;
  }}

  tbody.innerHTML = rows || '<tr><td colspan=11 style=text-align:center>尚無庫存</td></tr>';

  const totPnl = totValue - totCost;
  const totPct = totCost > 0 ? totPnl / totCost * 100 : 0;
  document.getElementById('f-cost').textContent = fmt(totCost);
  document.getElementById('f-value').textContent = totValue > 0 ? fmt(totValue) : '—';
  document.getElementById('f-pnl').textContent = totValue > 0 ? fmt(totPnl) : '—';
  document.getElementById('f-pct').textContent = totValue > 0 ? fmtP(totPct) : '—';
  document.getElementById('f-div').textContent = totDiv > 0 ? fmt(totDiv) : '—';
  document.getElementById('f-divstock').textContent = totDivStock > 0 ? fmt(totDivStock) : '—';
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
  let totCost = 0, totValue = 0, hasVal = false, totDiv = 0, totDivStock = 0;
  for (const r of vis) {{
    totCost += +r.dataset.cost || 0;
    const v = +r.dataset.value || 0;
    if (v > 0) {{ totValue += v; hasVal = true; }}
    totDiv += +r.dataset.div || 0;
    totDivStock += +r.dataset.divstock || 0;
  }}
  document.getElementById('f-div').textContent = totDiv > 0 ? fmt(totDiv) : '—';
  document.getElementById('f-divstock').textContent = totDivStock > 0 ? fmt(totDivStock) : '—';
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

let _invCol = null;
load().then(async () => {{
  _invCol = setupColumns('inv-table', 'stocks-inv', {{}});
  const inv = initTable('inv-table', {{ cols: 11, onFilter: updateInvTotals }});
  if (inv) _invReapply = inv.run;
  // 配息資料抓取較慢，於價格載入後再補上，避免拖慢首屏
  const res = await fetch('/api/stocks/holdings');
  const holdings = await res.json();
  const codes = [...new Set(holdings.map(h => h.code))].join(',');
  if (codes) {{ await loadDividends(codes); await load(); if (_invCol) _invCol.reapply(); if (_invReapply) _invReapply(); }}
}});
setInterval(() => load().then(() => {{ if (_invCol) _invCol.reapply(); if (_invReapply) _invReapply(); }}), 60000);
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
        var colCount = 4 + (years.Count - 1) * 2 + 6;
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
td.div-manual{{position:relative}}
td.div-manual::after{{content:'✎';position:absolute;top:1px;right:2px;font-size:.62rem;color:#0055cc}}
.div-edit-btn.has-manual{{border-color:#0055cc;background:#eaf1ff;color:#0055cc}}
.dm-mask{{position:fixed;inset:0;background:rgba(0,0,0,.45);z-index:2000;display:flex;align-items:center;justify-content:center;padding:16px}}
.dm-modal{{background:#fff;border-radius:10px;padding:20px;width:640px;max-width:100%;max-height:90vh;overflow:auto;box-shadow:0 10px 40px rgba(0,0,0,.25)}}
.dm-head{{display:flex;align-items:flex-start;gap:10px;margin-bottom:4px}}
.dm-title{{font-weight:600;flex:1}}
.dm-close{{background:none;border:none;font-size:1.3rem;color:#888;cursor:pointer;line-height:1;padding:0 4px}}
.dm-sub{{color:#888;font-size:.8rem;margin-bottom:12px}}
.dm-year{{border:1px solid #e3e8f0;border-radius:8px;padding:12px 14px;margin-bottom:12px}}
.dm-year-head{{display:flex;align-items:center;gap:8px;margin-bottom:8px}}
.dm-year-title{{font-weight:600;flex:1}}
.dm-badge{{font-size:.72rem;background:#eaf1ff;color:#0055cc;border-radius:10px;padding:1px 8px}}
.dm-grid{{display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px}}
.dm-grid label{{font-size:.75rem;color:#666;margin-bottom:2px}}
.dm-grid input{{padding:6px 8px;font-size:.85rem}}
.dm-actions{{display:flex;justify-content:flex-end;gap:8px;margin-top:6px}}
.dm-clear{{color:#c0392b;font-size:.78rem;text-decoration:none;cursor:pointer}}
.dm-clear:hover{{text-decoration:underline}}
</style><script>{SharedLayout.TableJs}{SharedLayout.ExportJs}</script></head><body>
{NavBar("dividends")}
<div class='actions' style='margin-bottom:8px'>
  <h1 style='margin:0;flex:1'>💰 歷史配息紀錄（近五年）</h1>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('div-table','歷史配息',{{headers:[{exportHeaders}],skipCols:[3],btn:this}})"">⬇ 下載Excel</button>
</div>
<div class='table-wrap'>
<table id='div-table'>
<thead>
  <tr>
    <th rowspan='2'>股號</th><th rowspan='2'>股名</th><th rowspan='2'>帳戶</th><th rowspan='2'>維護</th>
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

let _divs = {{}}, _manual = {{}}, _names = {{}};

async function load() {{
  const res = await fetch('/api/stocks/holdings');
  const holdings = await res.json();
  if (!holdings.length) {{
    document.getElementById('tbody').innerHTML = '<tr><td colspan={colCount} style=text-align:center>尚無庫存</td></tr>';
    return;
  }}
  const codes = [...new Set(holdings.map(h => h.code))].join(',');
  try {{
    const dr = await fetch('/api/stocks/dividends/history?codes=' + codes);
    _divs = await dr.json();
  }} catch(e) {{}}
  try {{
    const mr = await fetch('/api/stocks/dividends/manual?codes=' + codes);
    _manual = await mr.json();
  }} catch(e) {{}}

  const tbody = document.getElementById('tbody');
  let rows = '';
  for (const h of holdings) {{
    _names[h.code] = h.name;
    const d = _divs[h.code] || {{}};
    const man = _manual[h.code] || {{}};
    let cells = '';
    for (const y of years) {{
      const yr = d[y.toString()];
      const isCur = y === currentYear;
      const mCls = man[y.toString()] ? ' div-manual' : '';
      if (yr == null) {{
        cells += isCur
          ? `<td class='empty cur-year'></td><td class='empty cur-year'></td><td class='cur-year'></td><td class='cur-year'></td><td class='cur-year'></td><td class='cur-year'></td>`
          : `<td class='empty'>—</td><td class='empty'>—</td>`;
      }} else {{
        const cash = yr.cashDiv > 0 ? yr.cashDiv.toFixed(2) : '—';
        const stock = yr.stockDiv > 0 ? yr.stockDiv.toFixed(4) : '—';
        cells += `<td class='${{yr.cashDiv > 0 ? 'year-cash' : 'empty'}}${{isCur ? ' cur-year' : ''}}${{mCls}}'>${{cash}}</td>`;
        cells += `<td class='${{yr.stockDiv > 0 ? 'year-stock' : 'empty'}}${{isCur ? ' cur-year' : ''}}${{mCls}}'>${{stock}}</td>`;
        if (isCur) {{
          cells += `<td class='date-col cur-year'>${{yr.cashExDate || ''}}</td>`;
          cells += `<td class='date-col cur-year'>${{yr.stockExDate || ''}}</td>`;
          cells += `<td class='date-col cur-year'>${{yr.cashPayDate || ''}}</td>`;
          cells += `<td class='date-col cur-year'>${{yr.stockPayDate || ''}}</td>`;
        }}
      }}
    }}
    const hasMan = Object.keys(man).length > 0;
    const btn = `<td><button class='btn btn-sm btn-outline div-edit-btn${{hasMan ? ' has-manual' : ''}}' onclick=""editDiv('${{h.code}}')"">✏️ 維護</button></td>`;
    rows += `<tr><td>${{h.code}}</td><td>${{h.name}}</td><td>${{h.type}}</td>${{btn}}${{cells}}</tr>`;
  }}
  tbody.innerHTML = rows;
}}

// ── 手動維護配息/配股 ──
function _vDate(s) {{ return /^\d{{4}}-\d{{2}}-\d{{2}}$/.test(s || '') && !String(s).startsWith('0000') ? s : ''; }}
function _vNum(n) {{ return (n != null && n > 0) ? String(n) : ''; }}

function editDiv(code) {{
  const name = _names[code] || '';
  const d = _divs[code] || {{}};
  const man = _manual[code] || {{}};
  const yearsDesc = years.slice().sort((a, b) => b - a);

  let inner = '';
  for (const y of yearsDesc) {{
    const ys = y.toString();
    const e = d[ys] || {{}};
    const isMan = !!man[ys];
    const cash = _vNum(e.cashDiv), stock = _vNum(e.stockDiv);
    const cashEx = _vDate(e.cashExDate), stockEx = _vDate(e.stockExDate), cashPay = _vDate(e.cashPayDate), stockPay = _vDate(e.stockPayDate);
    inner += `<div class='dm-year' data-year='${{ys}}'>
      <div class='dm-year-head'>
        <span class='dm-year-title'>${{y}} 年${{y === currentYear ? '（今年）' : ''}}</span>
        ${{isMan ? ""<span class='dm-badge'>手動維護</span><a class='dm-clear' data-year='"" + ys + ""'>改回自動</a>"" : ''}}
      </div>
      <div class='dm-grid'>
        <div><label>現金股利(元/股)</label><input type='number' step='0.0001' min='0' data-f='cash' data-init='${{cash}}' value='${{cash}}'></div>
        <div><label>股票股利(元/股)</label><input type='number' step='0.0001' min='0' data-f='stock' data-init='${{stock}}' value='${{stock}}'></div>
        <div></div>
        <div><label>除息交易日</label><input type='date' data-f='cashEx' data-init='${{cashEx}}' value='${{cashEx}}'></div>
        <div><label>除權交易日</label><input type='date' data-f='stockEx' data-init='${{stockEx}}' value='${{stockEx}}'></div>
        <div><label>現金股利發放日</label><input type='date' data-f='cashPay' data-init='${{cashPay}}' value='${{cashPay}}'></div>
        <div><label>股票股利發放日</label><input type='date' data-f='stockPay' data-init='${{stockPay}}' value='${{stockPay}}'></div>
      </div>
    </div>`;
  }}

  const mask = document.createElement('div');
  mask.className = 'dm-mask';
  const modal = document.createElement('div');
  modal.className = 'dm-modal';
  modal.innerHTML = `<div class='dm-head'><div class='dm-title'>✏️ 維護配息／配股 — ${{code}} ${{name}}</div><button type='button' class='dm-close'>×</button></div>
    <div class='dm-sub'>手動維護的年度會以此為準，且不再即時上網抓取。留白代表無資料。日期格式 yyyy-MM-dd。</div>
    <div id='dmMsg'></div>
    ${{inner}}
    <div class='dm-actions'><button type='button' class='btn dm-save'>儲存</button><button type='button' class='btn btn-outline dm-cancel'>取消</button></div>`;

  const close = () => {{ mask.remove(); document.removeEventListener('keydown', onKey); }};
  const onKey = ev => {{ if (ev.key === 'Escape') close(); }};
  modal.querySelector('.dm-close').addEventListener('click', close);
  modal.querySelector('.dm-cancel').addEventListener('click', close);
  mask.addEventListener('click', ev => {{ if (ev.target === mask) close(); }});
  modal.querySelectorAll('.dm-clear').forEach(a =>
    a.addEventListener('click', () => clearDivManual(code, a.dataset.year)));
  modal.querySelector('.dm-save').addEventListener('click', () => saveDivManual(code, modal));
  document.addEventListener('keydown', onKey);
  mask.appendChild(modal);
  document.body.appendChild(mask);
}}

async function saveDivManual(code, modal) {{
  const posts = [];
  for (const ye of modal.querySelectorAll('.dm-year')) {{
    const vals = {{}}; let changed = false;
    for (const inp of ye.querySelectorAll('input[data-f]')) {{
      vals[inp.dataset.f] = inp.value;
      if (inp.value !== (inp.dataset.init || '')) changed = true;
    }}
    if (!changed) continue;
    posts.push({{
      code, year: +ye.dataset.year,
      cashDiv: parseFloat(vals.cash) || 0,
      stockDiv: parseFloat(vals.stock) || 0,
      cashExDate: vals.cashEx || '', stockExDate: vals.stockEx || '',
      cashPayDate: vals.cashPay || '', stockPayDate: vals.stockPay || ''
    }});
  }}
  if (!posts.length) {{ document.getElementById('dmMsg').innerHTML = ""<div class='alert err'>沒有任何變更</div>""; return; }}
  const btn = modal.querySelector('.dm-save');
  btn.disabled = true; btn.textContent = '儲存中…';
  try {{
    for (const p of posts)
      await fetch('/api/stocks/dividends/manual', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify(p) }});
    location.reload();
  }} catch(e) {{
    btn.disabled = false; btn.textContent = '儲存';
    document.getElementById('dmMsg').innerHTML = ""<div class='alert err'>儲存失敗</div>"";
  }}
}}

async function clearDivManual(code, year) {{
  if (!confirm(year + ' 年改回自動抓取？將刪除該年度手動維護資料。')) return;
  await fetch('/api/stocks/dividends/manual?code=' + encodeURIComponent(code) + '&year=' + encodeURIComponent(year), {{ method: 'DELETE' }});
  location.reload();
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

        // 個股分析：依股號整理每次快照中該檔股票的張數/股價/市值，供前端篩選比較
        var stockMeta = snapshots
            .SelectMany(s => (s.Holdings ?? []).Select(h => new { h.Code, h.Name }))
            .GroupBy(x => x.Code)
            .Select(g => new { Code = g.Key, Name = g.Last().Name })
            .OrderBy(x => x.Code)
            .ToList();

        var stockSeriesData = stockMeta.ToDictionary(
            m => m.Code,
            m => snapshots.OrderBy(s => s.Date)
                .Select(s => new { s.Date, Holding = (s.Holdings ?? []).FirstOrDefault(h => h.Code == m.Code) })
                .Where(x => x.Holding != null)
                .Select(x => new { date = x.Date, shares = x.Holding!.Shares, price = x.Holding.Price, value = x.Holding.Value, cost = x.Holding.Cost })
                .ToList());

        var stockSeriesJson = JsonSerializer.Serialize(stockSeriesData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).Replace("</", "<\\/");
        var stockOptions = string.Join("", stockMeta.Select(m =>
            $"<option value='{System.Net.WebUtility.HtmlEncode(m.Code)}'>{System.Net.WebUtility.HtmlEncode(m.Code)} {System.Net.WebUtility.HtmlEncode(m.Name)}</option>"));

        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>市值快照</title>
<style>{CommonCss}
.form-card{{background:#fff;border-radius:8px;padding:20px;box-shadow:0 1px 4px rgba(0,0,0,.08);max-width:680px;margin-bottom:20px}}
.field{{margin-bottom:14px}}
label{{display:block;font-size:.85rem;color:#555;margin-bottom:4px;font-weight:500}}
input,select{{width:100%;padding:9px 12px;border:1px solid #ddd;border-radius:6px;font-size:.95rem;box-sizing:border-box}}
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
  <h2 style='margin-top:0'>個股分析：庫存增減 vs 市值變化</h2>
  <div class='field' style='max-width:320px'>
    <label>選擇股票</label>
    <select id='stockFilter' onchange='renderStockAnalysis()'>
      <option value=''>請選擇…</option>
      {stockOptions}
    </select>
  </div>
  <div class='summary' id='stockSummary'></div>
  <div class='table-wrap'>
  <table id='stock-analysis-table'>
  <thead><tr><th>日期</th><th>張數</th><th>張數增減</th><th>股價</th><th>股價漲跌</th><th>市值(元)</th><th>市值增減</th></tr></thead>
  <tbody id='stockRows'><tr><td colspan='7' style='text-align:center;padding:20px;color:#999'>請先選擇股票</td></tr></tbody>
  </table>
  </div>
  <p style='font-size:.78rem;color:#999;margin-top:8px'>將市值增減拆解為「張數增減」（加碼/減碼帶來的變化）與「股價漲跌」兩部分，可看出該檔市值成長主要來自持股增加還是股價上漲。</p>
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
const stockSeries = {stockSeriesJson};

function renderStockAnalysis() {{
  const code = document.getElementById('stockFilter').value;
  const tbody = document.getElementById('stockRows');
  const summaryEl = document.getElementById('stockSummary');
  const series = stockSeries[code];
  if (!code || !series || series.length === 0) {{
    tbody.innerHTML = ""<tr><td colspan='7' style='text-align:center;padding:20px;color:#999'>請先選擇股票</td></tr>"";
    summaryEl.innerHTML = '';
    return;
  }}

  const pct = (a, b) => a > 0 ? (b - a) / a * 100 : null;
  const fmtPct = p => p === null ? '—' : `<span class=""${{p >= 0 ? 'pos' : 'neg'}}"">${{p >= 0 ? '+' : ''}}${{p.toFixed(2)}}%</span>`;

  let rows = '';
  for (let i = 0; i < series.length; i++) {{
    const cur = series[i];
    let sharesChange = '—', priceChange = '—', valueChange = '—';
    if (i > 0) {{
      const prev = series[i - 1];
      sharesChange = fmtPct(pct(prev.shares, cur.shares));
      priceChange = fmtPct(pct(prev.price, cur.price));
      valueChange = fmtPct(pct(prev.value, cur.value));
    }}
    rows += `<tr><td>${{cur.date}}</td><td>${{cur.shares.toLocaleString()}}</td><td>${{sharesChange}}</td><td>${{cur.price.toFixed(2)}}</td><td>${{priceChange}}</td><td>${{Math.round(cur.value).toLocaleString()}}</td><td>${{valueChange}}</td></tr>`;
  }}
  tbody.innerHTML = rows;

  const first = series[0], last = series[series.length - 1];
  const shareGrowth = pct(first.shares, last.shares);
  const priceGrowth = pct(first.price, last.price);
  const valueGrowth = pct(first.value, last.value);
  summaryEl.innerHTML = `
    <div class='card'><div class='label'>期間</div><div class='value' style='font-size:1rem'>${{first.date}} ~ ${{last.date}}</div></div>
    <div class='card'><div class='label'>張數增加幅度</div><div class='value'>${{fmtPct(shareGrowth)}}</div></div>
    <div class='card'><div class='label'>股價漲跌幅度</div><div class='value'>${{fmtPct(priceGrowth)}}</div></div>
    <div class='card'><div class='label'>市值增加幅度</div><div class='value'>${{fmtPct(valueGrowth)}}</div></div>
  `;
}}

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
</style><script>{SharedLayout.TableJs}{SharedLayout.ExportJs}{SharedLayout.ColumnChooserJs}</script></head><body>
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
setupColumns('hist-table', 'stocks-history', {{ labels: {{ 12: '操作' }} }});
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

    // ── 勝率回測 ────────────────────────────────────────────────────────────────
    static async Task BacktestPage(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildBacktestHtml());
    }

    // 取股名（沿用即時報價來源，失敗回空字串）
    static async Task<string> GetStockNameSafe(string code, IHttpClientFactory httpFactory)
    {
        try
        {
            var client = httpFactory.CreateClient("twse");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://mis.twse.com.tw/");
            var response = await client.GetStringAsync($"/stock/api/getStockInfo.jsp?ex_ch=tse_{code}.tw&json=1&delay=0");
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("msgArray", out var arr) && arr.GetArrayLength() > 0
                && arr[0].TryGetProperty("n", out var nEl))
                return nEl.GetString() ?? "";
        }
        catch { }
        return "";
    }

    // 以 FinMind 十年日收盤價回測「連跌/連漲 N 天後買入、持有 M 個交易日賣出」各組合的勝率與報酬率
    static async Task<IResult> RunBacktest(string code, int? years, IHttpClientFactory httpFactory)
    {
        code = (code ?? "").Trim();
        if (string.IsNullOrEmpty(code))
            return Results.Json(new { error = "請輸入股號" });
        int yrs = Math.Clamp(years ?? 10, 1, 20);
        try
        {
            var client = httpFactory.CreateClient("finmind");
            var end = DateTime.Today;
            var start = end.AddYears(-yrs);
            var resp = await client.GetStringAsync(
                $"/api/v4/data?dataset=TaiwanStockPrice&data_id={code}&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.GetArrayLength() == 0)
                return Results.Json(new { error = "查無此股號的歷史股價資料（請確認為上市股票代號）" });

            var dates = new List<string>();
            var closes = new List<double>();
            foreach (var item in arr.EnumerateArray())
            {
                var d = item.TryGetProperty("date", out var de) ? de.GetString() : null;
                if (string.IsNullOrEmpty(d)) continue;
                if (!item.TryGetProperty("close", out var c) || c.ValueKind != JsonValueKind.Number) continue;
                var close = c.GetDouble();
                if (close <= 0) continue;
                dates.Add(d);
                closes.Add(close);
            }
            int n = closes.Count;
            if (n < 60)
                return Results.Json(new { error = "歷史資料不足，無法回測" });

            // 連續漲跌天數（平盤同時歸零）
            var downStreak = new int[n];
            var upStreak = new int[n];
            for (int i = 1; i < n; i++)
            {
                if (closes[i] < closes[i - 1]) { downStreak[i] = downStreak[i - 1] + 1; upStreak[i] = 0; }
                else if (closes[i] > closes[i - 1]) { upStreak[i] = upStreak[i - 1] + 1; downStreak[i] = 0; }
            }

            // 各交易日對應的日曆年份，供「年度分佈」統計用
            var yearList = new List<int>();
            var yearIdx = new Dictionary<int, int>();
            var yearOf = new int[n];
            for (int i = 0; i < n; i++)
            {
                int y = int.Parse(dates[i][..4]);
                if (!yearIdx.TryGetValue(y, out var idx))
                {
                    idx = yearList.Count; yearIdx[y] = idx; yearList.Add(y);
                }
                yearOf[i] = idx;
            }

            int[] holds = { 1, 3, 5, 10, 20, 60 };
            int[] streakNs = { 1, 2, 3, 4, 5 };
            const int minTrades = 20;
            var results = new List<object>();
            object? best = null;
            double bestWin = -1, bestRet = double.NegativeInfinity;

            foreach (var (dir, dirLabel) in new[] { ("down", "連跌"), ("up", "連漲") })
            {
                var streak = dir == "down" ? downStreak : upStreak;
                foreach (var nn in streakNs)
                {
                    foreach (var hold in holds)
                    {
                        int trades = 0, wins = 0;
                        double sumRet = 0, maxWin = double.NegativeInfinity, maxLoss = double.PositiveInfinity;
                        var byYear = new int[yearList.Count];
                        for (int i = 0; i + hold < n; i++)
                        {
                            if (streak[i] != nn) continue;
                            double buy = closes[i], sell = closes[i + hold];
                            double ret = (sell - buy) / buy * 100.0;
                            trades++;
                            byYear[yearOf[i]]++;
                            sumRet += ret;
                            if (ret > 0) wins++;
                            if (ret > maxWin) maxWin = ret;
                            if (ret < maxLoss) maxLoss = ret;
                        }
                        if (trades == 0) continue;
                        double winRate = (double)wins / trades * 100.0;
                        double avgRet = sumRet / trades;
                        var row = new
                        {
                            entry = dir,
                            entryLabel = $"{dirLabel}{nn}天後買入",
                            n = nn,
                            holdDays = hold,
                            trades,
                            winRate = Math.Round(winRate, 1),
                            avgReturn = Math.Round(avgRet, 2),
                            maxWin = Math.Round(maxWin, 2),
                            maxLoss = Math.Round(maxLoss, 2),
                            byYear
                        };
                        results.Add(row);
                        if (trades >= minTrades &&
                            (winRate > bestWin || (Math.Abs(winRate - bestWin) < 1e-9 && avgRet > bestRet)))
                        {
                            bestWin = winRate; bestRet = avgRet; best = row;
                        }
                    }
                }
            }

            var name = await GetStockNameSafe(code, httpFactory);
            double yearsSpan = (DateTime.Parse(dates[^1]) - DateTime.Parse(dates[0])).TotalDays / 365.25;
            if (yearsSpan < 0.5) yearsSpan = 0.5;
            return Results.Json(new
            {
                code,
                name,
                from = dates[0],
                to = dates[^1],
                dataPoints = n,
                yearsSpan = Math.Round(yearsSpan, 1),
                years = yearList,
                minTrades,
                best,
                results
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "資料取得失敗：" + ex.Message });
        }
    }

    static string BuildBacktestHtml()
    {
        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>股價勝率回測</title>
<style>{CommonCss}
.bt-form{{display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end;background:#fff;border-radius:8px;padding:16px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08);margin-bottom:20px}}
.bt-form .field{{margin-bottom:0}}
.bt-form input,.bt-form select{{width:auto}}
.bt-form input#code{{width:150px}}
.hint{{font-size:.82rem;color:#888;margin:0 0 16px}}
.best-card{{background:linear-gradient(135deg,#0055cc,#3a7bd5);color:#fff;border-radius:10px;padding:18px 22px;margin-bottom:20px;box-shadow:0 3px 12px rgba(0,85,204,.25)}}
.best-card .bc-title{{font-size:.82rem;opacity:.85;margin-bottom:8px}}
.best-card .bc-main{{font-size:1.35rem;font-weight:700;margin-bottom:10px;line-height:1.4}}
.best-card .bc-stats{{display:flex;gap:24px;flex-wrap:wrap}}
.best-card .bc-stats div{{font-size:.85rem;opacity:.9}}
.best-card .bc-stats b{{display:block;font-size:1.25rem;margin-top:2px}}
.meta{{font-size:.85rem;color:#666;margin-bottom:14px}}
#bt-table th{{cursor:pointer;user-select:none;text-align:right}}
#bt-table th:first-child{{text-align:left}}
#bt-table td{{text-align:right}}
#bt-table td:first-child{{text-align:left}}
#bt-table th.sorted::after{{content:' ▾';font-size:.7rem;color:#0055cc}}
#bt-table th.sorted.asc::after{{content:' ▴'}}
#bt-table tbody tr{{cursor:pointer}}
#bt-table tbody tr:hover td{{background:#eef4ff}}
tr.hl td{{background:#fff8e0}}
tr.sel td{{box-shadow:inset 3px 0 0 #0055cc}}
tr.sel.hl td{{background:#fff8e0}}
.pos{{color:#c00}}.neg{{color:#080}}
.spin{{color:#999;font-style:italic}}
.year-sec{{background:#fff;border-radius:8px;padding:18px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08);margin:20px 0}}
.year-sec h2{{margin:0 0 4px;border:none;padding:0}}
.year-sub{{font-size:.85rem;color:#666;margin:0 0 16px}}
.year-chart{{display:flex;align-items:flex-end;gap:6px;height:180px;padding-top:10px;overflow-x:auto}}
.yc-col{{flex:1;min-width:34px;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;height:100%}}
.yc-cnt{{font-size:.78rem;font-weight:600;color:#0055cc;margin-bottom:2px}}
.yc-bar{{width:70%;max-width:38px;background:linear-gradient(180deg,#3a7bd5,#0055cc);border-radius:4px 4px 0 0;min-height:2px;transition:height .2s}}
.yc-bar.zero{{background:#e2e6ec}}
.yc-yr{{font-size:.75rem;color:#888;margin-top:5px;transform:rotate(-40deg);transform-origin:center;white-space:nowrap}}
</style></head><body>
{NavBar("backtest")}
<h1 style='margin:0 0 6px'>📊 股價勝率回測</h1>
<p class='hint'>輸入股號，統計近十年日收盤價，測試「連跌／連漲 N 天後買入、持有 M 個交易日後賣出」各種組合的勝率與平均報酬率，找出勝率最高的進場方式。</p>

<div class='bt-form'>
  <div class='field'><label for='code'>股號</label><input id='code' placeholder='如 2330' autocomplete='off'></div>
  <div class='field'><label for='years'>統計期間</label>
    <select id='years'>
      <option value='10' selected>近 10 年</option>
      <option value='5'>近 5 年</option>
      <option value='3'>近 3 年</option>
      <option value='15'>近 15 年</option>
    </select>
  </div>
  <div class='field'><label for='amount'>投資金額 (元)</label><input id='amount' type='number' value='5000000' min='0' step='100000' style='width:130px'></div>
  <div class='field'><button class='btn' id='go'>開始回測</button></div>
</div>

<div id='status'></div>
<div id='result' style='display:none'>
  <div id='best'></div>
  <div class='meta' id='meta'></div>
  <div class='year-sec' id='year-sec'>
    <h2 id='yc-title'>📅 訊號年度分佈</h2>
    <p class='year-sub' id='yc-sub'>點選下方表格任一列，即可看該組合每年出現幾次買入訊號。</p>
    <div class='year-chart' id='yc-chart'></div>
  </div>
  <div class='table-wrap'>
    <table id='bt-table'>
      <thead><tr>
        <th data-k='entryLabel'>買入訊號</th>
        <th data-k='holdDays'>持有天數</th>
        <th data-k='trades'>訊號次數<br><span style='font-weight:400;font-size:.75rem;color:#888'>(10年)</span></th>
        <th data-k='perYear'>每年約</th>
        <th data-k='perYearMax'>每年最多</th>
        <th data-k='perYearMin'>每年最少</th>
        <th data-k='winRate'>勝率</th>
        <th data-k='avgReturn'>平均報酬率</th>
        <th data-k='profitPer'>平均每次獲利</th>
        <th data-k='maxWin'>最大獲利率</th>
        <th data-k='maxLoss'>最大虧損率</th>
      </tr></thead>
      <tbody></tbody>
    </table>
  </div>
  <p class='hint' style='margin-top:12px'>※「訊號次數」為這段期間內符合買入條件、且持有到期能完成賣出的次數（每次買進持有到期賣出算一次）；「每年約」為訊號次數 ÷ 實際資料年數，「每年最多／最少」為各日曆年度中出現次數的最大／最小值（頭尾年份可能不足整年，最少值會偏低）。「平均每次獲利」= 投資金額 × 平均報酬率（每次都投入設定金額的估算）。報酬率未計入手續費與交易稅，僅以收盤價估算；勝率為報酬率大於 0 的交易佔比。標黃列為建議組合（訊號次數 ≥ <span id='mt'></span> 中勝率最高者）；點選任一列可查看該組合的年度分佈。</p>
</div>

<script>
let _rows = [], _best = null, _ys = 10, _years = [], _selKey = null, _d = null;
let _sortK = 'winRate', _sortAsc = false;

function keyOf(r) {{ return r.entryLabel + '|' + r.holdDays; }}
function getAmt() {{ const v = parseFloat(document.getElementById('amount').value); return isFinite(v) && v > 0 ? v : 0; }}
function money(v) {{ return (v < 0 ? '-$' : '$') + Math.abs(Math.round(v)).toLocaleString('en-US'); }}
function applyAmount() {{ const a = getAmt(); _rows.forEach(r => r.profitPer = a * r.avgReturn / 100); }}

const codeEl = document.getElementById('code');
const goEl = document.getElementById('go');
goEl.addEventListener('click', run);
codeEl.addEventListener('keydown', e => {{ if (e.key === 'Enter') run(); }});
document.getElementById('amount').addEventListener('input', () => {{
  if (!_rows.length) return;
  applyAmount();
  if (_d) renderBest(_d);
  renderTable();
}});

function fmt(v) {{ return (v > 0 ? '+' : '') + v.toFixed(2) + '%'; }}
function cls(v) {{ return v > 0 ? 'pos' : (v < 0 ? 'neg' : ''); }}

async function run() {{
  const code = codeEl.value.trim();
  if (!code) {{ codeEl.focus(); return; }}
  const years = document.getElementById('years').value;
  const st = document.getElementById('status');
  const res = document.getElementById('result');
  res.style.display = 'none';
  st.innerHTML = ""<p class='spin'>回測中，正在抓取歷史股價…</p>"";
  goEl.disabled = true;
  try {{
    const r = await fetch('/api/stocks/backtest?code=' + encodeURIComponent(code) + '&years=' + years);
    const d = await r.json();
    if (d.error) {{ st.innerHTML = ""<div class='alert err'>"" + d.error + ""</div>""; return; }}
    st.innerHTML = '';
    _rows = d.results; _best = d.best; _ys = d.yearsSpan || 10; _years = d.years || []; _d = d;
    _rows.forEach(r => {{
      r.perYear = Math.round(r.trades / _ys * 10) / 10;
      r.perYearMax = r.byYear.length ? Math.max(...r.byYear) : 0;
      r.perYearMin = r.byYear.length ? Math.min(...r.byYear) : 0;
    }});
    applyAmount();
    document.getElementById('mt').textContent = d.minTrades;
    renderBest(d);
    renderMeta(d);
    _sortK = 'winRate'; _sortAsc = false;
    _selKey = _best ? keyOf(_best) : (_rows[0] ? keyOf(_rows[0]) : null);
    renderTable();
    renderChart();
    res.style.display = 'block';
  }} catch (e) {{
    st.innerHTML = ""<div class='alert err'>回測失敗："" + e + ""</div>"";
  }} finally {{
    goEl.disabled = false;
  }}
}}

function renderBest(d) {{
  const b = _best;
  const el = document.getElementById('best');
  if (!b) {{ el.innerHTML = ""<div class='alert err'>資料不足，找不到足夠交易次數的組合。</div>""; return; }}
  const a = getAmt();
  const profitPer = a * b.avgReturn / 100;
  const profitTotal = profitPer * b.trades;
  el.innerHTML =
    ""<div class='best-card'>"" +
    ""<div class='bc-title'>🏆 勝率最高的買入策略</div>"" +
    ""<div class='bc-main'>"" + b.entryLabel + ""，"" + b.holdDays + "" 個交易日後賣出</div>"" +
    ""<div class='bc-stats'>"" +
      ""<div>勝率<b>"" + b.winRate.toFixed(1) + ""%</b></div>"" +
      ""<div>平均報酬率<b>"" + fmt(b.avgReturn) + ""</b></div>"" +
      ""<div>投入 "" + money(a) + "" 平均每次獲利<b>"" + money(profitPer) + ""</b></div>"" +
      ""<div>十年累計獲利(估)<b>"" + money(profitTotal) + ""</b></div>"" +
      ""<div>訊號次數<b>"" + b.trades + "" 次（每年約 "" + (Math.round(b.trades / _ys * 10) / 10).toFixed(1) + ""）</b></div>"" +
    ""</div></div>"";
}}

function renderMeta(d) {{
  document.getElementById('meta').textContent =
    (d.name ? d.code + ' ' + d.name : d.code) +
    '　資料期間 ' + d.from + ' ~ ' + d.to + '（' + d.dataPoints + ' 個交易日）';
}}

function renderTable() {{
  const rows = _rows.slice().sort((a, b) => {{
    let x = a[_sortK], y = b[_sortK];
    if (x < y) return _sortAsc ? -1 : 1;
    if (x > y) return _sortAsc ? 1 : -1;
    return 0;
  }});
  const bk = _best ? keyOf(_best) : '';
  const tb = document.querySelector('#bt-table tbody');
  tb.innerHTML = rows.map(r => {{
    const k = keyOf(r);
    const cl = [k === bk ? 'hl' : '', k === _selKey ? 'sel' : ''].filter(Boolean).join(' ');
    return ""<tr data-k='"" + k + ""'"" + (cl ? "" class='"" + cl + ""'"" : '') + ""><td>"" + r.entryLabel +
      ""</td><td>"" + r.holdDays + ""</td><td>"" + r.trades +
      ""</td><td>"" + r.perYear.toFixed(1) + "" 次</td>"" +
      ""<td>"" + r.perYearMax + "" 次</td>"" +
      ""<td>"" + r.perYearMin + "" 次</td>"" +
      ""<td>"" + r.winRate.toFixed(1) + ""%</td>"" +
      ""<td class='"" + cls(r.avgReturn) + ""'>"" + fmt(r.avgReturn) + ""</td>"" +
      ""<td class='"" + cls(r.profitPer) + ""'>"" + money(r.profitPer) + ""</td>"" +
      ""<td class='pos'>"" + fmt(r.maxWin) + ""</td>"" +
      ""<td class='neg'>"" + fmt(r.maxLoss) + ""</td></tr>"";
  }}).join('');
  document.querySelectorAll('#bt-table th').forEach(th => {{
    th.classList.toggle('sorted', th.dataset.k === _sortK);
    th.classList.toggle('asc', th.dataset.k === _sortK && _sortAsc);
  }});
}}

function renderChart() {{
  const r = _rows.find(x => keyOf(x) === _selKey);
  const chart = document.getElementById('yc-chart');
  const title = document.getElementById('yc-title');
  const sub = document.getElementById('yc-sub');
  if (!r || !r.byYear) {{ chart.innerHTML = ''; return; }}
  const isBest = _best && keyOf(_best) === _selKey;
  title.innerHTML = '📅 訊號年度分佈　<span style=""font-weight:400;font-size:.9rem;color:#0055cc"">' +
    r.entryLabel + '，持有 ' + r.holdDays + ' 天' + (isBest ? '（建議組合）' : '') + '</span>';
  const total = r.byYear.reduce((a, b) => a + b, 0);
  sub.textContent = '十年共 ' + total + ' 次，平均每年 ' + r.perYear.toFixed(1) +
    ' 次（最多 ' + r.perYearMax + ' 次、最少 ' + r.perYearMin + ' 次）。' +
    (r.perYearMax >= r.perYearMin * 3 && r.perYearMin >= 0 ? ' 分佈不均，集中在特定年份。' : '');
  const mx = Math.max(1, r.perYearMax);
  chart.innerHTML = _years.map((y, i) => {{
    const c = r.byYear[i] || 0;
    const h = Math.round(c / mx * 140);
    return ""<div class='yc-col'><div class='yc-cnt'>"" + c + ""</div>"" +
      ""<div class='yc-bar"" + (c === 0 ? "" zero"" : '') + ""' style='height:"" + Math.max(2, h) + ""px'></div>"" +
      ""<div class='yc-yr'>"" + y + ""</div></div>"";
  }}).join('');
}}

document.querySelector('#bt-table tbody').addEventListener('click', e => {{
  const tr = e.target.closest('tr');
  if (!tr || !tr.dataset.k) return;
  _selKey = tr.dataset.k;
  document.querySelectorAll('#bt-table tbody tr').forEach(x =>
    x.classList.toggle('sel', x.dataset.k === _selKey));
  renderChart();
  document.getElementById('year-sec').scrollIntoView({{ behavior: 'smooth', block: 'nearest' }});
}});

document.querySelectorAll('#bt-table th').forEach(th => {{
  th.addEventListener('click', () => {{
    const k = th.dataset.k;
    if (_sortK === k) _sortAsc = !_sortAsc;
    else {{ _sortK = k; _sortAsc = (k === 'entryLabel'); }}
    renderTable();
  }});
}});

const _qcode = new URLSearchParams(location.search).get('code');
if (_qcode) {{ codeEl.value = _qcode; run(); }}
</script>
</body></html>";
    }

    // ── 關注訊號（今日買賣訊號） ──────────────────────────────────────────────────
    static List<WatchItem> LoadWatchlist()
    {
        if (!File.Exists(WatchlistFile)) return [];
        try { return JsonSerializer.Deserialize<List<WatchItem>>(File.ReadAllText(WatchlistFile), JsonOpts) ?? []; }
        catch { return []; }
    }

    static void SaveWatchlist(List<WatchItem> items) =>
        File.WriteAllText(WatchlistFile, JsonSerializer.Serialize(items, JsonOpts));

    static async Task WatchlistPage(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildWatchlistHtml());
    }

    static IResult GetWatchlist() => Results.Json(LoadWatchlist());

    static async Task<IResult> AddWatch(WatchRequest req, IHttpClientFactory httpFactory)
    {
        var code = (req.Code ?? "").Trim();
        if (string.IsNullOrEmpty(code)) return Results.BadRequest(new { error = "請輸入股號" });
        var dir = req.Dir == "up" ? "up" : "down";
        var nn = Math.Clamp(req.N, 1, 5);
        var hold = Math.Clamp(req.HoldDays, 1, 250);
        var name = string.IsNullOrWhiteSpace(req.Name) ? await GetStockNameSafe(code, httpFactory) : req.Name!.Trim();

        var items = LoadWatchlist();
        // 同股號＋同規則＋同持有天數視為重複
        if (items.Any(w => w.Code == code && w.Dir == dir && w.N == nn && w.HoldDays == hold))
            return Results.Conflict(new { error = "已有相同的關注設定" });
        items.Add(new WatchItem(Guid.NewGuid().ToString("N")[..8], code, name, dir, nn, hold,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        SaveWatchlist(items);
        return Results.Json(new { ok = true });
    }

    static IResult DeleteWatch(string id)
    {
        var items = LoadWatchlist();
        var removed = items.RemoveAll(w => w.Id == id);
        if (removed > 0) SaveWatchlist(items);
        return Results.Json(new { ok = removed > 0 });
    }

    // 對每個關注項目，用最新已確定的日收盤價判斷「今天是否該買 / 該賣」
    static async Task<IResult> GetSignals(IHttpClientFactory httpFactory)
    {
        var items = LoadWatchlist();
        var tasks = items.Select(w => ComputeSignal(w, httpFactory)).ToList();
        var results = await Task.WhenAll(tasks);
        return Results.Json(new { asOf = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), items = results });
    }

    static async Task<object> ComputeSignal(WatchItem w, IHttpClientFactory httpFactory)
    {
        var dirLabel = w.Dir == "down" ? "連跌" : "連漲";
        try
        {
            var client = httpFactory.CreateClient("finmind");
            var end = DateTime.Today;
            var start = end.AddDays(-(w.HoldDays * 2 + 80));
            var resp = await client.GetStringAsync(
                $"/api/v4/data?dataset=TaiwanStockPrice&data_id={w.Code}&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.GetArrayLength() == 0)
                return new { w.Id, w.Code, w.Name, w.Dir, dirLabel, w.N, w.HoldDays, error = "查無股價資料" };

            var dates = new List<string>();
            var closes = new List<double>();
            foreach (var it in arr.EnumerateArray())
            {
                var d = it.TryGetProperty("date", out var de) ? de.GetString() : null;
                if (string.IsNullOrEmpty(d)) continue;
                if (!it.TryGetProperty("close", out var c) || c.ValueKind != JsonValueKind.Number) continue;
                var cl = c.GetDouble();
                if (cl <= 0) continue;
                dates.Add(d); closes.Add(cl);
            }
            int n = closes.Count;
            if (n < 2)
                return new { w.Id, w.Code, w.Name, w.Dir, dirLabel, w.N, w.HoldDays, error = "資料不足" };

            var down = new int[n];
            var up = new int[n];
            for (int i = 1; i < n; i++)
            {
                if (closes[i] < closes[i - 1]) { down[i] = down[i - 1] + 1; up[i] = 0; }
                else if (closes[i] > closes[i - 1]) { up[i] = up[i - 1] + 1; down[i] = 0; }
            }
            var streak = w.Dir == "down" ? down : up;
            int last = n - 1;

            // 目前連續狀態（正=連漲、負=連跌、0=平盤）
            int signed = up[last] > 0 ? up[last] : (down[last] > 0 ? -down[last] : 0);
            string streakText = signed > 0 ? $"連漲 {signed} 天" : signed < 0 ? $"連跌 {-signed} 天" : "平盤";

            bool buyToday = streak[last] == w.N;
            int toGo = (streak[last] > 0 && streak[last] < w.N) ? w.N - streak[last] : -1;

            bool sellToday = false;
            string? buyDate = null; double? buyPrice = null, sellReturn = null;
            int sellIdx = last - w.HoldDays;
            if (sellIdx >= 1 && streak[sellIdx] == w.N)
            {
                sellToday = true;
                buyDate = dates[sellIdx];
                buyPrice = closes[sellIdx];
                sellReturn = Math.Round((closes[last] - closes[sellIdx]) / closes[sellIdx] * 100, 2);
            }

            return new
            {
                w.Id, w.Code, w.Name, w.Dir, dirLabel, w.N, w.HoldDays,
                latestDate = dates[last],
                latestClose = Math.Round(closes[last], 2),
                streakText,
                buyToday,
                toGo,
                sellToday,
                buyDate,
                buyPrice = buyPrice.HasValue ? Math.Round(buyPrice.Value, 2) : (double?)null,
                sellReturn
            };
        }
        catch
        {
            return new { w.Id, w.Code, w.Name, w.Dir, dirLabel, w.N, w.HoldDays, error = "資料取得失敗" };
        }
    }

    static string BuildWatchlistHtml()
    {
        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>關注訊號</title>
<style>{CommonCss}
.wl-form{{display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end;background:#fff;border-radius:8px;padding:16px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08);margin-bottom:18px}}
.wl-form .field{{margin-bottom:0}}
.wl-form input,.wl-form select{{width:auto}}
.wl-form input#wcode{{width:120px}}
.hint{{font-size:.82rem;color:#888;margin:0 0 16px}}
.sig-cards{{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:14px}}
.sig-card{{background:#fff;border-radius:10px;padding:16px 18px;box-shadow:0 1px 4px rgba(0,0,0,.08);border-left:5px solid #dde2e8;position:relative}}
.sig-card.buy{{border-left-color:#c0392b}}
.sig-card.sell{{border-left-color:#1e7e34}}
.sig-head{{display:flex;align-items:baseline;gap:8px;margin-bottom:2px}}
.sig-name{{font-size:1.05rem;font-weight:700}}
.sig-rule{{font-size:.82rem;color:#666;margin-bottom:10px}}
.sig-badge{{display:inline-block;padding:4px 12px;border-radius:16px;font-size:.9rem;font-weight:700;margin-bottom:8px}}
.sig-badge.buy{{background:#fdecea;color:#c0392b}}
.sig-badge.sell{{background:#e6f4ea;color:#1e7e34}}
.sig-badge.hold{{background:#eef2ff;color:#556}}
.sig-line{{font-size:.85rem;color:#444;margin:3px 0}}
.sig-del{{position:absolute;top:12px;right:12px;background:none;border:none;color:#bbb;cursor:pointer;font-size:1rem;padding:2px 6px;border-radius:4px}}
.sig-del:hover{{background:#fde8e8;color:#c0392b}}
.pos{{color:#c00}}.neg{{color:#080}}
.spin{{color:#999;font-style:italic}}
</style></head><body>
{NavBar("watchlist")}
<h1 style='margin:0 0 6px'>🔔 關注個股・今日買賣訊號</h1>
<p class='hint'>加入想追蹤的股票與進場規則，本頁會用<b>最新已確定的日收盤價</b>判斷今天是否觸發買入訊號，以及先前依此規則買入的部位是否已到賣出日。（台股 13:30 收盤，盤中開啟時以前一交易日收盤價為準）</p>

<div class='wl-form'>
  <div class='field'><label for='wcode'>股號</label><input id='wcode' placeholder='如 2330' autocomplete='off'></div>
  <div class='field'><label for='wdir'>進場規則</label>
    <select id='wdir'><option value='down' selected>連跌</option><option value='up'>連漲</option></select>
  </div>
  <div class='field'><label for='wn'>天數</label>
    <select id='wn'><option>1</option><option>2</option><option selected>3</option><option>4</option><option>5</option></select>
  </div>
  <div class='field'><label for='whold'>持有天數後賣出</label>
    <select id='whold'><option>1</option><option>3</option><option>5</option><option selected>10</option><option>20</option><option>60</option></select>
  </div>
  <div class='field'><button class='btn' id='wadd'>加入關注</button></div>
</div>
<div id='wmsg'></div>

<div class='meta' id='asof' style='font-size:.82rem;color:#888;margin-bottom:12px'></div>
<div id='sigs'><p class='spin'>載入中…</p></div>

<script>
const dirTxt = {{ down: '連跌', up: '連漲' }};

async function loadSigs() {{
  const box = document.getElementById('sigs');
  try {{
    const r = await fetch('/api/stocks/signals');
    const d = await r.json();
    document.getElementById('asof').textContent = '資料判斷時間：' + d.asOf;
    if (!d.items.length) {{
      box.innerHTML = ""<div class='empty-state'><div class='icon'>🔕</div>尚未加入任何關注個股，先在上方加入吧。</div>"";
      return;
    }}
    box.innerHTML = ""<div class='sig-cards'>"" + d.items.map(card).join('') + ""</div>"";
  }} catch (e) {{
    box.innerHTML = ""<div class='alert err'>載入失敗："" + e + ""</div>"";
  }}
}}

function card(s) {{
  const rule = dirTxt[s.dir] + s.n + '天後買入 → 持有 ' + s.holdDays + ' 天賣出';
  if (s.error) {{
    return ""<div class='sig-card'><button class='sig-del' onclick=\""delWatch('"" + s.id + ""')\"">✕</button>"" +
      ""<div class='sig-head'><span class='sig-name'>"" + s.code + "" "" + (s.name || '') + ""</span></div>"" +
      ""<div class='sig-rule'>"" + rule + ""</div><div class='sig-line' style='color:#c0392b'>⚠ "" + s.error + ""</div></div>"";
  }}
  let cls = '', badge = ''; let lines = '';
  if (s.buyToday) {{
    cls = 'buy'; badge = ""<div class='sig-badge buy'>🔴 今日買入訊號</div>"";
    lines += ""<div class='sig-line'>剛好"" + dirTxt[s.dir] + s.n + ""天，符合進場條件</div>"";
  }} else if (s.sellToday) {{
    cls = 'sell'; badge = ""<div class='sig-badge sell'>🟢 今日賣出訊號</div>"";
    lines += ""<div class='sig-line'>"" + s.buyDate + "" 依規則買入（"" + s.buyPrice + ""），持有 "" + s.holdDays + "" 天到期</div>"";
    const rc = s.sellReturn >= 0 ? 'pos' : 'neg';
    lines += ""<div class='sig-line'>持有報酬 <b class='"" + rc + ""'>"" + (s.sellReturn >= 0 ? '+' : '') + s.sellReturn + ""%</b></div>"";
  }} else {{
    badge = ""<div class='sig-badge hold'>⚪ 無訊號</div>"";
    if (s.toGo > 0) lines += ""<div class='sig-line'>目前"" + s.streakText + ""，距離觸發還差 "" + s.toGo + "" 天</div>"";
    else lines += ""<div class='sig-line'>目前"" + s.streakText + ""</div>"";
  }}
  lines += ""<div class='sig-line' style='color:#888'>最新收盤 "" + s.latestClose + ""（"" + s.latestDate + ""）</div>"";
  return ""<div class='sig-card "" + cls + ""'><button class='sig-del' onclick=\""delWatch('"" + s.id + ""')\"">✕</button>"" +
    ""<div class='sig-head'><span class='sig-name'>"" + s.code + "" "" + (s.name || '') + ""</span></div>"" +
    ""<div class='sig-rule'>"" + rule + ""</div>"" + badge + lines + ""</div>"";
}}

async function delWatch(id) {{
  if (!confirm('確定移除此關注？')) return;
  await fetch('/api/stocks/watchlist/' + id, {{ method: 'DELETE' }});
  loadSigs();
}}

document.getElementById('wadd').addEventListener('click', async () => {{
  const code = document.getElementById('wcode').value.trim();
  const msg = document.getElementById('wmsg');
  if (!code) {{ document.getElementById('wcode').focus(); return; }}
  const body = {{
    code,
    dir: document.getElementById('wdir').value,
    n: +document.getElementById('wn').value,
    holdDays: +document.getElementById('whold').value
  }};
  const r = await fetch('/api/stocks/watchlist', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify(body) }});
  if (r.ok) {{
    msg.innerHTML = ""<div class='alert ok'>已加入關注</div>"";
    document.getElementById('wcode').value = '';
    loadSigs();
  }} else {{
    const e = await r.json().catch(() => ({{}}));
    msg.innerHTML = ""<div class='alert err'>"" + (e.error || '加入失敗') + ""</div>"";
  }}
  setTimeout(() => msg.innerHTML = '', 2500);
}});
document.getElementById('wcode').addEventListener('keydown', e => {{ if (e.key === 'Enter') document.getElementById('wadd').click(); }});

loadSigs();
</script>
</body></html>";
    }

    // ── 批量回測（一次分析一群股票，找出最值得關注的個股） ──────────────────────────
    static async Task BatchPage(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildBatchHtml());
    }

    // 以 twse 即時報價介面一次查多檔股名（分批，避免單次網址過長）
    static async Task<Dictionary<string, string>> GetNamesBulk(List<string> codes, IHttpClientFactory httpFactory)
    {
        var names = new Dictionary<string, string>();
        try
        {
            var client = httpFactory.CreateClient("twse");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://mis.twse.com.tw/");
            for (int i = 0; i < codes.Count; i += 20)
            {
                var chunk = codes.Skip(i).Take(20).ToList();
                var ex = string.Join("|", chunk.Select(c => $"tse_{c}.tw"));
                try
                {
                    var resp = await client.GetStringAsync($"/stock/api/getStockInfo.jsp?ex_ch={ex}&json=1&delay=0");
                    using var doc = JsonDocument.Parse(resp);
                    if (doc.RootElement.TryGetProperty("msgArray", out var arr))
                        foreach (var it in arr.EnumerateArray())
                        {
                            var c = it.TryGetProperty("c", out var ce) ? ce.GetString() : null;
                            var nm = it.TryGetProperty("n", out var ne) ? ne.GetString() : null;
                            if (!string.IsNullOrEmpty(c) && !string.IsNullOrEmpty(nm)) names[c] = nm;
                        }
                }
                catch { }
            }
        }
        catch { }
        return names;
    }

    // 對單一股票跑完整組合網格，回傳勝率最高（樣本 ≥ minTrades）的策略
    static async Task<BatchOne> ComputeBatchOne(string code, int years, IHttpClientFactory httpFactory)
    {
        try
        {
            var client = httpFactory.CreateClient("finmind");
            var end = DateTime.Today;
            var start = end.AddYears(-years);
            var resp = await client.GetStringAsync(
                $"/api/v4/data?dataset=TaiwanStockPrice&data_id={code}&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.GetArrayLength() == 0)
                return new BatchOne(code, 0, "查無資料", null, 0, 0, 0, 0);

            var closes = new List<double>();
            foreach (var it in arr.EnumerateArray())
            {
                if (!it.TryGetProperty("close", out var c) || c.ValueKind != JsonValueKind.Number) continue;
                var cl = c.GetDouble();
                if (cl > 0) closes.Add(cl);
            }
            int n = closes.Count;
            if (n < 60) return new BatchOne(code, n, "資料不足", null, 0, 0, 0, 0);

            var down = new int[n];
            var up = new int[n];
            for (int i = 1; i < n; i++)
            {
                if (closes[i] < closes[i - 1]) { down[i] = down[i - 1] + 1; up[i] = 0; }
                else if (closes[i] > closes[i - 1]) { up[i] = up[i - 1] + 1; down[i] = 0; }
            }

            int[] holds = { 1, 3, 5, 10, 20, 60 };
            int[] streakNs = { 1, 2, 3, 4, 5 };
            const int minTrades = 20;
            string bestLabel = "", bestDir = "down"; int bestHold = 0, bestTrades = 0, bestN = 0;
            double bestWin = -1, bestRet = double.NegativeInfinity;

            foreach (var (dir, dl) in new[] { ("down", "連跌"), ("up", "連漲") })
            {
                var streak = dir == "down" ? down : up;
                foreach (var nn in streakNs)
                    foreach (var hold in holds)
                    {
                        int trades = 0, wins = 0; double sum = 0;
                        for (int i = 0; i + hold < n; i++)
                        {
                            if (streak[i] != nn) continue;
                            double ret = (closes[i + hold] - closes[i]) / closes[i] * 100.0;
                            trades++; sum += ret; if (ret > 0) wins++;
                        }
                        if (trades < minTrades) continue;
                        double wr = (double)wins / trades * 100.0, ar = sum / trades;
                        if (wr > bestWin || (Math.Abs(wr - bestWin) < 1e-9 && ar > bestRet))
                        {
                            bestWin = wr; bestRet = ar; bestLabel = $"{dl}{nn}天後買入";
                            bestHold = hold; bestTrades = trades; bestDir = dir; bestN = nn;
                        }
                    }
            }

            if (bestWin < 0) return new BatchOne(code, n, "無足夠樣本的策略", null, 0, 0, 0, 0);
            return new BatchOne(code, n, null, bestLabel, bestHold,
                Math.Round(bestWin, 1), Math.Round(bestRet, 2), bestTrades, bestDir, bestN);
        }
        catch
        {
            return new BatchOne(code, 0, "取得失敗", null, 0, 0, 0, 0);
        }
    }

    static async Task<IResult> RunBatch(string codes, int? years, IHttpClientFactory httpFactory)
    {
        var list = (codes ?? "")
            .Split(new[] { ',', ' ', '\n', '\r', '\t', ';', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().Take(60).ToList();
        if (list.Count == 0) return Results.Json(new { error = "請輸入或選擇股號" });
        int yrs = Math.Clamp(years ?? 10, 1, 20);

        var sem = new SemaphoreSlim(6);
        var tasks = list.Select(async c =>
        {
            await sem.WaitAsync();
            try { return await ComputeBatchOne(c, yrs, httpFactory); }
            finally { sem.Release(); }
        });
        var batch = await Task.WhenAll(tasks);
        var names = await GetNamesBulk(list, httpFactory);

        var results = batch.Select(b => new
        {
            b.Code,
            name = names.GetValueOrDefault(b.Code, ""),
            b.DataPoints,
            b.Error,
            best = b.Error == null
                ? new { entryLabel = b.EntryLabel, dir = b.Dir, n = b.N, holdDays = b.HoldDays, winRate = b.WinRate, avgReturn = b.AvgReturn, trades = b.Trades }
                : null
        });
        return Results.Json(new { years = yrs, count = list.Count, results });
    }

    static string BuildBatchHtml()
    {
        return $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>批量回測</title>
<style>{CommonCss}
.bt-form{{display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end;background:#fff;border-radius:8px;padding:16px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08);margin-bottom:16px}}
.bt-form .field{{margin-bottom:0}}
.bt-form select,.bt-form input{{width:auto}}
.bt-form textarea{{width:100%;min-height:56px;font-size:.9rem;font-family:inherit}}
.field-wide{{flex:1 1 100%}}
.hint{{font-size:.82rem;color:#888;margin:0 0 16px}}
.meta{{font-size:.85rem;color:#666;margin-bottom:12px}}
#bat-table th{{cursor:pointer;user-select:none;text-align:right;white-space:nowrap}}
#bat-table th:first-child,#bat-table th:nth-child(2),#bat-table th:nth-child(3){{text-align:left}}
#bat-table td{{text-align:right}}
#bat-table td:first-child,#bat-table td:nth-child(2),#bat-table td:nth-child(3){{text-align:left}}
#bat-table th.sorted::after{{content:' ▾';font-size:.7rem;color:#0055cc}}
#bat-table th.sorted.asc::after{{content:' ▴'}}
#bat-table tbody tr:hover td{{background:#eef4ff}}
.rank{{color:#888;font-weight:600}}
.top3 td:first-child{{color:#c0392b;font-weight:700}}
.stk-link{{color:#0055cc;text-decoration:none}}
.stk-link:hover{{text-decoration:underline}}
.pos{{color:#c00}}.neg{{color:#080}}
.spin{{color:#999;font-style:italic}}
.err-txt{{color:#c0392b;font-size:.85rem}}
</style></head><body>
{NavBar("batch")}
<h1 style='margin:0 0 6px'>📚 批量回測・找出最值得關注的個股</h1>
<p class='hint'>選一個股票群組（或自訂股號清單），一次對每檔跑完整回測，取出各自「勝率最高」的策略後排名，快速篩出值得深入研究的標的。每列可一鍵把該策略加入關注，或轉到單檔勝率回測看更細的數據。</p>

<div class='bt-form'>
  <div class='field'><label for='preset'>股票群組</label>
    <select id='preset'>
      <option value='finance'>金融官股</option>
      <option value='t50' selected>0050 成分股</option>
      <option value='t50top'>台灣50 權值前15</option>
      <option value='custom'>自訂（自行輸入）</option>
    </select>
  </div>
  <div class='field'><label for='years'>統計期間</label>
    <select id='years'><option value='10' selected>近 10 年</option><option value='5'>近 5 年</option><option value='3'>近 3 年</option></select>
  </div>
  <div class='field'><label for='amount'>投資金額 (元)</label><input id='amount' type='number' value='5000000' min='0' step='100000' style='width:120px'></div>
  <div class='field'><button class='btn' id='go'>開始批量回測</button></div>
  <div class='field field-wide'><label for='codes'>股號清單（可編輯，逗號或空白分隔）</label><textarea id='codes'></textarea></div>
</div>
<div id='status'></div>

<div id='result' style='display:none'>
  <div class='meta' id='meta'></div>
  <div class='table-wrap'>
    <table id='bat-table'>
      <thead><tr>
        <th data-k='rank'>#</th>
        <th data-k='code'>股號</th>
        <th data-k='name'>股名</th>
        <th data-k='bestStrategy'>最佳買入策略</th>
        <th data-k='holdDays'>持有天數</th>
        <th data-k='winRate'>勝率</th>
        <th data-k='avgReturn'>平均報酬率</th>
        <th data-k='profitPer'>平均每次獲利</th>
        <th data-k='trades'>訊號次數</th>
        <th data-k='act'>操作</th>
      </tr></thead>
      <tbody></tbody>
    </table>
  </div>
  <p class='hint' style='margin-top:12px'>※ 每檔股票取「訊號次數 ≥ 20 且勝率最高」的策略；預設依勝率排序，可點欄位標題改排序。「平均每次獲利」= 投資金額 × 平均報酬率。「🔔 關注」會把該檔的最佳策略（進場規則＋持有天數）加入關注訊號頁；「📊 細節」開啟單檔勝率回測。報酬率未計手續費與交易稅，僅供研究參考，非投資建議。</p>
</div>

<script>
const PRESETS = {{
  finance: '2880 2892 5880 2886 2834 2801',
  t50: '2330 2317 2454 2308 2382 2891 2881 2882 2412 2303 2886 2884 3711 2357 2885 2892 2880 2890 2883 2887 1216 2002 2207 3008 2379 3034 2345 2301 4938 1303 1301 2603 2609 2615 5871 2912 1101 2327 3037 2395 6505 5876 6669 3231 2377 4904 2474 3661 2357 6446',
  t50top: '2330 2317 2454 2308 2382 2891 2881 2882 2412 2303 2886 2884 3711 2357 2885',
  custom: ''
}};
let _rows = [], _sortK = 'winRate', _sortAsc = false;
const _added = new Set();

const presetEl = document.getElementById('preset');
const codesEl = document.getElementById('codes');
function fillPreset() {{ if (presetEl.value !== 'custom') codesEl.value = PRESETS[presetEl.value]; }}
presetEl.addEventListener('change', fillPreset);
fillPreset();
codesEl.addEventListener('input', () => {{ presetEl.value = 'custom'; }});
document.getElementById('amount').addEventListener('input', () => {{ if (_rows.length) renderTable(); }});
document.getElementById('go').addEventListener('click', run);

function getAmt() {{ const v = parseFloat(document.getElementById('amount').value); return isFinite(v) && v > 0 ? v : 0; }}
function money(v) {{ return (v < 0 ? '-$' : '$') + Math.abs(Math.round(v)).toLocaleString('en-US'); }}
function fmt(v) {{ return (v > 0 ? '+' : '') + v.toFixed(2) + '%'; }}
function cls(v) {{ return v > 0 ? 'pos' : (v < 0 ? 'neg' : ''); }}

async function run() {{
  const codes = codesEl.value.trim();
  if (!codes) {{ codesEl.focus(); return; }}
  const years = document.getElementById('years').value;
  const st = document.getElementById('status');
  const res = document.getElementById('result');
  res.style.display = 'none';
  const nApprox = codes.split(/[^0-9A-Za-z]+/).filter(Boolean).length;
  st.innerHTML = ""<p class='spin'>批量回測中，正在分析 "" + nApprox + "" 檔股票…（檔數多時約需數十秒）</p>"";
  document.getElementById('go').disabled = true;
  try {{
    const r = await fetch('/api/stocks/batch?years=' + years + '&codes=' + encodeURIComponent(codes));
    const d = await r.json();
    if (d.error) {{ st.innerHTML = ""<div class='alert err'>"" + d.error + ""</div>""; return; }}
    st.innerHTML = '';
    _rows = d.results.map(x => ({{
      code: x.code, name: x.name, error: x.error, dataPoints: x.dataPoints,
      bestStrategy: x.best ? x.best.entryLabel : '',
      dir: x.best ? x.best.dir : '', n: x.best ? x.best.n : 0,
      holdDays: x.best ? x.best.holdDays : 0,
      winRate: x.best ? x.best.winRate : -1,
      avgReturn: x.best ? x.best.avgReturn : 0,
      trades: x.best ? x.best.trades : 0
    }}));
    const ok = _rows.filter(r => !r.error).length;
    document.getElementById('meta').textContent =
      '共分析 ' + d.count + ' 檔，成功 ' + ok + ' 檔（近 ' + d.years + ' 年）；依勝率由高至低排序。';
    _sortK = 'winRate'; _sortAsc = false;
    renderTable();
    res.style.display = 'block';
  }} catch (e) {{
    st.innerHTML = ""<div class='alert err'>批量回測失敗："" + e + ""</div>"";
  }} finally {{
    document.getElementById('go').disabled = false;
  }}
}}

function renderTable() {{
  const amt = getAmt();
  const rows = _rows.slice().sort((a, b) => {{
    if (a.error && !b.error) return 1;
    if (!a.error && b.error) return -1;
    let x = a[_sortK], y = b[_sortK];
    if (typeof x === 'string') {{ x = x || ''; y = y || ''; return _sortAsc ? x.localeCompare(y) : y.localeCompare(x); }}
    if (x < y) return _sortAsc ? -1 : 1;
    if (x > y) return _sortAsc ? 1 : -1;
    return 0;
  }});
  const tb = document.querySelector('#bat-table tbody');
  tb.innerHTML = rows.map((r, i) => {{
    if (r.error) {{
      return ""<tr><td class='rank'>—</td><td>"" + r.code + ""</td><td>"" + (r.name || '') +
        ""</td><td colspan='6' class='err-txt'>"" + r.error + ""</td>"" +
        ""<td><button class='btn btn-sm btn-outline bat-bt' data-code='"" + r.code + ""'>📊 細節</button></td></tr>"";
    }}
    const profit = amt * r.avgReturn / 100;
    const top = i < 3 ? "" class='top3'"" : '';
    const wkey = r.code + '|' + r.dir + '|' + r.n + '|' + r.holdDays;
    const added = _added.has(wkey);
    const watchBtn = added
      ? ""<button class='btn btn-sm' disabled style='background:#8a9;'>✓ 已加入</button>""
      : ""<button class='btn btn-sm bat-watch' data-key='"" + wkey + ""' data-code='"" + r.code +
        ""' data-name='"" + (r.name || '') + ""' data-dir='"" + r.dir + ""' data-n='"" + r.n +
        ""' data-hold='"" + r.holdDays + ""'>🔔 關注</button>"";
    return ""<tr"" + top + ""><td class='rank'>"" + (i + 1) + ""</td>"" +
      ""<td>"" + r.code + ""</td>"" +
      ""<td>"" + (r.name || '') + ""</td>"" +
      ""<td>"" + r.bestStrategy + ""</td>"" +
      ""<td>"" + r.holdDays + ""</td>"" +
      ""<td>"" + r.winRate.toFixed(1) + ""%</td>"" +
      ""<td class='"" + cls(r.avgReturn) + ""'>"" + fmt(r.avgReturn) + ""</td>"" +
      ""<td class='"" + cls(profit) + ""'>"" + money(profit) + ""</td>"" +
      ""<td>"" + r.trades + ""</td>"" +
      ""<td class='actions' style='justify-content:flex-end'>"" + watchBtn +
      "" <button class='btn btn-sm btn-outline bat-bt' data-code='"" + r.code + ""'>📊 細節</button></td></tr>"";
  }}).join('');
  document.querySelectorAll('#bat-table th').forEach(th => {{
    th.classList.toggle('sorted', th.dataset.k === _sortK);
    th.classList.toggle('asc', th.dataset.k === _sortK && _sortAsc);
  }});
}}

document.querySelectorAll('#bat-table th').forEach(th => {{
  th.addEventListener('click', () => {{
    const k = th.dataset.k;
    if (k === 'rank' || k === 'act') return;
    if (_sortK === k) _sortAsc = !_sortAsc;
    else {{ _sortK = k; _sortAsc = (k === 'code' || k === 'name' || k === 'bestStrategy'); }}
    renderTable();
  }});
}});

document.querySelector('#bat-table tbody').addEventListener('click', e => {{
  const b = e.target.closest('button');
  if (!b) return;
  if (b.classList.contains('bat-bt')) {{ window.open('/stocks/backtest?code=' + b.dataset.code, '_blank'); return; }}
  if (b.classList.contains('bat-watch')) addWatch(b);
}});

async function addWatch(btn) {{
  const d = btn.dataset;
  btn.disabled = true; btn.textContent = '加入中…';
  try {{
    const r = await fetch('/api/stocks/watchlist', {{
      method: 'POST', headers: {{ 'Content-Type': 'application/json' }},
      body: JSON.stringify({{ code: d.code, name: d.name, dir: d.dir, n: +d.n, holdDays: +d.hold }})
    }});
    if (r.ok || r.status === 409) {{
      _added.add(d.key);
      btn.textContent = r.status === 409 ? '✓ 已在關注' : '✓ 已加入';
      btn.style.background = '#8a9';
    }} else {{
      const e = await r.json().catch(() => ({{}}));
      btn.disabled = false; btn.textContent = '🔔 關注';
      alert(e.error || '加入失敗');
    }}
  }} catch (err) {{
    btn.disabled = false; btn.textContent = '🔔 關注';
    alert('加入失敗：' + err);
  }}
}}
</script>
</body></html>";
    }
}

// ── Models ───────────────────────────────────────────────────────────────────

public record BatchOne(string Code, int DataPoints, string? Error,
    string? EntryLabel, int HoldDays, double WinRate, double AvgReturn, int Trades,
    string? Dir = null, int N = 0);

public record DivEntry(
    [property: JsonPropertyName("cashDiv")] decimal CashDiv,
    [property: JsonPropertyName("stockDiv")] decimal StockDiv,
    [property: JsonPropertyName("cashExDate")] string CashExDate,
    [property: JsonPropertyName("stockExDate")] string StockExDate,
    [property: JsonPropertyName("cashPayDate")] string CashPayDate,
    [property: JsonPropertyName("stockPayDate")] string StockPayDate = "");

// 手動維護的配息/配股資訊（含四個日期），可覆蓋自動抓取結果並免除即時抓取
public record DividendManualRequest(
    string Code, int Year, decimal CashDiv, decimal StockDiv,
    string? CashExDate, string? StockExDate, string? CashPayDate, string? StockPayDate);

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

public record WatchItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dir")] string Dir,
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("holdDays")] int HoldDays,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

public record WatchRequest(string Code, string? Name, string Dir, int N, int HoldDays);

public record EditTradeRequest(
    string StockCode,
    string StockName,
    string TradeType,
    decimal Price,
    long Shares,
    string? Date,
    string? OrderNo,
    string? AccountType);
