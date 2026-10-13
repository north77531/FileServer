// 棒棒集點卡 iOS App 的公開頁面（隱私權政策、支援）。
// App Store 規定這兩個網址必須不用登入就能開，所以 AuthModule 對 /stampcard 開放。
public static class StampCardModule
{
    public const string PathPrefix = "/stampcard";
    const string Email = "north77531@icloud.com";
    const string Effective = "2026-10-13";

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/stampcard", () => Results.Redirect("/stampcard/support"));
        app.MapGet("/stampcard/privacy", Privacy);
        app.MapGet("/stampcard/support", Support);
    }

    static async Task Privacy(HttpContext ctx)
    {
        var zh = $@"
<h1>隱私權政策</h1>
<p class='meta'>棒棒集點卡（Star Chart: Kids Rewards）· 生效日期：{Effective}</p>
<p>棒棒集點卡是一款給家長使用的獎勵集點卡 App。我們重視你和孩子的隱私，<b>本 App 不收集、不上傳、不分享任何個人資料。</b></p>
<h2>資料儲存在哪裡</h2>
<p>你建立的集點卡、小朋友名字、蓋章紀錄、獎勵內容與照片，<b>只儲存在你的裝置上</b>。我們沒有伺服器接收這些資料，也看不到它們。</p>
<h2>照片存取</h2>
<p>只有在你按下「選擇照片」時，App 才會開啟相簿，讓你挑一張照片當作集點卡頭像。選好的照片會複製一份存在 App 自己的資料夾中，不會上傳到任何地方。App 不會讀取相簿中的其他照片。</p>
<h2>不使用的東西</h2>
<ul>
  <li>沒有帳號註冊或登入</li>
  <li>沒有廣告</li>
  <li>沒有分析、追蹤或第三方統計工具</li>
  <li>不使用網路傳送你的資料</li>
</ul>
<h2>備份</h2>
<p>若你開啟了 iPhone 的 iCloud 備份或電腦備份，App 資料可能會依照 Apple 的備份設定一併備份。這是由 Apple 管理，我們無法存取。</p>
<h2>刪除資料</h2>
<p>你可以在 App 中刪除任何一張集點卡；刪除 App 會同時刪除所有資料。</p>
<h2>兒童隱私</h2>
<p>本 App 設計為由家長操作。由於我們不收集任何資料，也不會收集兒童的個人資料。</p>
<h2>政策變更</h2>
<p>若本政策有變更，會更新於此頁面並修改生效日期。</p>
<h2>聯絡我們</h2>
<p>對隱私權有任何問題，請來信 <a href='mailto:{Email}'>{Email}</a>。</p>";

        var en = $@"
<h1>Privacy Policy</h1>
<p class='meta'>Star Chart: Kids Rewards (棒棒集點卡) · Effective date: {Effective}</p>
<p>Star Chart is a reward stamp card app for parents. We care about your and your child's privacy: <b>this app does not collect, upload, or share any personal data.</b></p>
<h2>Where your data is stored</h2>
<p>The charts you create, children's names, stamp history, rewards, and photos are <b>stored only on your device</b>. We have no server that receives this data, and we cannot see it.</p>
<h2>Photo access</h2>
<p>The app opens your photo library only when you tap “Choose Photo”, so you can pick a photo as a chart's avatar. The chosen photo is copied into the app's own storage and is never uploaded. The app does not read any other photos in your library.</p>
<h2>What we don't use</h2>
<ul>
  <li>No account sign-up or login</li>
  <li>No advertising</li>
  <li>No analytics, tracking, or third-party SDKs</li>
  <li>No network transfer of your data</li>
</ul>
<h2>Backups</h2>
<p>If you use iCloud Backup or computer backups, app data may be included according to your Apple backup settings. These backups are managed by Apple and we cannot access them.</p>
<h2>Deleting your data</h2>
<p>You can delete any chart inside the app. Deleting the app removes all of its data.</p>
<h2>Children's privacy</h2>
<p>The app is designed to be operated by parents. Because we collect no data at all, we do not collect any personal information from children.</p>
<h2>Changes</h2>
<p>If this policy changes, we will update this page and its effective date.</p>
<h2>Contact</h2>
<p>For privacy questions, email <a href='mailto:{Email}'>{Email}</a>.</p>";

        await Write(ctx, "Privacy Policy · 隱私權政策", zh, en);
    }

    static async Task Support(HttpContext ctx)
    {
        var zh = $@"
<h1>棒棒集點卡 · 支援</h1>
<p>有問題、建議或發現錯誤，歡迎來信：<a href='mailto:{Email}'>{Email}</a>，我們會盡快回覆。</p>
<h2>常見問題</h2>
<h3>怎麼一次蓋好幾個章？</h3>
<p>按「🖐 蓋一個章」後，在「蓋幾個章」用 − / ＋ 調整數量，或按「一次蓋滿」。</p>
<h3>蓋錯了怎麼辦？</h3>
<p>按卡片上的「↩ 取消上一個」即可移除最後一個章。</p>
<h3>集滿之後呢？</h3>
<p>按「🎁 完成兌換」，卡片會標記為已兌換並保留紀錄；想再集一輪，按「↺ 重新開始」。</p>
<h3>怎麼修改或刪除集點卡？</h3>
<p>按卡片右上角「編輯」，可以修改名字、印章、點數、獎勵與照片；刪除按鈕在編輯頁最下方。</p>
<h3>換手機資料會不見嗎？</h3>
<p>資料只存在手機上。使用 iCloud 備份或電腦備份，再於新手機回復備份，資料就會一起轉移。直接刪除 App 會刪除所有資料。</p>
<h3>怎麼切換中文 / 英文？</h3>
<p>首頁左上角的「EN / 中」按鈕可以切換；預設會跟隨手機系統語言。</p>
<p class='links'><a href='/stampcard/privacy'>隱私權政策</a></p>";

        var en = $@"
<h1>Star Chart · Support</h1>
<p>Questions, suggestions, or found a bug? Email <a href='mailto:{Email}'>{Email}</a> and we'll get back to you as soon as possible.</p>
<h2>FAQ</h2>
<h3>How do I add several stamps at once?</h3>
<p>Tap “🖐 Add Stamp”, then use − / ＋ under “How many?”, or tap “Fill card”.</p>
<h3>I added a stamp by mistake.</h3>
<p>Tap “↩ Undo” on the chart to remove the last stamp.</p>
<h3>What happens when a chart is full?</h3>
<p>Tap “🎁 Redeem” to mark it as redeemed (the history is kept). To collect again, tap “↺ Start Over”.</p>
<h3>How do I edit or delete a chart?</h3>
<p>Tap “Edit” at the top right of the chart to change the name, stamp, goal, reward, or photo. The delete button is at the bottom of the edit screen.</p>
<h3>Will I lose my data when I change phones?</h3>
<p>Data is stored only on your phone. Back up with iCloud or a computer and restore the backup on your new phone to bring it along. Deleting the app deletes all data.</p>
<h3>How do I switch between English and Chinese?</h3>
<p>Use the “中 / EN” button at the top left of the home screen. By default the app follows your phone's language.</p>
<p class='links'><a href='/stampcard/privacy'>Privacy Policy</a></p>";

        await Write(ctx, "Support · 支援", zh, en);
    }

    // 中英文放在同一頁，依瀏覽器語言決定先顯示哪一個，右上角可切換
    static async Task Write(HttpContext ctx, string title, string zh, string en)
    {
        var html = $@"<!doctype html>
<html lang='zh-Hant'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>{title} · Star Chart</title>
<style>
:root{{--bg:#fdf8ef;--card:#fff;--text:#3d3420;--muted:#8a7d5c;--accent:#e8890c;--border:#ffe0a3}}
@media (prefers-color-scheme:dark){{:root{{--bg:#1f1b14;--card:#2a251c;--text:#f1e9d6;--muted:#b3a682;--accent:#ffb347;--border:#4a3f2a}}}}
*{{box-sizing:border-box}}
body{{margin:0;background:var(--bg);color:var(--text);font:16px/1.7 -apple-system,BlinkMacSystemFont,'PingFang TC','Noto Sans TC','Segoe UI',sans-serif}}
main{{max-width:720px;margin:0 auto;padding:24px 16px 60px}}
.top{{display:flex;justify-content:space-between;align-items:center;margin-bottom:16px}}
.brand{{font-weight:700;color:var(--accent)}}
.lang a{{color:var(--muted);text-decoration:none;margin-left:12px;font-size:.9rem}}
.lang a.on{{color:var(--accent);font-weight:700}}
article[lang=en]{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}}
article{{background:var(--card);border:2px solid var(--border);border-radius:16px;padding:8px 24px 20px}}
h1{{font-size:1.5rem;margin:16px 0 4px}}
h2{{font-size:1.1rem;margin:24px 0 4px;color:var(--accent)}}
h3{{font-size:1rem;margin:16px 0 2px}}
p,li{{margin:6px 0}}
.meta{{color:var(--muted);font-size:.9rem;margin-top:0}}
a{{color:var(--accent)}}
.links{{margin-top:24px}}
[hidden]{{display:none}}
</style>
</head>
<body>
<main>
  <div class='top'>
    <span class='brand'>⭐ Star Chart · 棒棒集點卡</span>
    <span class='lang'><a href='#zh' data-l='zh'>中文</a><a href='#en' data-l='en'>English</a></span>
  </div>
  <article id='zh'>{zh}</article>
  <article id='en' lang='en'>{en}</article>
</main>
<script>
function show(l) {{
  document.getElementById('zh').hidden = l !== 'zh';
  document.getElementById('en').hidden = l !== 'en';
  document.querySelectorAll('.lang a').forEach(a => a.classList.toggle('on', a.dataset.l === l));
  document.documentElement.lang = l === 'zh' ? 'zh-Hant' : 'en';
}}
const pick = () => location.hash === '#en' ? 'en' : location.hash === '#zh' ? 'zh'
  : ((navigator.language || '').toLowerCase().startsWith('zh') ? 'zh' : 'en');
window.addEventListener('hashchange', () => show(pick()));
show(pick());
</script>
</body>
</html>";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(html);
    }
}
