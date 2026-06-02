using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var sharedFolder = builder.Configuration["SharedFolder"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

app.MapGet("/", async (HttpContext ctx) =>
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
        "</a></td><td>" + FormatSize(f.Length) +
        "</td><td>" + f.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</td></tr>"
    );

    var fileListHtml = files.Count == 0
        ? "<tr><td colspan='3' class='empty'>資料夾內沒有檔案</td></tr>"
        : string.Join("\n          ", rows);

    var css = @"
        body { font-family: sans-serif; max-width: 900px; margin: 40px auto; padding: 0 16px; }
        h1 { font-size: 1.5rem; margin-bottom: 4px; }
        .path { color: #666; font-size: 0.85rem; margin-bottom: 20px; }
        table { width: 100%; border-collapse: collapse; }
        th { text-align: left; border-bottom: 2px solid #ddd; padding: 8px 12px; background: #f5f5f5; }
        td { padding: 8px 12px; border-bottom: 1px solid #eee; }
        a { color: #0066cc; text-decoration: none; }
        a:hover { text-decoration: underline; }
        .empty { color: #999; padding: 20px 12px; }";

    var html =
        "<!DOCTYPE html><html lang=\"zh-TW\"><head>" +
        "<meta charset=\"UTF-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        "<title>檔案下載</title>" +
        "<style>" + css + "</style></head><body>" +
        "<h1>檔案下載</h1>" +
        "<div class=\"path\">共享資料夾：" + System.Net.WebUtility.HtmlEncode(sharedFolder) + "</div>" +
        "<table><thead><tr><th>檔案名稱</th><th>大小</th><th>修改時間</th></tr></thead>" +
        "<tbody>" + fileListHtml + "</tbody></table>" +
        "</body></html>";

    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.WriteAsync(html);
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
