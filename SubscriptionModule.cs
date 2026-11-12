using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

// 生活 › 訂閱服務：管理 Office 365、Apple 開發者帳號、Higgsfield 等定期扣款服務
public static class SubscriptionModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string SubsFile = Path.Combine(DataDir, "subscriptions.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    static readonly object FileLock = new();

    // 計費週期：key → (顯示名稱, 月數)
    static readonly Dictionary<string, (string Label, int Months)> Cycles = new()
    {
        ["monthly"] = ("每月", 1),
        ["quarterly"] = ("每季", 3),
        ["halfyear"] = ("每半年", 6),
        ["yearly"] = ("每年", 12),
    };
    static readonly string[] Currencies = ["TWD", "USD", "JPY", "EUR"];

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/life/subs", SubsPage);
        app.MapPost("/api/life/subs", Upsert);
        app.MapPost("/api/life/subs/rates", SaveRates);
        app.MapPost("/api/life/subs/{id}/toggle", Toggle);
        app.MapDelete("/api/life/subs/{id}", Delete);
    }

    // ── Page ──────────────────────────────────────────────────────────────

    static async Task SubsPage(HttpContext ctx)
    {
        var data = Load();
        var today = DateTime.Today;
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        string Money(decimal v) => v.ToString("#,0", CultureInfo.InvariantCulture);
        string Price(decimal v, string cur) => cur + " " + v.ToString(v % 1 == 0 ? "#,0" : "#,0.##", CultureInfo.InvariantCulture);

        var active = data.Items.Where(s => !s.Cancelled).ToList();
        var monthlyTwd = active.Sum(s => ToTwd(s.Price, s.Currency, data) / CycleMonths(s.Cycle));
        var yearlyTwd = monthlyTwd * 12;
        var due30 = active
            .Select(s => (s, next: NextRenewal(s, today)))
            .Where(x => x.next != null && (x.next.Value - today).TotalDays <= 30)
            .ToList();
        var due30Twd = due30.Sum(x => ToTwd(x.s.Price, x.s.Currency, data));

        string Row(Subscription s)
        {
            var next = s.Cancelled ? null : NextRenewal(s, today);
            var days = next == null ? (int?)null : (int)(next.Value - today).TotalDays;
            var dueCls = days == null ? "" : days <= 7 ? " due-soon" : days <= 30 ? " due-month" : "";
            var dueText = next == null
                ? (s.Cancelled ? "<span class='muted'>已停用</span>" : "<span class='muted'>—</span>")
                : $"{next:yyyy-MM-dd}<div class='days{dueCls}'>{(days < 0 ? $"已到期 {-days} 天" : days == 0 ? "今天扣款" : $"{days} 天後")}</div>";
            var twdMonthly = ToTwd(s.Price, s.Currency, data) / CycleMonths(s.Cycle);
            var nameHtml = string.IsNullOrWhiteSpace(s.Url)
                ? Enc(s.Name)
                : $"<a href='{Enc(SafeUrl(s.Url))}' target='_blank' rel='noopener'>{Enc(s.Name)}</a>";
            var meta = string.Join(" · ", new[] { s.Account, s.Note }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Enc));
            var json = Enc(JsonSerializer.Serialize(s, JsonOpts));
            return $@"
<tr class='{(s.Cancelled ? "cancelled" : "")}{dueCls}'>
  <td><div class='sub-name'>{nameHtml}</div>{(meta == "" ? "" : $"<div class='sub-meta'>{meta}</div>")}</td>
  <td>{(string.IsNullOrWhiteSpace(s.Category) ? "" : $"<span class='tag'>{Enc(s.Category)}</span>")}</td>
  <td class='num'>{Price(s.Price, s.Currency)}<div class='sub-meta'>{Enc(CycleLabel(s.Cycle))}</div></td>
  <td class='num'>{(s.Cancelled ? "<span class='muted'>—</span>" : "≈ NT$ " + Money(twdMonthly))}</td>
  <td>{dueText}</td>
  <td>{Enc(s.PaymentMethod)}{(s.AutoRenew ? "" : "<div class='sub-meta'>手動續約</div>")}</td>
  <td class='ops'>
    <button class='btn btn-outline btn-sm' data-sub='{json}' onclick='openEdit(this)'>編輯</button>
    <button class='btn btn-outline btn-sm' onclick='toggleSub(""{s.Id}"", {(s.Cancelled ? "false" : "true")})'>{(s.Cancelled ? "恢復" : "停用")}</button>
    <button class='btn btn-danger btn-sm' onclick='delSub(""{s.Id}"")'>刪除</button>
  </td>
</tr>";
        }

        // 啟用中依下次扣款日排序，已停用放最後
        var ordered = data.Items
            .OrderBy(s => s.Cancelled)
            .ThenBy(s => NextRenewal(s, today) ?? DateTime.MaxValue)
            .ThenBy(s => s.Name)
            .ToList();

        var tableHtml = ordered.Count == 0
            ? "<div class='empty-state'><div class='icon'>🧾</div><p>還沒有訂閱，點上方「＋ 新增訂閱」或下方快速新增。</p></div>"
            : $@"<div class='table-wrap'><table class='subs-table'>
<thead><tr><th>服務</th><th>分類</th><th class='num'>費用</th><th class='num'>月均（台幣）</th><th>下次扣款</th><th>付款方式</th><th></th></tr></thead>
<tbody>{string.Join("", ordered.Select(Row))}</tbody></table></div>";

        // 快速新增：只預填名稱／幣別／週期／分類，金額與日期請自行填
        var presets = new[]
        {
            new { name = "Office 365", category = "軟體", currency = "TWD", cycle = "yearly", url = "https://account.microsoft.com/services" },
            new { name = "Apple 開發者帳號", category = "開發", currency = "USD", cycle = "yearly", url = "https://developer.apple.com/account" },
            new { name = "Higgsfield", category = "AI", currency = "USD", cycle = "monthly", url = "https://higgsfield.ai" },
        };
        var existingNames = data.Items.Select(s => s.Name.Trim().ToLowerInvariant()).ToHashSet();
        var presetBtns = string.Join("", presets
            .Where(p => !existingNames.Contains(p.name.ToLowerInvariant()))
            .Select(p => $"<button class='btn btn-outline btn-sm' data-preset='{Enc(JsonSerializer.Serialize(p))}' onclick='openPreset(this)'>＋ {Enc(p.name)}</button>"));
        var presetHtml = presetBtns == "" ? "" : $"<div class='actions preset-bar'><span class='muted'>快速新增：</span>{presetBtns}</div>";

        var categories = data.Items.Select(s => s.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct()
            .Concat(["軟體", "開發", "AI", "影音", "雲端", "其他"]).Distinct().ToList();
        var catOptions = string.Join("", categories.Select(c => $"<option value='{Enc(c)}'>"));
        var cycleOptions = string.Join("", Cycles.Select(kv => $"<option value='{kv.Key}'>{kv.Value.Label}</option>"));
        var curOptions = string.Join("", Currencies.Select(c => $"<option>{c}</option>"));
        var rateInputs = string.Join("", Currencies.Where(c => c != "TWD").Select(c =>
            $"<label class='rate'>{c} <input type='number' step='0.0001' min='0' data-cur='{c}' value='{GetRate(data, c).ToString(CultureInfo.InvariantCulture)}'></label>"));

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>🧾 訂閱服務</h1>
  <button class='btn' onclick='openAdd()'>＋ 新增訂閱</button>
</div>
<style>
.subs-table td.num,.subs-table th.num{{text-align:right;font-variant-numeric:tabular-nums}}
.subs-table td{{vertical-align:top}}
.sub-name{{font-weight:600}}
.sub-name a{{color:#0055cc;text-decoration:none}}
.sub-name a:hover{{text-decoration:underline}}
.sub-meta{{font-size:.78rem;color:#999;margin-top:2px}}
.muted{{color:#aaa}}
.days{{font-size:.78rem;color:#888}}
.days.due-month{{color:#b3700a;font-weight:600}}
.days.due-soon{{color:#c0392b;font-weight:700}}
tr.due-soon td{{background:#fff6f5}}
tr.cancelled td{{color:#aaa}}
tr.cancelled .sub-name a{{color:#99a}}
.ops{{white-space:nowrap;text-align:right}}
.preset-bar{{margin:14px 0 0;font-size:.88rem}}
.rates{{display:flex;gap:12px;flex-wrap:wrap;align-items:center;font-size:.85rem;color:#666;margin-top:16px}}
.rates label.rate{{display:flex;align-items:center;gap:4px;margin:0;font-size:.85rem}}
.rates input{{width:90px;padding:5px 8px;font-size:.85rem}}
.modal-bg{{position:fixed;inset:0;background:rgba(0,0,0,.4);display:flex;align-items:center;justify-content:center;z-index:50;padding:16px}}
.modal{{background:#fff;border-radius:12px;padding:22px;width:min(100%,520px);max-height:92vh;overflow:auto;box-shadow:0 8px 30px rgba(0,0,0,.25)}}
.modal h3{{margin:0 0 14px}}
.chk{{display:flex;align-items:center;gap:6px;margin:0}}
.chk input{{width:auto}}
@media (max-width:640px){{.row2,.row3{{grid-template-columns:1fr}}}}
</style>
<div class='cards'>
  <div class='card'><div class='lbl'>啟用中</div><div class='val'>{active.Count} 項</div></div>
  <div class='card'><div class='lbl'>每月平均（台幣）</div><div class='val'>NT$ {Money(monthlyTwd)}</div></div>
  <div class='card'><div class='lbl'>每年合計（台幣）</div><div class='val'>NT$ {Money(yearlyTwd)}</div></div>
  <div class='card'><div class='lbl'>30 天內扣款</div><div class='val'>{due30.Count} 筆 · NT$ {Money(due30Twd)}</div></div>
</div>
<div id='msg'></div>
{tableHtml}
{presetHtml}
<div class='rates'>
  <span>換算匯率（1 外幣 = ? 台幣）：</span>{rateInputs}
  <button class='btn btn-outline btn-sm' onclick='saveRates()'>儲存匯率</button>
</div>

<div id='subModal' class='modal-bg' style='display:none'>
  <div class='modal'>
    <h3 id='m-title'>新增訂閱</h3>
    <input type='hidden' id='f-id'>
    <div class='row2'>
      <div class='field'><label>服務名稱</label><input id='f-name' placeholder='例如：Office 365'></div>
      <div class='field'><label>分類</label><input id='f-category' list='cat-list' placeholder='例如：軟體'><datalist id='cat-list'>{catOptions}</datalist></div>
    </div>
    <div class='row3'>
      <div class='field'><label>金額</label><input type='number' id='f-price' step='0.01' min='0'></div>
      <div class='field'><label>幣別</label><select id='f-currency'>{curOptions}</select></div>
      <div class='field'><label>週期</label><select id='f-cycle'>{cycleOptions}</select></div>
    </div>
    <div class='row2'>
      <div class='field'><label>下次扣款／到期日</label><input type='date' id='f-next'></div>
      <div class='field'><label>付款方式</label><input id='f-payment' placeholder='例如：國泰信用卡'></div>
    </div>
    <div class='field'><label class='chk'><input type='checkbox' id='f-auto' checked> 自動續約（日期過了會自動往後推一個週期）</label></div>
    <div class='field'><label>帳號／Email（選填）</label><input id='f-account'></div>
    <div class='field'><label>管理網址（選填）</label><input id='f-url' placeholder='https://'></div>
    <div class='field'><label>備註（選填）</label><input id='f-note' placeholder='例如：家庭方案 6 人共用'></div>
    <div id='m-msg'></div>
    <div class='actions'>
      <button class='btn' onclick='saveSub()'>儲存</button>
      <button class='btn btn-outline' onclick='closeModal()'>取消</button>
    </div>
  </div>
</div>
<script>
const $ = id => document.getElementById(id);
function fill(s, title) {{
  $('m-title').textContent = title;
  $('f-id').value = s.id || '';
  $('f-name').value = s.name || '';
  $('f-category').value = s.category || '';
  $('f-price').value = s.price ?? '';
  $('f-currency').value = s.currency || 'TWD';
  $('f-cycle').value = s.cycle || 'monthly';
  $('f-next').value = s.nextDate || '';
  $('f-payment').value = s.paymentMethod || '';
  $('f-auto').checked = s.autoRenew !== false;
  $('f-account').value = s.account || '';
  $('f-url').value = s.url || '';
  $('f-note').value = s.note || '';
  $('m-msg').innerHTML = '';
  $('subModal').style.display = 'flex';
  $('f-name').focus();
}}
const openAdd = () => fill({{}}, '新增訂閱');
const openPreset = b => {{ fill(JSON.parse(b.dataset.preset), '新增訂閱'); $('f-price').focus(); }};
const openEdit = b => fill(JSON.parse(b.dataset.sub), '編輯訂閱');
function closeModal() {{ $('subModal').style.display = 'none'; }}
$('subModal').addEventListener('click', e => {{ if (e.target.id === 'subModal') closeModal(); }});
document.addEventListener('keydown', e => {{ if (e.key === 'Escape') closeModal(); }});

async function saveSub() {{
  const req = {{
    id: $('f-id').value || null,
    name: $('f-name').value.trim(),
    category: $('f-category').value.trim(),
    price: parseFloat($('f-price').value) || 0,
    currency: $('f-currency').value,
    cycle: $('f-cycle').value,
    nextDate: $('f-next').value,
    paymentMethod: $('f-payment').value.trim(),
    autoRenew: $('f-auto').checked,
    account: $('f-account').value.trim(),
    url: $('f-url').value.trim(),
    note: $('f-note').value.trim()
  }};
  if (!req.name) {{ $('m-msg').innerHTML = ""<div class='alert err'>請填寫服務名稱</div>""; return; }}
  const r = await fetch('/api/life/subs', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify(req) }});
  if (r.ok) location.reload();
  else $('m-msg').innerHTML = ""<div class='alert err'>"" + ((await r.text()) || '儲存失敗') + ""</div>"";
}}
async function call(url, method) {{
  const r = await fetch(url, {{ method: method || 'POST' }});
  if (r.ok) location.reload();
  else $('msg').innerHTML = ""<div class='alert err'>操作失敗</div>"";
}}
function toggleSub(id, cancel) {{
  if (cancel && !confirm('標記為已停用？（資料保留，不計入費用）')) return;
  call('/api/life/subs/' + id + '/toggle');
}}
function delSub(id) {{ if (confirm('確定刪除這筆訂閱？')) call('/api/life/subs/' + id, 'DELETE'); }}
async function saveRates() {{
  const rates = {{}};
  document.querySelectorAll('.rates input[data-cur]').forEach(i => rates[i.dataset.cur] = parseFloat(i.value) || 0);
  const r = await fetch('/api/life/subs/rates', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify(rates) }});
  if (r.ok) location.reload();
  else $('msg').innerHTML = ""<div class='alert err'>匯率儲存失敗</div>"";
}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("訂閱服務", "life", "subs", body));
    }

    // ── API ───────────────────────────────────────────────────────────────

    static IResult Upsert(SubscriptionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest("請填寫服務名稱");
        if (req.Price < 0) return Results.BadRequest("金額不能是負數");
        var cycle = Cycles.ContainsKey(req.Cycle ?? "") ? req.Cycle! : "monthly";
        var currency = Currencies.Contains(req.Currency) ? req.Currency! : "TWD";
        var next = DateTime.TryParse(req.NextDate, out var d) ? d.ToString("yyyy-MM-dd") : "";

        lock (FileLock)
        {
            var data = Load();
            var idx = string.IsNullOrWhiteSpace(req.Id) ? -1 : data.Items.FindIndex(s => s.Id == req.Id);
            var sub = new Subscription(
                idx >= 0 ? data.Items[idx].Id : Guid.NewGuid().ToString("N")[..8],
                req.Name.Trim(), req.Category?.Trim() ?? "", req.Price, currency, cycle, next,
                req.AutoRenew, req.PaymentMethod?.Trim() ?? "", req.Account?.Trim() ?? "",
                req.Url?.Trim() ?? "", req.Note?.Trim() ?? "",
                idx >= 0 && data.Items[idx].Cancelled,
                idx >= 0 ? data.Items[idx].CreatedAt : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            if (idx >= 0) data.Items[idx] = sub; else data.Items.Add(sub);
            Save(data);
        }
        return Results.Ok();
    }

    static IResult Toggle(string id)
    {
        lock (FileLock)
        {
            var data = Load();
            var idx = data.Items.FindIndex(s => s.Id == id);
            if (idx < 0) return Results.NotFound();
            data.Items[idx] = data.Items[idx] with { Cancelled = !data.Items[idx].Cancelled };
            Save(data);
        }
        return Results.Ok();
    }

    static IResult Delete(string id)
    {
        lock (FileLock)
        {
            var data = Load();
            if (data.Items.RemoveAll(s => s.Id == id) == 0) return Results.NotFound();
            Save(data);
        }
        return Results.Ok();
    }

    static IResult SaveRates(Dictionary<string, decimal> rates)
    {
        lock (FileLock)
        {
            var data = Load();
            foreach (var (cur, rate) in rates)
                if (Currencies.Contains(cur) && cur != "TWD" && rate > 0)
                    data.Rates[cur] = rate;
            Save(data);
        }
        return Results.Ok();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    static int CycleMonths(string cycle) => Cycles.TryGetValue(cycle, out var c) ? c.Months : 1;
    static string CycleLabel(string cycle) => Cycles.TryGetValue(cycle, out var c) ? c.Label : cycle;

    static decimal GetRate(SubsData data, string cur) => cur switch
    {
        "TWD" => 1m,
        _ when data.Rates.TryGetValue(cur, out var r) && r > 0 => r,
        "USD" => 32m,
        "JPY" => 0.21m,
        "EUR" => 35m,
        _ => 1m,
    };
    static decimal ToTwd(decimal price, string cur, SubsData data) => price * GetRate(data, cur);

    // 自動續約且日期已過 → 依週期往後推到今天以後，顯示真正的下次扣款日
    static DateTime? NextRenewal(Subscription s, DateTime today)
    {
        if (s.Cancelled || !DateTime.TryParse(s.NextDate, out var d)) return null;
        if (!s.AutoRenew) return d;
        var months = CycleMonths(s.Cycle);
        var n = 0;
        var cur = d;
        while (cur < today && n < 1200) cur = d.AddMonths(months * ++n); // 從原日期累加，避免月底日期漂移
        return cur;
    }

    static string SafeUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url : "https://" + url;

    static SubsData Load()
    {
        if (!File.Exists(SubsFile)) return new SubsData([], []);
        var data = JsonSerializer.Deserialize<SubsData>(File.ReadAllText(SubsFile), JsonOpts) ?? new SubsData([], []);
        return data with { Items = data.Items ?? [], Rates = data.Rates ?? [] };
    }

    static void Save(SubsData data)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SubsFile, JsonSerializer.Serialize(data, JsonOpts));
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

public record SubsData(
    [property: JsonPropertyName("items")] List<Subscription> Items,
    [property: JsonPropertyName("rates")] Dictionary<string, decimal> Rates);

public record Subscription(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("cycle")] string Cycle,
    [property: JsonPropertyName("nextDate")] string NextDate,
    [property: JsonPropertyName("autoRenew")] bool AutoRenew,
    [property: JsonPropertyName("paymentMethod")] string PaymentMethod,
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

public record SubscriptionRequest(
    string? Id, string? Name, string? Category, decimal Price, string? Currency, string? Cycle,
    string? NextDate, bool AutoRenew, string? PaymentMethod, string? Account, string? Url, string? Note);
