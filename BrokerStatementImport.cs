using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// 券商對帳單（CSV）匯入：
// 瀏覽器端把 CSV 拆成表格（string[][]）送來，這裡負責找出表頭、對應欄位、轉成標準交易，
// 與現有交易紀錄比對產生預覽，使用者勾選確認後才寫入。
// 各家券商欄位不同：先套「已儲存的對應」（依表頭特徵），沒有就以欄名同義詞自動辨識；
// 仍缺必要欄位時由使用者在頁面上手動指定，套用後存成對應，同一格式下次自動套用。
public static class BrokerStatementImport
{
    static readonly string ProfilesFile = Path.Combine(
        Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"), "broker_profiles.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    static readonly Regex CodePattern = new(@"^\d{4,6}[A-Z]?$");

    // 欄位 → 常見欄名（先找完全相同，再找包含；長的同義詞優先，避免「股數」搶走「成交股數」）
    static readonly (string Field, string[] Aliases)[] FieldAliases =
    [
        ("date", ["成交日期", "交易日期", "成交日", "交易日", "日期"]),
        ("code", ["股票代號", "證券代號", "商品代號", "股票代碼", "股號", "代號"]),
        ("name", ["股票名稱", "證券名稱", "商品名稱", "股名", "名稱"]),
        ("type", ["買賣別", "交易別", "交易類別", "委託類別", "買賣"]),
        ("shares", ["成交股數", "成交數量", "股數", "數量"]),
        ("price", ["成交價格", "成交單價", "成交價", "單價", "價格"]),
        ("commission", ["手續費"]),
        ("tax", ["交易稅", "證交稅", "稅額"]),
        ("net", ["淨收付金額", "客戶應收付", "應收付金額", "淨收付", "應收付", "收付金額", "淨額"]),
        ("orderNo", ["委託書號", "委託單號", "委託序號", "書號", "單號"]),
    ];
    static readonly string[] Required = ["date", "type", "shares", "price"];

    // 已知券商：表頭同時含這些欄名即判定，只用來顯示名稱與預設帳戶
    static readonly (string Broker, string[] Headers)[] KnownBrokers =
    [
        ("國泰證券", ["股名", "淨收付金額", "委託書號", "融資金額/券擔保品"]),
    ];

    public static void MapRoutes(WebApplication app)
    {
        app.MapPost("/api/stocks/trade/statement/preview", Preview);
        app.MapPost("/api/stocks/trade/statement/apply", Apply);
    }

    // ── 1. 表頭與欄位對應 ─────────────────────────────────────────────────

    static string Clean(string? s) => (s ?? "").Replace("﻿", "").Trim();

    static string Signature(List<string> headers) => string.Join("|", headers.Select(Clean));

    static Dictionary<string, int> AutoMap(List<string> headers)
    {
        var map = new Dictionary<string, int>();
        var taken = new HashSet<int>();
        foreach (var exact in new[] { true, false })
            foreach (var (field, aliases) in FieldAliases)
            {
                if (map.ContainsKey(field)) continue;
                foreach (var alias in aliases)
                {
                    var idx = Enumerable.Range(0, headers.Count).FirstOrDefault(i => !taken.Contains(i)
                        && (exact ? Clean(headers[i]) == alias : Clean(headers[i]).Contains(alias)), -1);
                    if (idx < 0) continue;
                    map[field] = idx; taken.Add(idx);
                    break;
                }
            }
        return map;
    }

    const int HeaderScanRows = 15;

    // 前 15 列中：先找與已儲存對應的表頭完全相同的列，再找能對應到最多欄位的列；
    // 都認不出時取第一個有 3 格以上內容的列，讓使用者手動指定欄位
    static int FindHeaderRow(List<List<string>> table, List<BrokerProfile> profiles)
    {
        var n = Math.Min(HeaderScanRows, table.Count);
        var sigs = profiles.Select(p => p.Signature).ToHashSet();
        for (int r = 0; r < n; r++)
            if (sigs.Contains(Signature(table[r]))) return r;
        int best = -1, bestScore = 0;
        for (int r = 0; r < n; r++)
        {
            var score = AutoMap(table[r]).Count;
            if (score > bestScore) { best = r; bestScore = score; }
        }
        if (bestScore >= 4) return best;
        for (int r = 0; r < n; r++)
            if (table[r].Count(c => Clean(c) != "") >= 3) return r;
        return -1;
    }

    static List<BrokerProfile> LoadProfiles() =>
        File.Exists(ProfilesFile) ? JsonSerializer.Deserialize<List<BrokerProfile>>(File.ReadAllText(ProfilesFile), JsonOpts) ?? [] : [];

    static void SaveProfile(BrokerProfile p)
    {
        var all = LoadProfiles();
        all.RemoveAll(x => x.Signature == p.Signature);
        all.Add(p);
        File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(all, JsonOpts));
    }

    // 對應優先順序：使用者這次手動指定 ＞ 已儲存的對應 ＞ 依欄名自動辨識
    static (int HeaderRow, List<string> Headers, Dictionary<string, int> Map, string Source, string Broker, string Account)
        ResolveMapping(BrokerStatementRequest req)
    {
        var profiles = LoadProfiles();
        var headerRow = req.HeaderRow is int h && h >= 0 && h < req.Table.Count ? h : FindHeaderRow(req.Table, profiles);
        if (headerRow < 0) return (-1, [], [], "none", "", "");
        var headers = req.Table[headerRow].Select(Clean).ToList();
        var sig = Signature(headers);
        var broker = KnownBrokers.FirstOrDefault(b => b.Headers.All(headers.Contains)).Broker ?? "";
        var saved = profiles.FirstOrDefault(p => p.Signature == sig);

        if (req.Mapping != null)
        {
            var manual = req.Mapping.Where(kv => kv.Value >= 0 && kv.Value < headers.Count).ToDictionary(kv => kv.Key, kv => kv.Value);
            return (headerRow, headers, manual, "manual", saved?.Broker ?? broker, saved?.AccountType ?? "");
        }
        if (saved != null)
        {
            // 對應存的是欄名，欄位順序變了也找得到
            var map = saved.Columns.Select(kv => (kv.Key, Idx: headers.IndexOf(kv.Value))).Where(x => x.Idx >= 0).ToDictionary(x => x.Key, x => x.Idx);
            return (headerRow, headers, map, "saved", saved.Broker, saved.AccountType);
        }
        return (headerRow, headers, AutoMap(headers), broker != "" ? "builtin" : "auto", broker, "");
    }

    // ── 2. 值的轉換 ──────────────────────────────────────────────────────

    // 2026/10/8、2026-10-08、20261008、115/10/08、1151008 → 2026-10-08
    static string? ParseDate(string s)
    {
        var parts = Regex.Matches(s, @"\d+").Select(m => m.Value).ToList();
        int y, m, d;
        if (parts.Count >= 3) { y = int.Parse(parts[0]); m = int.Parse(parts[1]); d = int.Parse(parts[2]); }
        else if (parts.Count == 1 && parts[0].Length is 7 or 8)
        {
            var t = parts[0];
            y = int.Parse(t[..^4]); m = int.Parse(t[^4..^2]); d = int.Parse(t[^2..]);
        }
        else return null;
        if (y < 1000) y += 1911;
        try { return new DateTime(y, m, d).ToString("yyyy-MM-dd"); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static decimal? ParseNum(string s)
    {
        s = s.Replace(",", "").Replace("$", "").Replace(" ", "").Trim();
        if (s.StartsWith('(') && s.EndsWith(')')) s = "-" + s[1..^1];
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // 只匯入現股；融資融券與借券的成本計算不同，先略過並提示
    static string? ParseType(string s)
    {
        s = s.Trim();
        if (s.Contains("資") || s.Contains("券") || s.Contains("借")) return null;
        if (s.Contains("配股")) return "配股";
        if (s.Contains("買") || s.Equals("B", StringComparison.OrdinalIgnoreCase)) return "現買";
        if (s.Contains("賣") || s.Equals("S", StringComparison.OrdinalIgnoreCase)) return "現賣";
        return null;
    }

    static string NormName(string s) => s.Replace(" ", "").Replace("　", "").Replace("臺", "台").Trim();

    // 證交所＋櫃買上市櫃清單（股名 → 股號），只在交易紀錄裡查不到時才抓一次
    static Dictionary<string, string>? _stockList;
    static async Task<Dictionary<string, string>> StockList(IHttpClientFactory httpFactory)
    {
        if (_stockList != null) return _stockList;
        var list = new Dictionary<string, string>();
        try
        {
            var json = await httpFactory.CreateClient("finmind").GetStringAsync("/api/v4/data?dataset=TaiwanStockInfo");
            using var doc = JsonDocument.Parse(json);
            foreach (var d in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = d.GetProperty("stock_id").GetString() ?? "";
                var name = NormName(d.GetProperty("stock_name").GetString() ?? "");
                if (CodePattern.IsMatch(id) && name != "") list.TryAdd(name, id);
            }
            _stockList = list;
        }
        catch { }
        return list;
    }

    // ── 3. 預覽（比對現有交易）────────────────────────────────────────────

    static async Task<IResult> Preview(BrokerStatementRequest req, IHttpClientFactory httpFactory)
    {
        if (req.Table == null || req.Table.Count == 0) return Results.BadRequest("檔案是空的");
        var result = await BuildPreview(req, StocksModule.LoadTrades(), httpFactory);
        // 手動指定且必要欄位齊全時存成對應，同一格式下次自動套用
        if (req.Mapping != null && result.Missing.Count == 0 && result.HeaderRow >= 0)
            SaveProfile(new BrokerProfile(Signature(result.Headers), result.Broker,
                result.Mapping.ToDictionary(kv => kv.Key, kv => result.Headers[kv.Value]), result.SuggestedAccount));
        return Results.Json(result, WebJson);
    }

    static async Task<BrokerPreview> BuildPreview(BrokerStatementRequest req, List<Trade> trades, IHttpClientFactory httpFactory)
    {
        var (headerRow, headers, map, source, broker, account) = ResolveMapping(req);
        var missing = Required.Where(f => !map.ContainsKey(f)).ToList();
        if (!map.ContainsKey("code") && !map.ContainsKey("name")) missing.Add("code");
        var warnings = new List<string>();
        if (headerRow < 0)
            return new BrokerPreview(-1, [], map, [.. Required, "code"], "none", "", [], ["檔案裡找不到表格，請確認是券商的成交對帳單 CSV"], "");
        if (missing.Count > 0)
            return new BrokerPreview(headerRow, headers, map, missing, source, broker, [], warnings, account);

        string Cell(List<string> row, string field) =>
            map.TryGetValue(field, out var i) && i < row.Count ? Clean(row[i]) : "";

        var byName = trades.GroupBy(t => NormName(t.StockName)).ToDictionary(g => g.Key, g => g.Last().StockCode);
        var parsed = new List<(int Index, string Date, string Code, string Name, string Type, long Shares, decimal Price,
            decimal? Commission, decimal? Tax, decimal? Net, string OrderNo)>();
        for (int r = headerRow + 1; r < req.Table.Count; r++)
        {
            var row = req.Table[r];
            if (row.All(c => Clean(c) == "")) continue;
            var date = ParseDate(Cell(row, "date"));
            var type = ParseType(Cell(row, "type"));
            var shares = ParseNum(Cell(row, "shares"));
            var price = ParseNum(Cell(row, "price"));
            var name = Cell(row, "name");
            // 合計、小計等列沒有日期，不提示
            if (date == null) continue;
            if (type == null) { warnings.Add($"第 {r + 1} 列「{Cell(row, "type")}」不是現股買賣，已略過"); continue; }
            if (shares is not > 0 || price is not >= 0) { warnings.Add($"第 {r + 1} 列股數或成交價讀不到，已略過"); continue; }

            var code = Cell(row, "code").ToUpperInvariant();
            if (req.Codes?.TryGetValue(r, out var given) == true) code = given.Trim().ToUpperInvariant();
            if (!CodePattern.IsMatch(code)) code = byName.GetValueOrDefault(NormName(name)) ?? "";
            if (code == "" && name != "") code = (await StockList(httpFactory)).GetValueOrDefault(NormName(name)) ?? "";
            parsed.Add((r, date, code, name, type, (long)shares.Value, price.Value,
                ParseNum(Cell(row, "commission")), ParseNum(Cell(row, "tax")), ParseNum(Cell(row, "net")), Cell(row, "orderNo")));
        }

        // 每筆既有交易只能抵銷一列，同一張委託分多筆成交（股數價格都相同）也能正確計數
        var used = new HashSet<int>();
        bool SameOrder(Trade t, string orderNo) =>
            orderNo == "" || t.OrderNo == "" || string.Equals(t.OrderNo, orderNo, StringComparison.OrdinalIgnoreCase);
        bool SameStock(Trade t, string code, string name) =>
            code != "" ? t.StockCode == code : NormName(t.StockName) == NormName(name);

        var items = new List<BrokerItem>();
        var names = trades.GroupBy(t => t.StockCode).ToDictionary(g => g.Key, g => g.Last().StockName);
        foreach (var p in parsed)
        {
            int Find(Func<Trade, bool> pred) =>
                Enumerable.Range(0, trades.Count).FirstOrDefault(i => !used.Contains(i) && pred(trades[i]), -1);
            var exact = Find(t => t.Date == p.Date && t.TradeType == p.Type && SameStock(t, p.Code, p.Name)
                && t.Shares == p.Shares && t.Price == p.Price && SameOrder(t, p.OrderNo));
            string status, note = "";
            if (exact >= 0) { used.Add(exact); status = "duplicate"; }
            else
            {
                // 同日同股同方向但股數或價格不同（手動登錄打錯、或同一委託書號）→ 疑似，交給使用者判斷
                var near = Find(t => t.Date == p.Date && t.TradeType == p.Type && SameStock(t, p.Code, p.Name)
                    && ((p.OrderNo != "" && string.Equals(t.OrderNo, p.OrderNo, StringComparison.OrdinalIgnoreCase))
                        || (t.OrderNo == "" && (t.Shares == p.Shares || t.Price == p.Price))));
                if (near >= 0)
                {
                    var t = trades[near];
                    status = "suspect";
                    note = $"系統已有同日 {t.Shares:N0} 股 @ {t.Price}" + (t.OrderNo != "" ? $"（書號 {t.OrderNo}）" : "") + "，可能是同一筆";
                }
                else status = "new";
            }
            if (p.Code == "") note = (note + " 查不到股號，請填寫").Trim();
            var name = names.GetValueOrDefault(p.Code) ?? new string(p.Name.Where(c => c is not ('<' or '>' or '"' or '\'' or '&')).Take(30).ToArray());
            items.Add(new BrokerItem(p.Index, p.Date, p.Code, name, p.Type, p.Shares, p.Price,
                p.Commission, p.Tax, p.Net, p.OrderNo, status, note));
        }

        if (account == "")
            account = trades.Where((t, i) => used.Contains(i) && t.AccountType != "").GroupBy(t => t.AccountType)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        return new BrokerPreview(headerRow, headers, map, [], source, broker, items, warnings, account);
    }

    // ── 4. 套用勾選的列 ──────────────────────────────────────────────────

    static async Task<IResult> Apply(BrokerStatementRequest req, IHttpClientFactory httpFactory)
    {
        if (req.Table == null || req.Rows == null) return Results.BadRequest("沒有要匯入的交易");
        if (string.IsNullOrWhiteSpace(req.AccountType)) return Results.BadRequest("請填寫帳戶");
        if (StocksModule.ExcelLocked()) return Results.BadRequest("請先關閉 Excel 檔案「股票總張數統計.xlsx」再匯入");
        var selected = req.Rows.ToHashSet();

        await StocksModule.Lock.WaitAsync();
        try
        {
            // 以寫入當下的交易紀錄重新比對，重複送出或別處剛新增過也不會重複匯入
            var trades = StocksModule.LoadTrades();
            var preview = await BuildPreview(req, trades, httpFactory);
            if (preview.Missing.Count > 0) return Results.BadRequest("欄位對應不完整");
            var added = new List<Trade>();
            var skipped = 0;
            foreach (var it in preview.Items.Where(i => selected.Contains(i.Index)))
            {
                if (it.Status == "duplicate") { skipped++; continue; }
                if (it.Code == "") return Results.BadRequest($"第 {it.Index + 1} 列 {it.Name} 請填寫股號");
                var t = StocksModule.BuildTrade(Guid.NewGuid().ToString("N")[..8], it.Name, it.Code, it.Date,
                    it.Shares, it.Price, it.TradeType, it.OrderNo, req.AccountType.Trim());
                // 券商有折讓，手續費、稅、淨收付以對帳單為準
                if (it.Commission is decimal c) t = t with { Commission = (long)Math.Abs(c) };
                if (it.Tax is decimal x) t = t with { Tax = (long)Math.Abs(x) };
                t = it.Net is decimal n
                    ? t with { NetAmount = it.TradeType == "現買" ? -(long)Math.Abs(n) : (long)Math.Abs(n) }
                    : t with { NetAmount = it.TradeType == "現買" ? -(t.Cost + t.Commission) : t.Cost - t.Commission - t.Tax };
                added.Add(t);
            }
            if (added.Count > 0)
            {
                trades.AddRange(added);
                StocksModule.SaveTrades(trades);
                if (StocksModule._enableExcel)
                    foreach (var t in added) StocksModule.AppendTradeToExcel(t);
            }
            // 記住這個格式慣用的帳戶
            if (preview.HeaderRow >= 0)
                SaveProfile(new BrokerProfile(Signature(preview.Headers), preview.Broker,
                    preview.Mapping.ToDictionary(kv => kv.Key, kv => preview.Headers[kv.Value]), req.AccountType.Trim()));
            return Results.Json(new { imported = added.Count, skipped }, WebJson);
        }
        finally { StocksModule.Lock.Release(); }
    }
}

// Mapping：欄位 → 欄號（手動指定時才傳）；Codes：列號 → 使用者補填的股號；Rows：要匯入的列號
public record BrokerStatementRequest(
    List<List<string>> Table, int? HeaderRow, Dictionary<string, int>? Mapping,
    Dictionary<int, string>? Codes, List<int>? Rows, string? AccountType);

// Columns：欄位 → 欄名
public record BrokerProfile(string Signature, string Broker, Dictionary<string, string> Columns, string AccountType);

// status：new 新交易｜suspect 疑似已登錄｜duplicate 已存在
public record BrokerItem(
    int Index, string Date, string Code, string Name, string TradeType, long Shares, decimal Price,
    decimal? Commission, decimal? Tax, decimal? Net, string OrderNo, string Status, string Note);

public record BrokerPreview(
    int HeaderRow, List<string> Headers, Dictionary<string, int> Mapping, List<string> Missing,
    string Source, string Broker, List<BrokerItem> Items, List<string> Warnings, string SuggestedAccount);
