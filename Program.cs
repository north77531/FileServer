using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("twse", client =>
{
    client.BaseAddress = new Uri("https://mis.twse.com.tw");
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("finmind", client =>
{
    client.BaseAddress = new Uri("https://api.finmindtrade.com");
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient();

var app = builder.Build();

StocksModule.MapRoutes(app);
DebtModule.MapRoutes(app);
HouseModule.MapRoutes(app);
CarModule.MapRoutes(app);

var sharedFolder = builder.Configuration["SharedFolder"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

// 首頁 → 導向股票庫存
app.MapGet("/", ctx =>
{
    ctx.Response.Redirect("/stocks");
    return Task.CompletedTask;
});

// 生活 → 開發中預留頁
app.MapGet("/life", async ctx =>
{
    var body = @"
<h1>🌿 生活</h1>
<div class='section' style='text-align:center;padding:60px 20px;color:#999'>
  <div style='font-size:3rem;margin-bottom:16px'>🌿</div>
  <p style='font-size:1.1rem;margin:0'>生活功能開發中，敬請期待</p>
</div>";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(SharedLayout.Page("生活", "life", "", body));
});

// 工作 → 檔案下載
app.MapGet("/work", async ctx =>
{
    if (!Directory.Exists(sharedFolder))
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsync($"資料夾不存在：{sharedFolder}");
        return;
    }

    var files = Directory.GetFiles(sharedFolder, "*", SearchOption.TopDirectoryOnly)
        .Select(f => new FileInfo(f))
        .OrderBy(f => f.Name)
        .ToList();

    var rows = files.Select(f =>
        "<tr><td><a href=\"/download/" + Uri.EscapeDataString(f.Name) + "\">" +
        System.Net.WebUtility.HtmlEncode(f.Name) +
        "</a></td><td style='text-align:right'>" + FormatSize(f.Length) +
        "</td><td>" + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</td></tr>"
    );

    var fileListHtml = files.Count == 0
        ? "<tr><td colspan='3' style='text-align:center;padding:30px;color:#999'>資料夾內沒有檔案</td></tr>"
        : string.Join("\n", rows);

    var body =
        "<h1>📁 檔案下載</h1>" +
        "<div style='color:#888;font-size:.85rem;margin:-12px 0 16px'>共享資料夾：" + System.Net.WebUtility.HtmlEncode(sharedFolder) + "</div>" +
        "<div class='table-wrap'><table><thead><tr><th>檔案名稱</th><th style='text-align:right'>大小</th><th>修改時間</th></tr></thead>" +
        "<tbody>" + fileListHtml + "</tbody></table></div>";

    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(SharedLayout.Page("檔案下載", "work", "files", body));
});

app.MapGet("/download/{filename}", async (string filename, HttpContext ctx) =>
{
    var safeName = Path.GetFileName(filename);
    var filePath = Path.Combine(sharedFolder, safeName);

    if (!File.Exists(filePath))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("檔案不存在");
        return;
    }

    var provider = new FileExtensionContentTypeProvider();
    if (!provider.TryGetContentType(filePath, out var contentType))
        contentType = "application/octet-stream";

    ctx.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(safeName)}";
    ctx.Response.ContentType = contentType;
    await ctx.Response.SendFileAsync(filePath);
});

app.Run();

static string FormatSize(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
    _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
};
