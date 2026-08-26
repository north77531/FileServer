using System.Text.Json;
using System.Text.Json.Serialization;

public static class LifeModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string CardsFile = Path.Combine(DataDir, "reward_cards.json");
    // 小朋友照片資料夾（與個人網站同層），檔名為「名字.jpg」，可用環境變數覆蓋
    static readonly string PhotosDir = Environment.GetEnvironmentVariable("PHOTOS_DIR") ?? @"C:\Users\USER\OneDrive\文件\個人網站";
    static readonly string[] PhotoExts = [".jpg", ".jpeg", ".png"];
    // 印章圖片資料夾，檔名為「印章key.png/svg…」，有圖就用圖、沒有就退回 emoji
    static readonly string StampsDir = Environment.GetEnvironmentVariable("STAMPS_DIR") ?? Path.Combine(PhotosDir, "stamps");
    static readonly string[] StampImgExts = [".svg", ".png", ".webp", ".jpg", ".jpeg", ".gif"];
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // 可選的蓋章圖案（emoji、顯示名稱、分類）
    static readonly Dictionary<string, StampDef> Stamps = new()
    {
        // 動物
        ["dog"] = new("🐶", "小狗", "動物"), ["cat"] = new("🐱", "小貓", "動物"),
        ["rabbit"] = new("🐰", "兔子", "動物"), ["bear"] = new("🐻", "小熊", "動物"),
        ["panda"] = new("🐼", "熊貓", "動物"), ["fox"] = new("🦊", "狐狸", "動物"),
        ["tiger"] = new("🐯", "老虎", "動物"), ["lion"] = new("🦁", "獅子", "動物"),
        ["pig"] = new("🐷", "小豬", "動物"), ["frog"] = new("🐸", "青蛙", "動物"),
        ["monkey"] = new("🐵", "猴子", "動物"), ["chick"] = new("🐥", "小雞", "動物"),
        ["penguin"] = new("🐧", "企鵝", "動物"), ["koala"] = new("🐨", "無尾熊", "動物"),
        ["hamster"] = new("🐹", "倉鼠", "動物"), ["unicorn"] = new("🦄", "獨角獸", "動物"),
        // 汪汪隊（以每位狗狗的招牌載具／主題代表）
        ["chase"] = new("🚓", "阿奇", "汪汪隊"),    // 警犬
        ["marshall"] = new("🚒", "毛毛", "汪汪隊"), // 消防犬
        ["skye"] = new("🚁", "天天", "汪汪隊"),     // 飛行犬
        ["rubble"] = new("🚜", "小力", "汪汪隊"),   // 工程犬
        ["rocky"] = new("♻️", "灰灰", "汪汪隊"),    // 環保犬
        ["zuma"] = new("🚤", "路馬", "汪汪隊"),     // 水上救援犬
        ["everest"] = new("❄️", "珠珠", "汪汪隊"),  // 雪地救援犬
        ["tracker"] = new("🌴", "阿樂", "汪汪隊"),  // 叢林追蹤犬
        ["ryder"] = new("🛵", "萊德", "汪汪隊"),    // 隊長
    };
    static string StampIcon(string key) => (Stamps.TryGetValue(key, out var v) ? v : Stamps["dog"]).Emoji;

    // 找印章圖檔（stamps/{key}.svg…）；key 必須是已知印章，藉此擋目錄穿越
    static string? StampImgPath(string key)
    {
        if (!Stamps.ContainsKey(key)) return null;
        foreach (var ext in StampImgExts)
        {
            var p = Path.Combine(StampsDir, key + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // 印章的顯示內容：有圖用圖、沒圖退回 emoji。cls 用來套不同尺寸的樣式
    static string StampVisual(string key, string cls) =>
        StampImgPath(key) != null
            ? $"<img class='{cls}' src='/life/stamp-img/{key}' alt=''>"
            : StampIcon(key);

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/life", CardsPage);
        app.MapGet("/life/card/add", AddCardPage);
        app.MapGet("/life/photo/{name}", PhotoFile);
        app.MapGet("/life/stamp-img/{key}", StampImgFile);

        app.MapPost("/api/life/card", AddCard);
        app.MapPost("/api/life/card/{id}/stamp", Stamp);
        app.MapPost("/api/life/card/{id}/unstamp", Unstamp);
        app.MapPost("/api/life/card/{id}/redeem", RedeemCard);
        app.MapPost("/api/life/card/{id}/reset", ResetCard);
        app.MapDelete("/api/life/card/{id}", DeleteCard);
    }

    // ── Pages ─────────────────────────────────────────────────────────────

    static async Task CardsPage(HttpContext ctx)
    {
        // 未兌換的排前面，已完成兌換的放到後方
        var cards = LoadCards()
            .OrderBy(c => !string.IsNullOrWhiteSpace(c.RedeemedAt))
            .ThenBy(c => c.CreatedAt)
            .ToList();
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        string CardHtml(RewardCard c)
        {
            var icon = StampIcon(c.Stamp);
            var slotVisual = StampVisual(c.Stamp, "stamp-img");
            var count = c.Entries.Count;
            var full = count >= c.Goal;
            var redeemed = !string.IsNullOrWhiteSpace(c.RedeemedAt);
            var slots = string.Join("", Enumerable.Range(0, c.Goal).Select(i =>
            {
                if (i < count)
                {
                    var e = c.Entries[i];
                    var tip = Enc(string.IsNullOrWhiteSpace(e.Reason) ? e.Date : $"{e.Date} · {e.Reason}");
                    return $"<div class='slot filled' title='{tip}'>{slotVisual}</div>";
                }
                return $"<div class='slot'><span class='slot-no'>{i + 1}</span></div>";
            }));

            var rewardLine = string.IsNullOrWhiteSpace(c.Reward)
                ? ""
                : $"<div class='reward'>🎁 集滿獎勵：{Enc(c.Reward)}</div>";

            var fullBanner = redeemed
                ? $"<div class='full-banner redeemed'>✅ 已於 {Enc(c.RedeemedAt)} 兌換獎勵</div>"
                : full
                    ? "<div class='full-banner'>🎉 集滿囉！可以兌換獎勵 🎉</div>"
                    : "";

            var logHtml = count == 0 ? "" : $@"
<details class='stamp-log'>
  <summary>📜 蓋章紀錄（{count}）</summary>
  <ul>{string.Join("", Enumerable.Reverse(c.Entries).Select(e =>
      $"<li><span class='log-date'>{Enc(e.Date)}</span>{(string.IsNullOrWhiteSpace(e.Reason) ? "" : " — " + Enc(e.Reason))}</li>"))}</ul>
</details>";

            string actionsHtml;
            if (redeemed)
                actionsHtml = $@"<button class='btn btn-outline' onclick='resetCard(""{c.Id}"")'>↺ 重新開始</button>";
            else if (full)
                actionsHtml = $@"<button class='btn btn-stamp' disabled>🖐 蓋一個章</button>
    <button class='btn btn-outline btn-sm' onclick='unstamp(""{c.Id}"")'>↩ 取消上一個</button>
    <button class='btn btn-reward' onclick='redeem(""{c.Id}"")'>🎁 完成兌換</button>";
            else
                actionsHtml = $@"<button class='btn btn-stamp' onclick='openStamp(""{c.Id}"")'>🖐 蓋一個章</button>
    <button class='btn btn-outline btn-sm' onclick='unstamp(""{c.Id}"")' {(count == 0 ? "disabled" : "")}>↩ 取消上一個</button>";

            var stateClass = redeemed ? " redeemed" : full ? " is-full" : "";

            // 微調個別照片：裁切位置（往上錨定 = 人像往下移）與縮放（放大人像）
            var photoPos = c.ChildName switch { "周妍" => "center 20%", "周禕" => "center 8%", _ => "center" };
            var photoScale = c.ChildName switch { "周禕" => "scale(1.6)", _ => "none" };
            var avatar = PhotoPath(c.ChildName) != null
                ? $"<span class='card-photo'><img style='object-position:{photoPos};transform:{photoScale}' src='/life/photo/{Uri.EscapeDataString(c.ChildName)}' alt='{Enc(c.ChildName)}'></span>"
                : $"<span class='card-icon'>{icon}</span>";

            return $@"
<div class='reward-card{stateClass}'>
  <div class='card-head'>
    <div class='card-title'>{avatar}{Enc(c.ChildName)} 的棒棒集點卡</div>
    <button class='btn btn-danger btn-sm' onclick='delCard(""{c.Id}"")'>刪除</button>
  </div>
  <div class='progress'>已蓋 <b>{count}</b> / {c.Goal} 點</div>
  {fullBanner}
  <div class='stamp-grid'>{slots}</div>
  {rewardLine}
  {logHtml}
  <div class='card-actions'>{actionsHtml}</div>
</div>";
        }

        var cardsHtml = cards.Count == 0
            ? "<div class='empty-state'><div class='icon'>🐾</div><p>還沒有集點卡，點右上角新增一張吧！</p></div>"
            : $"<div class='card-grid'>{string.Join("", cards.Select(CardHtml))}</div>";

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>⭐ 棒棒集點卡</h1>
  <a href='/life/card/add' class='btn'>＋ 新增集點卡</a>
</div>
<style>
.card-grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:18px}}
.reward-card{{background:#fff;border:2px solid #ffe0a3;border-radius:16px;padding:18px 20px;box-shadow:0 2px 10px rgba(0,0,0,.07)}}
.reward-card.is-full{{border-color:#ffb84d;background:#fffaf0}}
.reward-card.redeemed{{border-color:#9ad0a5;background:#f6fbf7}}
.full-banner.redeemed{{background:#e6f4ea;color:#1e7e34}}
.card-head{{display:flex;align-items:center;gap:8px;margin-bottom:8px}}
.card-title{{flex:1;font-size:1.05rem;font-weight:700;color:#5a4a00;display:flex;align-items:center;gap:6px}}
.card-icon{{font-size:1.4rem}}
.card-photo{{width:46px;height:46px;border-radius:50%;border:2px solid #ffcf66;flex:none;overflow:hidden;display:inline-block}}
.card-photo img{{width:100%;height:100%;object-fit:cover;display:block}}
.reward-card.is-full .card-photo{{border-color:#ffb84d}}
.reward-card.redeemed .card-photo{{border-color:#9ad0a5}}
.progress{{font-size:.9rem;color:#888;margin-bottom:10px}}
.progress b{{color:#e8890c;font-size:1.1rem}}
.full-banner{{background:#fff3d6;color:#b3700a;border-radius:10px;padding:8px 12px;text-align:center;font-weight:700;margin-bottom:10px;animation:pop .4s ease}}
@keyframes pop{{0%{{transform:scale(.9);opacity:0}}100%{{transform:scale(1);opacity:1}}}}
.stamp-grid{{display:grid;grid-template-columns:repeat(5,1fr);gap:8px;margin-bottom:12px}}
.slot{{aspect-ratio:1;border:2px dashed #d9d9d9;border-radius:50%;display:flex;align-items:center;justify-content:center;font-size:1.6rem;background:#fafafa;position:relative}}
.slot.filled{{cursor:help}}
.slot-no{{color:#cfcfcf;font-size:.85rem;font-weight:600}}
.slot.filled{{border:2px solid #ffcf66;background:#fff6dd;box-shadow:inset 0 0 0 2px #fff;animation:stampin .35s ease}}
.slot .stamp-img{{width:88%;height:88%;object-fit:contain;border-radius:50%;display:block}}
@keyframes stampin{{0%{{transform:scale(1.6) rotate(-12deg);opacity:0}}60%{{transform:scale(.9)}}100%{{transform:scale(1) rotate(0);opacity:1}}}}
.reward{{font-size:.88rem;color:#7a6a3a;background:#fcf6e8;border-radius:8px;padding:6px 10px;margin-bottom:10px}}
.stamp-log{{font-size:.82rem;color:#777;margin-bottom:12px}}
.stamp-log summary{{cursor:pointer;color:#b3700a;font-weight:600}}
.stamp-log ul{{margin:6px 0 0;padding-left:18px;max-height:140px;overflow:auto}}
.stamp-log li{{margin:2px 0}}
.log-date{{color:#aaa;font-variant-numeric:tabular-nums}}
.card-actions{{display:flex;gap:8px;flex-wrap:wrap}}
.btn-stamp{{background:#ff9f1c}}
.btn-stamp:hover{{background:#f08c00}}
.btn-stamp:disabled{{background:#e6c79a;cursor:not-allowed}}
.btn-reward{{background:#e8530e}}
.btn-reward:hover{{background:#cf4708}}
.btn-outline:disabled{{opacity:.4;cursor:not-allowed}}
.modal-bg{{position:fixed;inset:0;background:rgba(0,0,0,.4);display:flex;align-items:center;justify-content:center;z-index:50}}
.modal{{background:#fff;border-radius:14px;padding:22px;width:min(92vw,380px);box-shadow:0 8px 30px rgba(0,0,0,.25)}}
.modal h3{{margin:0 0 14px;color:#5a4a00}}
</style>
{cardsHtml}
<div id='msg' style='margin-top:12px'></div>

<div id='stampModal' class='modal-bg' style='display:none'>
  <div class='modal'>
    <h3>🖐 蓋一個章</h3>
    <div class='field'><label>日期</label><input type='date' id='m-date'></div>
    <div class='field'><label>蓋章原因（選填）</label><input id='m-reason' placeholder='例如：自己收玩具、幫忙做家事'></div>
    <div class='actions'>
      <button class='btn btn-stamp' onclick='confirmStamp()'>確認蓋章</button>
      <button class='btn btn-outline' onclick='closeStamp()'>取消</button>
    </div>
  </div>
</div>
<script>
let curCard = null;
function openStamp(id) {{
  curCard = id;
  document.getElementById('m-date').value = new Date().toISOString().slice(0, 10);
  document.getElementById('m-reason').value = '';
  document.getElementById('stampModal').style.display = 'flex';
  document.getElementById('m-reason').focus();
}}
function closeStamp() {{ document.getElementById('stampModal').style.display = 'none'; curCard = null; }}
async function confirmStamp() {{
  if (!curCard) return;
  const body = {{
    date: document.getElementById('m-date').value,
    reason: document.getElementById('m-reason').value.trim()
  }};
  const r = await fetch('/api/life/card/' + curCard + '/stamp', {{
    method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify(body)
  }});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">蓋章失敗</div>';
}}
document.getElementById('stampModal').addEventListener('click', e => {{ if (e.target.id === 'stampModal') closeStamp(); }});
document.getElementById('m-reason').addEventListener('keydown', e => {{ if (e.key === 'Enter') confirmStamp(); }});

async function post(url) {{
  const r = await fetch(url, {{ method: 'POST' }});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">操作失敗</div>';
}}
const unstamp = id => post('/api/life/card/' + id + '/unstamp');
function redeem(id) {{ if (confirm('確定完成兌換？這張卡會標記為已兌換並保留下來。')) post('/api/life/card/' + id + '/redeem'); }}
function resetCard(id) {{ if (confirm('要清空所有蓋章紀錄、重新開始集點嗎？（這張卡會保留）')) post('/api/life/card/' + id + '/reset'); }}
async function delCard(id) {{
  if (!confirm('確定刪除這張集點卡？')) return;
  const r = await fetch('/api/life/card/' + id, {{ method: 'DELETE' }});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">刪除失敗</div>';
}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("棒棒集點卡", "life", "cards", body));
    }

    static async Task AddCardPage(HttpContext ctx)
    {
        var first = true;
        var stampChoices = string.Join("", Stamps
            .GroupBy(kv => kv.Value.Group)
            .Select(g => $@"
<div class='stamp-group-title'>{System.Net.WebUtility.HtmlEncode(g.Key)}</div>
<div class='stamp-choices'>{string.Join("", g.Select(kv =>
{
    var chec2 = first ? " checked" : "";
    first = false;
    var vis = StampImgPath(kv.Key) != null
        ? $"<img class='stamp-choice-img' src='/life/stamp-img/{kv.Key}' alt=''>"
        : $"<span class='stamp-emoji'>{kv.Value.Emoji}</span>";
    return $@"
<label class='stamp-choice'>
  <input type='radio' name='stamp' value='{kv.Key}'{chec2}>
  {vis}
  <span class='stamp-name'>{System.Net.WebUtility.HtmlEncode(kv.Value.Label)}</span>
</label>";
}))}</div>"));

        var body = $@"
<div>
  <a href='/life' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回集點卡</a>
  <h1 style='margin:4px 0 20px'>⭐ 新增棒棒集點卡</h1>
</div>
<style>
.stamp-group-title{{font-size:.82rem;font-weight:700;color:#b3700a;margin:14px 0 8px}}
.stamp-group-title:first-of-type{{margin-top:4px}}
.stamp-choices{{display:grid;grid-template-columns:repeat(auto-fill,minmax(64px,1fr));gap:10px}}
.stamp-choice{{border:2px solid #e3e3e3;border-radius:12px;padding:10px 6px;text-align:center;cursor:pointer;transition:all .15s;margin:0}}
.stamp-choice:hover{{border-color:#ffcf66}}
.stamp-choice input{{display:none}}
.stamp-emoji{{font-size:2rem;display:block;height:2.6rem;line-height:2.6rem}}
.stamp-choice-img{{width:2.6rem;height:2.6rem;object-fit:contain;display:block;margin:0 auto}}
.stamp-name{{font-size:.72rem;color:#7a6a3a;display:block;margin-top:2px}}
.stamp-choice:has(input:checked){{border-color:#ff9f1c;background:#fff6dd;box-shadow:0 0 0 2px rgba(255,159,28,.2)}}
</style>
<div class='form-card' style='max-width:560px'>
<div id='msg'></div>
<div class='field'><label>小朋友名字</label><input id='childName' placeholder='例如：小明'></div>
<div class='field'>
  <label>選擇蓋章圖案</label>
  {stampChoices}
</div>
<div class='field'><label>集滿幾點</label><input type='number' id='goal' value='10' min='1' max='60'></div>
<div class='field'><label>集滿獎勵（選填）</label><input id='reward' placeholder='例如：去吃冰淇淋'></div>
<div class='actions' style='margin-top:8px'>
  <button class='btn' onclick='submit()'>確認新增</button>
  <a href='/life' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
async function submit() {{
  const req = {{
    childName: document.getElementById('childName').value.trim(),
    stamp: document.querySelector('input[name=stamp]:checked').value,
    goal: parseInt(document.getElementById('goal').value) || 10,
    reward: document.getElementById('reward').value.trim()
  }};
  if (!req.childName) {{ showMsg('請填寫小朋友名字', 'err'); return; }}
  if (req.goal < 1) {{ showMsg('點數至少 1 點', 'err'); return; }}
  const r = await fetch('/api/life/card', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify(req) }});
  if (r.ok) {{ showMsg('✓ 已建立！', 'ok'); setTimeout(() => location.href = '/life', 800); }}
  else {{ const t = await r.text(); showMsg(t || '建立失敗', 'err'); }}
}}
function showMsg(m, t) {{ document.getElementById('msg').innerHTML = `<div class='alert ${{t}}'>${{m}}</div>`; }}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("新增集點卡", "life", "cardadd", body));
    }

    static IResult PhotoFile(string name)
    {
        var path = PhotoPath(name);
        if (path == null) return Results.NotFound();
        var ct = Path.GetExtension(path).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
        return Results.File(path, ct);
    }

    static IResult StampImgFile(string key)
    {
        var path = StampImgPath(key);
        if (path == null) return Results.NotFound();
        var ct = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };
        return Results.File(path, ct);
    }

    // 依小朋友名字找對應照片檔（名字.jpg/.jpeg/.png）；用 GetFileName 防目錄穿越
    static string? PhotoPath(string childName)
    {
        var safe = Path.GetFileName(childName ?? "");
        if (string.IsNullOrWhiteSpace(safe)) return null;
        foreach (var ext in PhotoExts)
        {
            var p = Path.Combine(PhotosDir, safe + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ── API handlers ──────────────────────────────────────────────────────

    static IResult AddCard(RewardCardRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ChildName))
            return Results.BadRequest("請填寫小朋友名字");

        var goal = Math.Clamp(req.Goal, 1, 60);
        var stamp = Stamps.ContainsKey(req.Stamp ?? "") ? req.Stamp! : "dog";

        var card = new RewardCard(
            Guid.NewGuid().ToString("N")[..8],
            req.ChildName.Trim(), stamp, goal, [], req.Reward ?? "",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var cards = LoadCards();
        cards.Add(card);
        SaveCards(cards);
        return Results.Ok();
    }

    static IResult Stamp(string id, StampRequest req)
    {
        var cards = LoadCards();
        var idx = cards.FindIndex(c => c.Id == id);
        if (idx < 0) return Results.NotFound();
        if (cards[idx].Entries.Count >= cards[idx].Goal) return Results.Ok();

        var date = string.IsNullOrWhiteSpace(req.Date) ? DateTime.Today.ToString("yyyy-MM-dd") : req.Date.Trim();
        var entries = new List<StampEntry>(cards[idx].Entries) { new(date, req.Reason?.Trim() ?? "") };
        cards[idx] = cards[idx] with { Entries = entries };
        SaveCards(cards);
        return Results.Ok();
    }

    static IResult Unstamp(string id)
    {
        var cards = LoadCards();
        var idx = cards.FindIndex(c => c.Id == id);
        if (idx < 0) return Results.NotFound();
        if (cards[idx].Entries.Count > 0)
        {
            var entries = new List<StampEntry>(cards[idx].Entries);
            entries.RemoveAt(entries.Count - 1);
            cards[idx] = cards[idx] with { Entries = entries };
            SaveCards(cards);
        }
        return Results.Ok();
    }

    static IResult RedeemCard(string id)
    {
        var cards = LoadCards();
        var idx = cards.FindIndex(c => c.Id == id);
        if (idx < 0) return Results.NotFound();
        // 標記已兌換，保留蓋章紀錄；不刪卡，需刪除請按刪除鈕
        cards[idx] = cards[idx] with { RedeemedAt = DateTime.Today.ToString("yyyy-MM-dd") };
        SaveCards(cards);
        return Results.Ok();
    }

    static IResult ResetCard(string id)
    {
        var cards = LoadCards();
        var idx = cards.FindIndex(c => c.Id == id);
        if (idx < 0) return Results.NotFound();
        cards[idx] = cards[idx] with { Entries = [], RedeemedAt = "" };
        SaveCards(cards);
        return Results.Ok();
    }

    static IResult DeleteCard(string id)
    {
        var cards = LoadCards();
        var target = cards.FirstOrDefault(c => c.Id == id);
        if (target == null) return Results.NotFound();
        cards.Remove(target);
        SaveCards(cards);
        return Results.Ok();
    }

    // ── Data helpers ──────────────────────────────────────────────────────

    static List<RewardCard> LoadCards()
    {
        if (!File.Exists(CardsFile)) return [];
        var cards = JsonSerializer.Deserialize<List<RewardCard>>(File.ReadAllText(CardsFile), JsonOpts) ?? [];
        // 舊資料可能沒有 entries 欄位（早期用點數計數），補成空清單避免 null
        for (int i = 0; i < cards.Count; i++)
            if (cards[i].Entries == null)
                cards[i] = cards[i] with { Entries = [] };
        return cards;
    }

    static void SaveCards(List<RewardCard> cards)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(CardsFile, JsonSerializer.Serialize(cards, JsonOpts));
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

// 蓋章圖案定義：emoji、顯示名稱、分類
public record StampDef(string Emoji, string Label, string Group);

public record StampEntry(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("reason")] string Reason);

public record RewardCard(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("childName")] string ChildName,
    [property: JsonPropertyName("stamp")] string Stamp,
    [property: JsonPropertyName("goal")] int Goal,
    [property: JsonPropertyName("entries")] List<StampEntry> Entries,
    [property: JsonPropertyName("reward")] string Reward,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("redeemedAt")] string RedeemedAt = "");

public record RewardCardRequest(
    string? ChildName, string? Stamp, int Goal, string? Reward);

public record StampRequest(string? Date, string? Reason);
