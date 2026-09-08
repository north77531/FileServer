using System.Text.Json;
using System.Text.Json.Serialization;

// 高千穂峡貸しボート「搶票作戰台」：倒數計時 + 一鍵複製各欄位 + 自動填單書籤，
// 讓使用者在開賣瞬間用最快速度手動送出（最後的送出／信用卡付款仍由本人操作）。
public static class TakachihoModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string CfgFile = Path.Combine(DataDir, "takachiho.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    const string BookingUrl = "https://eipro.jp/takachiho1/eventCalendars/index";

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/life/takachiho", Page);
        app.MapPost("/api/life/takachiho", Save);
    }

    static async Task Page(HttpContext ctx)
    {
        var c = Load();
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        // ── 自動填單書籤（bookmarklet）：把目前設定值包進 javascript: URL ──
        // 用 encodeURIComponent(JSON) 包資料，避免日文／引號在 href 內出問題。
        var dataObj = new Dictionary<string, string>
        {
            ["kanji"] = c.Kanji, ["kanjiSei"] = c.KanjiSei, ["kanjiMei"] = c.KanjiMei,
            ["kana"] = c.Kana, ["kanaSei"] = c.KanaSei, ["kanaMei"] = c.KanaMei,
            ["email"] = c.Email, ["tel"] = c.Tel, ["zip"] = c.Zip, ["address"] = c.Address,
            ["adult"] = c.Adult, ["child"] = c.Child, ["infant"] = c.Infant,
            ["wishTime"] = c.WishTime, ["boats"] = c.Boats,
        };
        var dataEnc = Uri.EscapeDataString(JsonSerializer.Serialize(dataObj));
        var bookmarklet = "javascript:(function(){var D=JSON.parse(decodeURIComponent('" + dataEnc + "'));" + BookmarkletLogic + "})();";
        var bookmarkletAttr = System.Net.WebUtility.HtmlEncode(bookmarklet);

        // 前端倒數／複製用設定
        var cfgJson = JsonSerializer.Serialize(new
        {
            targetTime = c.TargetTime,
            targetDate = c.TargetDate,
        });

        // 個人資料欄位（label, 值, 說明）
        string Row(string key, string label, string val, string hint = "") => $@"
<div class='tk-row'>
  <div class='tk-label'>{Enc(label)}{(hint == "" ? "" : $"<span class='tk-hint'>{Enc(hint)}</span>")}</div>
  <input class='tk-input' data-key='{key}' value='{Enc(val)}'>
  <button class='btn btn-outline btn-sm tk-copy' type='button' data-key='{key}'>複製</button>
</div>";

        var body = $@"
<div class='actions' style='margin-bottom:14px'>
  <h1 style='margin:0;flex:1'>🚣 高千穂划船搶票作戰台</h1>
  <a href='{BookingUrl}' target='_blank' rel='noopener' class='btn'>① 開啟預約網站 ↗</a>
</div>

<style>
.tk-note{{background:#fff8e6;border:1px solid #ffe0a3;border-radius:10px;padding:12px 16px;font-size:.86rem;color:#7a5c10;margin-bottom:18px;line-height:1.6}}
.tk-count{{background:linear-gradient(135deg,#0e7490,#0891b2);color:#fff;border-radius:16px;padding:22px 24px;text-align:center;margin-bottom:20px;box-shadow:0 4px 16px rgba(8,145,178,.3)}}
.tk-count .cd{{font-size:2.8rem;font-weight:800;font-variant-numeric:tabular-nums;letter-spacing:1px;line-height:1.1}}
.tk-count .cd.go{{color:#fff59d;animation:tkpulse 1s infinite}}
@keyframes tkpulse{{50%{{opacity:.55}}}}
.tk-count .sub{{font-size:.9rem;opacity:.92;margin-top:8px}}
.tk-clocks{{display:flex;gap:20px;justify-content:center;margin-top:12px;font-size:.82rem;opacity:.9}}
.tk-clocks b{{font-variant-numeric:tabular-nums}}
.tk-target{{display:flex;gap:10px;align-items:center;justify-content:center;margin-top:12px;font-size:.85rem}}
.tk-target input{{width:auto;padding:5px 8px;font-size:.85rem}}
.tk-grid{{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin-bottom:20px}}
@media(max-width:720px){{.tk-grid{{grid-template-columns:1fr}}}}
.tk-card{{background:#fff;border-radius:12px;padding:18px 20px;box-shadow:0 1px 6px rgba(0,0,0,.08)}}
.tk-card h2{{margin:0 0 12px;font-size:1rem;border:none;padding:0;color:#0e5a6e}}
.tk-row{{display:flex;align-items:center;gap:8px;margin-bottom:8px}}
.tk-label{{width:120px;flex:none;font-size:.82rem;color:#555;font-weight:600}}
.tk-label .tk-hint{{display:block;font-size:.7rem;color:#999;font-weight:400}}
.tk-input{{flex:1;padding:6px 10px;font-size:.9rem}}
.tk-copy{{flex:none}}
.tk-copy.done{{background:#0e9f6e;color:#fff;border-color:#0e9f6e}}
.tk-bm{{display:inline-block;background:#e8530e;color:#fff;padding:10px 20px;border-radius:8px;font-weight:700;text-decoration:none;font-size:.95rem;cursor:grab}}
.tk-bm:hover{{background:#cf4708}}
.tk-steps{{font-size:.85rem;color:#555;line-height:1.9;margin:10px 0 0;padding-left:20px}}
.tk-check{{list-style:none;padding:0;margin:0;font-size:.88rem;line-height:2}}
.tk-check li::before{{content:'☐ ';color:#0891b2;font-weight:700}}
.tk-save-bar{{display:flex;gap:10px;align-items:center;margin-top:8px}}
</style>

<div class='tk-note'>
  ⚠️ 這是<b>手動搶票輔助工具</b>：倒數到時間後，按「開啟預約網站」→ 選 <b>{Enc(c.BookDate)} {Enc(c.WishTime)}</b> 的空位 →
  在填資料那頁點一下「自動填單」書籤，10 個欄位會瞬間填好，你只要<b>核對後自己按送出、刷卡付款</b>。
  最後的送出與信用卡付款一定要本人操作（工具不會、也不該幫你付款）。
</div>

<div class='tk-count'>
  <div class='cd' id='tk-cd'>--:--:--</div>
  <div class='sub' id='tk-when'></div>
  <div class='tk-clocks'>
    <span>🇹🇼 台灣 <b id='tk-tw'>--:--:--</b></span>
    <span>🇯🇵 日本 <b id='tk-jp'>--:--:--</b></span>
  </div>
  <div class='tk-target'>
    <span>搶票時間：</span>
    <input type='date' id='tk-tdate' value='{Enc(c.TargetDate)}' title='留空＝下一個到達的時間點（通常是明天）'>
    <input type='time' id='tk-ttime' value='{Enc(c.TargetTime)}'>
  </div>
</div>

<div class='tk-grid'>
  <div class='tk-card'>
    <h2>🎯 預約標的</h2>
    {Row("bookDate", "預約日期", c.BookDate, "boat 乘船日")}
    {Row("wishTime", "利用希望時間", c.WishTime)}
    {Row("boats", "利用ボート", c.Boats, "船隻數量")}
    <h2 style='margin-top:16px'>👥 人數</h2>
    {Row("adult", "大人", c.Adult, "中學生以上")}
    {Row("child", "子供", c.Child, "小學生")}
    {Row("infant", "幼児", c.Infant, "未就學兒童")}
  </div>
  <div class='tk-card'>
    <h2>🙋 個人資料</h2>
    {Row("kanji", "漢字 氏名", c.Kanji)}
    {Row("kanjiSei", "漢字 姓", c.KanjiSei, "拆欄位時用")}
    {Row("kanjiMei", "漢字 名", c.KanjiMei, "拆欄位時用")}
    {Row("kana", "フリガナ", c.Kana)}
    {Row("kanaSei", "フリガナ セイ", c.KanaSei, "拆欄位時用")}
    {Row("kanaMei", "フリガナ メイ", c.KanaMei, "拆欄位時用")}
    {Row("email", "メール", c.Email, "主要＋確認用同值")}
    {Row("tel", "電話番号", c.Tel)}
    {Row("zip", "郵便番号", c.Zip)}
    {Row("address", "住所", c.Address)}
  </div>
</div>

<div class='tk-card' style='margin-bottom:20px'>
  <h2>⚡ 自動填單書籤（先設定好、測試過）</h2>
  <p style='font-size:.85rem;color:#555;margin:0 0 10px'>把下面這顆按鈕<b>拖曳</b>到瀏覽器書籤列。到了填資料那一頁，點它一下就會自動填入上面所有欄位。</p>
  <a class='tk-bm' href=""{bookmarkletAttr}"" onclick=""return false"">🚣 高千穂自動填單</a>
  <ol class='tk-steps'>
    <li>把上方藍色欄位改成你要的值 → 按下方「💾 儲存設定」（存檔後書籤才會帶新值，需<b>重新拖曳</b>一次）。</li>
    <li>今天先<b>實測一次</b>：開預約網站、隨便點一個可預約時段進到填資料頁，點書籤看是否正確填好（不要送出）。</li>
    <li>若某欄沒填到／填錯，把哪一欄告訴我，我調整比對規則。</li>
  </ol>
  <details style='margin-top:8px'>
    <summary style='cursor:pointer;color:#0891b2;font-size:.82rem'>拖不動？改用手動建立書籤（複製程式碼）</summary>
    <textarea readonly style='width:100%;height:80px;margin-top:8px;font-size:.72rem;font-family:monospace' onclick='this.select()'>{Enc(bookmarklet)}</textarea>
  </details>
</div>

<div class='tk-card' style='margin-bottom:20px'>
  <h2>✅ 開搶前檢查清單</h2>
  <ul class='tk-check'>
    <li>信用卡準備好（Visa／Master／JCB… 支援 3D 驗證），手機能收簡訊 OTP</li>
    <li>已把「自動填單」書籤拖到書籤列，並實測過會正確填單</li>
    <li>網路穩定；先開好預約網站分頁</li>
    <li>確認搶的是 <b>{Enc(c.BookDate)} {Enc(c.WishTime)}</b>、船 {Enc(c.Boats)} 艘、共 {(int.TryParse(c.Adult, out var a) ? a : 0) + (int.TryParse(c.Infant, out var inf) ? inf : 0)} 人（1 艘含幼兒上限 4 人）</li>
    <li>時間到 → 重整日曆 → 點空位 → 書籤填單 → 核對 → 送出 → 刷卡</li>
  </ul>
</div>

<div class='tk-save-bar'>
  <button class='btn' id='tk-save'>💾 儲存設定</button>
  <span id='tk-msg' style='font-size:.85rem'></span>
</div>

<script>var TKCFG = {cfgJson};</script>
<script>{PageScript}</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("高千穂搶票", "life", "takachiho", body));
    }

    static IResult Save(TakachihoCfg req)
    {
        if (req == null) return Results.BadRequest();
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(CfgFile, JsonSerializer.Serialize(req, JsonOpts));
        return Results.Ok();
    }

    static TakachihoCfg Load()
    {
        if (File.Exists(CfgFile))
        {
            try { return JsonSerializer.Deserialize<TakachihoCfg>(File.ReadAllText(CfgFile), JsonOpts) ?? Default(); }
            catch { return Default(); }
        }
        return Default();
    }

    // 依使用者提供的資料建立預設值
    static TakachihoCfg Default() => new()
    {
        TargetTime = "08:00",
        TargetDate = "",
        BookDate = "2026-09-23",
        WishTime = "16:00",
        Boats = "1",
        Kanji = "周俊佑", KanjiSei = "周", KanjiMei = "俊佑",
        Kana = "チョウ チュンユウ", KanaSei = "チョウ", KanaMei = "チュンユウ",
        Email = "north77531@gmail.com",
        Tel = "912810137",
        Zip = "000-0000",
        Address = "Taiwan",
        Adult = "2", Child = "0", Infant = "2",
    };

    // ── 前端腳本：倒數、雙時區時鐘、複製、儲存 ──
    const string PageScript = """
    (function(){
      function pad(n){return String(n).padStart(2,'0');}
      function clockIn(tz){
        try{
          var s=new Date().toLocaleTimeString('en-GB',{timeZone:tz,hour12:false});
          return s;
        }catch(e){return '--:--:--';}
      }
      function target(){
        var d=document.getElementById('tk-tdate').value;
        var t=document.getElementById('tk-ttime').value||'08:00';
        var p=t.split(':'), h=+p[0], m=+p[1];
        var now=new Date(), tgt;
        if(d){ tgt=new Date(d+'T'+t+':00'); }
        else { tgt=new Date(now); tgt.setHours(h,m,0,0); if(tgt<=now) tgt.setDate(tgt.getDate()+1); }
        return tgt;
      }
      function tick(){
        document.getElementById('tk-tw').textContent=clockIn('Asia/Taipei');
        document.getElementById('tk-jp').textContent=clockIn('Asia/Tokyo');
        var tgt=target(), now=new Date(), diff=tgt-now;
        var cd=document.getElementById('tk-cd'), when=document.getElementById('tk-when');
        when.textContent='目標：'+tgt.toLocaleString('zh-TW',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false})+'（台灣時間）';
        if(diff<=0 && diff>-3600000){ cd.textContent='🔥 開搶！GO GO GO'; cd.classList.add('go'); return; }
        if(diff<=-3600000){ cd.textContent='已過時間'; cd.classList.remove('go'); return; }
        cd.classList.remove('go');
        var s=Math.floor(diff/1000), hh=Math.floor(s/3600), mm=Math.floor(s%3600/60), ss=s%60;
        var dd=Math.floor(hh/24);
        cd.textContent=(dd>0? dd+' 天 ':'')+pad(hh%24)+':'+pad(mm)+':'+pad(ss);
      }
      setInterval(tick,250); tick();

      // 複製
      document.querySelectorAll('.tk-copy').forEach(function(b){
        b.addEventListener('click',function(){
          var key=b.dataset.key;
          var inp=document.querySelector('.tk-input[data-key="'+key+'"]');
          if(!inp) return;
          var v=inp.value;
          var done=function(){ b.textContent='已複製'; b.classList.add('done'); setTimeout(function(){b.textContent='複製';b.classList.remove('done');},1200); };
          if(navigator.clipboard&&navigator.clipboard.writeText){ navigator.clipboard.writeText(v).then(done,function(){inp.select();document.execCommand('copy');done();}); }
          else { inp.select(); document.execCommand('copy'); done(); }
        });
      });

      // 儲存設定
      document.getElementById('tk-save').addEventListener('click',async function(){
        var g=function(k){var e=document.querySelector('.tk-input[data-key="'+k+'"]');return e?e.value.trim():'';};
        var body={
          targetTime:document.getElementById('tk-ttime').value||'08:00',
          targetDate:document.getElementById('tk-tdate').value||'',
          bookDate:g('bookDate'), wishTime:g('wishTime'), boats:g('boats'),
          kanji:g('kanji'), kanjiSei:g('kanjiSei'), kanjiMei:g('kanjiMei'),
          kana:g('kana'), kanaSei:g('kanaSei'), kanaMei:g('kanaMei'),
          email:g('email'), tel:g('tel'), zip:g('zip'), address:g('address'),
          adult:g('adult'), child:g('child'), infant:g('infant')
        };
        var msg=document.getElementById('tk-msg');
        var r=await fetch('/api/life/takachiho',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
        if(r.ok){ msg.textContent='✓ 已儲存，重新整理套用書籤新值…'; msg.style.color='#0e9f6e'; setTimeout(function(){location.reload();},700); }
        else { msg.textContent='儲存失敗'; msg.style.color='#c0392b'; }
      });
    })();
    """;

    // ── 自動填單書籤主體（在 D 物件已定義後執行）──
    // 依每個欄位周邊的 label／表格標題／name／id／placeholder 文字比對日文關鍵字，
    // 只填欄位、不送出；email 主要與確認欄同值。
    const string BookmarkletLogic = """
    function sv(el,v){var t=el.tagName.toLowerCase();if(t==='select'){var o=Array.from(el.options).find(function(x){return x.value===v||x.text.trim()===v||x.text.trim().indexOf(v)>=0;});if(!o)o=Array.from(el.options).find(function(x){return x.text.replace(/[^0-9]/g,'')===v&&v!=='';});if(o){el.value=o.value;}}else if(el.type==='checkbox'||el.type==='radio'){el.checked=true;}else{el.value=v;}el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));}
    function lt(el){var t='';if(el.id){var l=document.querySelector('label[for="'+el.id+'"]');if(l)t+=l.textContent;}var lp=el.closest('label');if(lp)t+=lp.textContent;var cell=el.closest('td,dd,div,li,p');if(cell){var pv=cell.previousElementSibling;if(pv)t+=pv.textContent;var pr=cell.parentElement?cell.parentElement.querySelector('th,dt,label'):null;if(pr)t+=pr.textContent;}t+=' '+(el.name||'')+' '+(el.id||'')+' '+(el.placeholder||'');return t.replace(/\s+/g,'');}
    var filled=0,emails=[];
    Array.from(document.querySelectorAll('input,select,textarea')).forEach(function(el){
      var ty=(el.type||'').toLowerCase();
      if(ty==='hidden'||ty==='submit'||ty==='button'||ty==='file'||ty==='password'||el.disabled||el.readOnly)return;
      var L=lt(el);
      if(ty==='email'||/(mail|メール|ﾒｰﾙ|Ｅメール|e-?mail)/i.test(L)){emails.push(el);return;}
      if(/(フリガナ|ふりがな|カナ|ｶﾅ|カタカナ|kana|furigana)/i.test(L)){
        if(/(セイ|姓|last|sei)/i.test(L)){sv(el,D.kanaSei);filled++;return;}
        if(/(メイ|名|first|mei)/i.test(L)){sv(el,D.kanaMei);filled++;return;}
        sv(el,D.kana);filled++;return;}
      if(el.tagName.toLowerCase()!=='select'&&/(氏名|お名前|名前|漢字|お客様名|name)/i.test(L)){
        if(/(姓|last|sei)/i.test(L)){sv(el,D.kanjiSei);filled++;return;}
        if(/(名|first|mei)/i.test(L)){sv(el,D.kanjiMei);filled++;return;}
        sv(el,D.kanji);filled++;return;}
      if(/(電話|TEL|でんわ|ﾃﾞﾝﾜ|phone|tel)/i.test(L)){sv(el,D.tel);filled++;return;}
      if(/(郵便|〒|zip|postal|ゆうびん)/i.test(L)){sv(el,D.zip);filled++;return;}
      if(/(住所|ご住所|address|じゅうしょ)/i.test(L)){sv(el,D.address);filled++;return;}
      if(/(大人|おとな|adult)/i.test(L)){sv(el,D.adult);filled++;return;}
      if(/(幼児|未就学|ようじ|infant|preschool)/i.test(L)){sv(el,D.infant);filled++;return;}
      if(/(子供|こども|小学生|child|kids)/i.test(L)){sv(el,D.child);filled++;return;}
      if(el.tagName.toLowerCase()==='select'&&/(利用希望時間|希望時間|時間|time)/i.test(L)){sv(el,D.wishTime);filled++;return;}
      if(/(ボート|ﾎﾞｰﾄ|boat|隻|艘)/i.test(L)){sv(el,D.boats);filled++;return;}
    });
    emails.forEach(function(el){sv(el,D.email);filled++;});
    alert('已自動填入 '+filled+' 個欄位。請核對所有欄位後，自行按下送出並完成付款。');
    """;
}

public record TakachihoCfg
{
    [property: JsonPropertyName("targetTime")] public string TargetTime { get; set; } = "08:00";
    [property: JsonPropertyName("targetDate")] public string TargetDate { get; set; } = "";
    [property: JsonPropertyName("bookDate")] public string BookDate { get; set; } = "";
    [property: JsonPropertyName("wishTime")] public string WishTime { get; set; } = "16:00";
    [property: JsonPropertyName("boats")] public string Boats { get; set; } = "1";
    [property: JsonPropertyName("kanji")] public string Kanji { get; set; } = "";
    [property: JsonPropertyName("kanjiSei")] public string KanjiSei { get; set; } = "";
    [property: JsonPropertyName("kanjiMei")] public string KanjiMei { get; set; } = "";
    [property: JsonPropertyName("kana")] public string Kana { get; set; } = "";
    [property: JsonPropertyName("kanaSei")] public string KanaSei { get; set; } = "";
    [property: JsonPropertyName("kanaMei")] public string KanaMei { get; set; } = "";
    [property: JsonPropertyName("email")] public string Email { get; set; } = "";
    [property: JsonPropertyName("tel")] public string Tel { get; set; } = "";
    [property: JsonPropertyName("zip")] public string Zip { get; set; } = "";
    [property: JsonPropertyName("address")] public string Address { get; set; } = "";
    [property: JsonPropertyName("adult")] public string Adult { get; set; } = "2";
    [property: JsonPropertyName("child")] public string Child { get; set; } = "0";
    [property: JsonPropertyName("infant")] public string Infant { get; set; } = "2";
}
