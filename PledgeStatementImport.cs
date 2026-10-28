using System.Text.Json;

// 元大證金質借月對帳單匯入：
// 對帳單是轉印檔（文字已成圖形），由瀏覽器端 OCR 讀成 Statement（見 PledgeModule 頁面腳本），
// 這裡負責與現有質借逐筆比對產生預覽，使用者勾選確認後才寫入。
// 每列對帳單＝一筆借款，以「股號＋借款日」對應到現有質借。
public static class PledgeStatementImport
{
    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static void MapRoutes(WebApplication app)
    {
        app.MapPost("/api/stocks/pledge/statement/preview", async (Statement s, IHttpClientFactory httpFactory) =>
            Results.Json(BuildPreview(s, PledgeModule.LoadPledges(), await BrokerStatementImport.StockNamesByCode(httpFactory)), WebJson));
        app.MapPost("/api/stocks/pledge/statement/apply", Apply);
    }
    // ── 1. Statement → 預覽（比對現有質借）────────────────────────────────

    // names：上市櫃清單（股號 → 股名），用來補股名並抓出辨識錯的股號；抓不到清單時為空，略過該檢查
    static Preview BuildPreview(Statement s, List<PledgeLoan> pledges, Dictionary<string, string> names)
    {
        var warnings = CheckTotals(s);
        var statementDate = RocToIso(s.StatementDate);
        if (statementDate == null) warnings.Insert(0, $"讀不到對帳日（{s.StatementDate}）");
        var asOf = statementDate != null ? DateTime.Parse(statementDate) : DateTime.Today;

        var matched = new HashSet<string>();
        var items = new List<PlanItem>();
        // 手動加的空白列在填好股號前先略過
        foreach (var r in s.Rows.Where(r => !string.IsNullOrWhiteSpace(r.StockCode)))
        {
            var loanDate = RocToIso(r.LoanDate);
            if (loanDate == null) warnings.Add($"序號 {r.Seq} 借款日讀不到（{r.LoanDate}）");
            var p = Match(r, loanDate, pledges, matched);
            if (p != null) matched.Add(p.Id);

            var repaid = r.Balance <= 0;
            var interestAfter = repaid ? r.RepaidPaidInterest + r.RepaidUnpaidInterest + r.RepaidFee : r.InterestDue;
            var closedDate = repaid ? RocToIso(r.RepayDate) ?? statementDate : null;
            var sharesAfter = repaid ? (p?.Shares ?? r.RepaidShares) : r.Shares;

            string action;
            if (p == null) action = repaid ? "create-closed" : "create";
            else
            {
                // 系統估算與對帳單差 1 元也要同步：只有已依同一份對帳單更新過（起算日、累計利息完全一致）才算無變動
                var same = p.AccumulatedInterest == interestAfter
                    && p.InterestSince == (repaid ? closedDate : statementDate)
                    && p.Principal == (repaid ? 0 : r.Balance)
                    && p.Shares == sharesAfter
                    && p.ClosedDate == closedDate;
                action = same ? "unchanged" : repaid ? "close" : "update";
            }

            var listedName = names.GetValueOrDefault(r.StockCode);
            var unlisted = names.Count > 0 && listedName == null;
            if (unlisted) warnings.Add($"序號 {r.Seq} 股號 {r.StockCode} 不在上市櫃清單，可能辨識錯誤");
            var stockName = !string.IsNullOrEmpty(p?.StockName) ? p.StockName : listedName ?? r.StockName;

            items.Add(new PlanItem(r.Seq, action, p?.Id, r.StockCode, stockName, loanDate ?? r.LoanDate,
                p?.Shares, sharesAfter,
                p?.Principal, repaid ? 0 : r.Balance,
                p == null ? null : Math.Round(PledgeModule.TotalInterest(p, asOf)), interestAfter,
                closedDate, repaid ? $"{r.RepayMethod} {r.RepaidPrincipal:N0} 元" : "", Suspect: unlisted));
        }

        // 股號辨識錯一位數（如 2892 → 2802）時對不到現有質借、會被當成新借款：
        // 找借款日相同、且股號只差一位數或原始借款金額相同、尚未被對應的質借，提示並提供一鍵改正
        var rowsBySeq = s.Rows.ToDictionary(r => r.Seq);
        for (var k = 0; k < items.Count; k++)
        {
            var it = items[k];
            if (it.PledgeId != null || it.LoanDate.Length != 10) continue;
            var r = rowsBySeq[it.Seq];
            var original = r.Balance + r.RepaidPrincipal;
            var guess = pledges
                .Where(p => !matched.Contains(p.Id) && p.StockCode != it.StockCode && PledgeLoanDate(p) == it.LoanDate)
                .Select(p => (p, oneDigit: OneDigitOff(p.StockCode, it.StockCode), sameAmount: OriginalPrincipal(p) == original))
                .Where(x => x.oneDigit || x.sameAmount)
                .OrderByDescending(x => x.oneDigit && x.sameAmount).ThenByDescending(x => x.oneDigit)
                .FirstOrDefault();
            if (guess.p == null) continue;
            var why = string.Join("、", new[] { "借款日相同", guess.oneDigit ? "股號只差一位數" : "", guess.sameAmount ? "借款金額相同" : "" }.Where(w => w != ""));
            warnings.Add($"序號 {it.Seq} 股號 {it.StockCode} 對不到現有質借，但 {guess.p.StockCode} {guess.p.StockName}（{why}），可能是辨識錯誤");
            items[k] = it with { Suspect = true, SuggestCode = guess.p.StockCode };
        }

        var orphans = pledges.Where(p => p.ClosedDate == null && !matched.Contains(p.Id))
            .Select(p => new Orphan(p.Id, p.StockCode, p.StockName, p.Shares, p.Principal, PledgeLoanDate(p), p.CustodyAccount))
            .ToList();

        var open = pledges.Where(p => p.ClosedDate == null).ToList();
        var defaultRate = open.GroupBy(p => p.Rate).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        return new Preview(statementDate, warnings, items, orphans, defaultRate);
    }

    // 股號＋借款日相同者優先（同日多筆取原始借款金額最接近者）；
    // 找不到時退而求其次：同股號、未結清、張數與本金都相同
    static PledgeLoan? Match(StatementRow r, string? loanDate, List<PledgeLoan> pledges, HashSet<string> taken)
    {
        var pool = pledges.Where(p => p.StockCode == r.StockCode && !taken.Contains(p.Id)).ToList();
        var original = r.Balance + r.RepaidPrincipal;
        var byDate = pool.Where(p => loanDate != null && PledgeLoanDate(p) == loanDate)
            .OrderBy(p => p.ClosedDate != null)
            .ThenBy(p => Math.Abs(OriginalPrincipal(p) - original))
            .FirstOrDefault();
        return byDate ?? pool.FirstOrDefault(p => p.ClosedDate == null && r.Balance > 0 && p.Principal == r.Balance && p.Shares == r.Shares);
    }

    static bool OneDigitOff(string a, string b) =>
        a.Length == b.Length && a.Zip(b).Count(x => x.First != x.Second) == 1;

    static string PledgeLoanDate(PledgeLoan p) =>
        p.LoanDate ?? p.Events?.FirstOrDefault()?.Date ?? p.InterestSince;

    static decimal OriginalPrincipal(PledgeLoan p) =>
        p.Events?.FirstOrDefault()?.PrincipalAfter ?? p.Principal;

    // 各欄加總與「合計」列比對，辨識錯字時能立刻看出來
    static List<string> CheckTotals(Statement s)
    {
        var w = new List<string>();
        void Check(string name, decimal sum, decimal total)
        {
            if (sum != total) w.Add($"{name}逐列加總 {sum:N0} 與合計列 {total:N0} 不符，請核對對帳單");
        }
        Check("餘額張數", s.Rows.Sum(r => r.Shares), s.Totals.Shares);
        Check("應繳利息", s.Rows.Sum(r => r.InterestDue), s.Totals.InterestDue);
        Check("融資餘額", s.Rows.Sum(r => r.Balance), s.Totals.Balance);
        Check("沖銷張數", s.Rows.Sum(r => r.RepaidShares), s.Totals.RepaidShares);
        Check("沖銷未繳利息", s.Rows.Sum(r => r.RepaidUnpaidInterest), s.Totals.RepaidUnpaidInterest);
        Check("沖銷融資金", s.Rows.Sum(r => r.RepaidPrincipal), s.Totals.RepaidPrincipal);
        foreach (var r in s.Rows.Where(r => r.InterestDue != r.InterestPaid + r.InterestUnpaid))
            w.Add($"序號 {r.Seq} 應繳利息 {r.InterestDue:N0} ≠ 已繳 {r.InterestPaid:N0} ＋ 未繳 {r.InterestUnpaid:N0}");
        return w;
    }

    // 民國年 1150203 / 115/02/03 → 2026-02-03
    static string? RocToIso(string? roc)
    {
        var d = new string((roc ?? "").Where(char.IsDigit).ToArray());
        if (d.Length is not (6 or 7)) return null;
        var y = int.Parse(d[..^4]) + 1911;
        var m = int.Parse(d[^4..^2]);
        var day = int.Parse(d[^2..]);
        try { return new DateTime(y, m, day).ToString("yyyy-MM-dd"); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ── 2. 套用勾選的項目 ─────────────────────────────────────────────────

    static async Task<IResult> Apply(ApplyRequest req, IHttpClientFactory httpFactory)
    {
        var pledges = PledgeModule.LoadPledges();
        var preview = BuildPreview(req.Statement, pledges, await BrokerStatementImport.StockNamesByCode(httpFactory));
        if (preview.StatementDate == null) return Results.BadRequest("讀不到對帳日，無法套用");
        var statementDate = preview.StatementDate;
        var label = $"元大對帳單 {statementDate[..7]}";
        var custody = pledges.Where(p => p.ClosedDate == null).GroupBy(p => p.CustodyAccount)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        var rate = req.Rate > 0 ? req.Rate : preview.DefaultRate;
        var selected = req.Seqs.ToHashSet();
        var rows = req.Statement.Rows.ToDictionary(r => r.Seq);
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var applied = 0;

        foreach (var item in preview.Items.Where(i => selected.Contains(i.Seq) && i.Action != "unchanged"))
        {
            var r = rows[item.Seq];
            var closing = item.Action is "close" or "create-closed";
            var eventDate = closing ? item.ClosedDate! : statementDate;
            var type = closing ? $"{label}（{(string.IsNullOrEmpty(r.RepayMethod) ? "還款" : r.RepayMethod)}）" : label;

            if (item.PledgeId == null)
            {
                if (rate <= 0) return Results.BadRequest("請輸入新借款的利率");
                var originalPrincipal = r.Balance + r.RepaidPrincipal;
                var startDate = item.LoanDate.Length == 10 ? item.LoanDate : eventDate;
                pledges.Add(new PledgeLoan(
                    Guid.NewGuid().ToString("N")[..8], item.StockCode, item.StockName,
                    item.SharesAfter, item.PrincipalAfter, rate, eventDate, item.InterestAfter, custody, now,
                    [
                        new PledgeEvent(startDate, "起息", rate, null, null, 0, originalPrincipal),
                        new PledgeEvent(eventDate, type, null, closing ? r.RepaidPrincipal : null, null, item.InterestAfter, item.PrincipalAfter),
                    ],
                    closing ? item.ClosedDate : null, $"由{label}匯入", startDate));
            }
            else
            {
                var idx = pledges.FindIndex(p => p.Id == item.PledgeId);
                var p = pledges[idx];
                var settled = item.InterestAfter - p.AccumulatedInterest;
                var repaidAmount = p.Principal - item.PrincipalAfter;
                var events = (p.Events ?? []).ToList();
                events.Add(new PledgeEvent(eventDate, type, null, repaidAmount > 0 ? repaidAmount : null, null, settled, item.PrincipalAfter));
                pledges[idx] = p with
                {
                    Shares = item.SharesAfter,
                    Principal = item.PrincipalAfter,
                    AccumulatedInterest = item.InterestAfter,
                    InterestSince = eventDate,
                    ClosedDate = closing ? item.ClosedDate : null,
                    Events = events,
                    LoanDate = p.LoanDate ?? (item.LoanDate.Length == 10 ? item.LoanDate : null),
                };
            }
            applied++;
        }

        PledgeModule.SavePledges(pledges);
        return Results.Json(new { applied }, WebJson);
    }
}

public record Statement(string StatementDate, List<StatementRow> Rows, StatementTotals Totals);

public record StatementRow(
    int Seq, string StockCode, string StockName, string LoanDate, int Shares,
    decimal InterestDue, decimal InterestPaid, decimal InterestUnpaid, decimal Balance,
    string RepayMethod, string RepayDate, int RepaidShares,
    decimal RepaidPaidInterest, decimal RepaidUnpaidInterest, decimal RepaidPrincipal, decimal RepaidFee);

public record StatementTotals(
    int Shares, decimal InterestDue, decimal Balance, int RepaidShares, decimal RepaidUnpaidInterest, decimal RepaidPrincipal);

// action：update 更新｜close 已還款結清｜create 新借款｜create-closed 新借款但已還清｜unchanged 無變動
public record PlanItem(
    int Seq, string Action, string? PledgeId, string StockCode, string StockName, string LoanDate,
    int? SharesBefore, int SharesAfter, decimal? PrincipalBefore, decimal PrincipalAfter,
    decimal? InterestBefore, decimal InterestAfter, string? ClosedDate, string Note,
    // Suspect：股號可能辨識錯（預設不勾選）；SuggestCode：推測的正確股號
    bool Suspect = false, string? SuggestCode = null);

// 系統中未結清、但對帳單上找不到的質借（可能屬於其他機構），只提示不處理
public record Orphan(string Id, string StockCode, string StockName, int Shares, decimal Principal, string LoanDate, string CustodyAccount);

public record Preview(string? StatementDate, List<string> Warnings, List<PlanItem> Items, List<Orphan> Orphans, decimal DefaultRate);

public record ApplyRequest(Statement Statement, List<int> Seqs, decimal Rate);
