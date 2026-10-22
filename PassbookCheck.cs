// 集保 E 存摺交易明細核對（只比對、不寫入）：
// 存摺 PDF 有文字層，由瀏覽器端 pdf.js 抽出每列（見 StocksModule 新增交易頁腳本），
// 這裡與現有交易紀錄比對，找出漏登或日期可能填錯的交易；實際匯入請用券商對帳單。
// 存摺同一天同股票同方向會合併成一列（例如分兩筆各 1,000 股買進 → 一列 2,000 股），
// 所以以「日期＋股號＋買賣別」加總股數比對。
public static class PassbookCheck
{
    static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);
    static readonly System.Text.RegularExpressions.Regex CodePattern = new(@"^\d{4,6}[A-Z]?$");

    // 系統裡日期不同但股數相同的交易，相差幾天內視為「可能是同一筆」（手動登錄時日期填錯）
    const int NearDays = 5;

    public static void MapRoutes(WebApplication app)
    {
        app.MapPost("/api/stocks/trade/passbook/check", (PassbookRequest req) =>
            req.Rows == null ? Results.BadRequest("沒有存摺明細") : Results.Json(Check(req.Rows, StocksModule.LoadTrades()), WebJson));
    }

    static string? TradeType(string memo) =>
        memo.Contains("買進") ? "現買"
        : memo.Contains("賣出") ? "現賣"
        : memo.Contains("配股") || memo.Contains("除權") ? "配股"
        : null;

    static string Key(string date, string code, string type) => $"{date}|{code}|{type}";

    static PassbookReport Check(List<PassbookRow> rows, List<Trade> trades)
    {
        var warnings = new List<string>();
        var ignored = new Dictionary<string, int>();
        var groups = new Dictionary<string, (string Date, string Code, string Name, string Type, long Shares)>();
        foreach (var r in rows)
        {
            var memo = r.Memo ?? "";
            var type = TradeType(memo);
            if (type == null) { ignored[memo] = ignored.GetValueOrDefault(memo) + 1; continue; }
            var code = r.Code?.Trim() ?? "";
            if (!DateTime.TryParseExact(r.Date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _)
                || !CodePattern.IsMatch(code) || r.Shares <= 0)
            {
                warnings.Add($"略過無法辨識的列：{r.Date} {r.Code} {r.Memo} {r.Shares}");
                continue;
            }
            var key = Key(r.Date, code, type);
            groups.TryGetValue(key, out var g);
            groups[key] = (r.Date, code, g.Name ?? r.Name?.Trim() ?? "", type, g.Shares + r.Shares);
        }
        if (groups.Count == 0) return new PassbookReport([], ignored.Select(kv => new IgnoredMemo(kv.Key, kv.Value)).ToList(), warnings, null, null);

        var from = groups.Values.Min(g => g.Date);
        var to = groups.Values.Max(g => g.Date);
        var codes = groups.Values.Select(g => g.Code).ToHashSet();
        var existing = trades.GroupBy(t => Key(t.Date, t.StockCode, t.TradeType)).ToDictionary(g => g.Key, g => g.ToList());
        var names = trades.GroupBy(t => t.StockCode).ToDictionary(g => g.Key, g => g.Last().StockName);

        var items = new List<PassbookItem>();
        foreach (var (key, g) in groups)
        {
            var have = existing.GetValueOrDefault(key)?.Sum(t => t.Shares) ?? 0;
            var status = have == g.Shares ? "matched" : have < g.Shares ? "missing" : "extra";
            var note = status switch
            {
                "missing" => have > 0 ? $"系統只登錄 {have:N0} 股，少 {g.Shares - have:N0} 股" : "系統沒有這筆交易",
                "extra" => $"系統登錄 {have:N0} 股，比存摺多 {have - g.Shares:N0} 股",
                _ => "",
            };
            items.Add(new PassbookItem(g.Date, g.Code, names.GetValueOrDefault(g.Code) ?? g.Name, g.Type, g.Shares, have, status, note));
        }

        // 系統有、存摺卻沒有這天的（限存摺期間內、存摺上出現過的股票，其他帳戶的股票不列入）
        foreach (var (key, list) in existing)
        {
            var t = list[0];
            if (groups.ContainsKey(key) || !codes.Contains(t.StockCode)
                || string.Compare(t.Date, from, StringComparison.Ordinal) < 0 || string.Compare(t.Date, to, StringComparison.Ordinal) > 0) continue;
            items.Add(new PassbookItem(t.Date, t.StockCode, t.StockName, t.TradeType, 0, list.Sum(x => x.Shares), "extra", "存摺沒有這筆，可能是日期填錯或其他帳戶的交易"));
        }

        // 少登與多登同股同方向、相差幾天且股數相同 → 很可能是同一筆日期填錯
        var extras = items.Where(i => i.Status == "extra").ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var m = items[i];
            if (m.Status != "missing") continue;
            var lack = m.PassbookShares - m.SystemShares;
            var near = extras.FirstOrDefault(x => x.Code == m.Code && x.TradeType == m.TradeType
                && x.SystemShares - x.PassbookShares == lack
                && Math.Abs((DateTime.Parse(x.Date) - DateTime.Parse(m.Date)).TotalDays) <= NearDays);
            if (near != null) items[i] = m with { Note = m.Note + $"；系統 {near.Date} 多 {lack:N0} 股，可能是日期填錯" };
        }

        items = items.OrderByDescending(i => i.Date).ThenBy(i => i.Code).ToList();
        return new PassbookReport(items, ignored.Select(kv => new IgnoredMemo(kv.Key, kv.Value)).ToList(), warnings, from, to);
    }
}

// 瀏覽器端已把民國日期轉成 yyyy-MM-dd
public record PassbookRow(string Date, string Code, string? Name, string? Memo, long Shares);

public record PassbookRequest(List<PassbookRow> Rows);

// status：matched 相符｜missing 存摺有、系統少｜extra 系統多、存摺沒有
public record PassbookItem(
    string Date, string Code, string Name, string TradeType, long PassbookShares, long SystemShares, string Status, string Note);

public record IgnoredMemo(string Memo, int Count);

public record PassbookReport(List<PassbookItem> Items, List<IgnoredMemo> Ignored, List<string> Warnings, string? From, string? To);
