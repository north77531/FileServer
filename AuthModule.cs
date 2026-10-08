using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

// 單一帳號登入：帳密來自環境變數 AUTH_USER / AUTH_PASSWORD（Fly 用 secrets 設定）。
// 未設定密碼時一律拒絕登入（fail closed），避免網站意外裸奔。
public static class AuthModule
{
    static readonly string? AuthUser = Environment.GetEnvironmentVariable("AUTH_USER");
    static readonly string? AuthPassword = Environment.GetEnvironmentVariable("AUTH_PASSWORD");
    static bool Configured => !string.IsNullOrEmpty(AuthUser) && !string.IsNullOrEmpty(AuthPassword);

    // 防暴力破解：同一 IP 連續失敗 MaxFailures 次即鎖定 LockMinutes 分鐘
    const int MaxFailures = 5;
    const int LockMinutes = 15;
    static readonly ConcurrentDictionary<string, (int Count, DateTime LockedUntil)> Failures = new();

    public static void AddServices(WebApplicationBuilder builder)
    {
        // 加密金鑰存在持久化磁碟，重新部署後登入狀態不會失效
        var dataDir = Environment.GetEnvironmentVariable("DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
            .SetApplicationName("FileServer");

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "fs_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                o.LoginPath = "/login";
            });
    }

    public static void UseAuth(WebApplication app)
    {
        // Fly 在前面終止 TLS，要信任 X-Forwarded-Proto 才能讓 cookie 帶 Secure
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Headers["X-Forwarded-Proto"] == "https") ctx.Request.Scheme = "https";
            return next();
        });
        app.UseAuthentication();

        // 除登入頁外，所有頁面與 API 都必須先登入
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (path.Equals("/login", StringComparison.OrdinalIgnoreCase) || ctx.User.Identity?.IsAuthenticated == true)
            {
                await next();
                return;
            }
            if (path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("請先登入");
                return;
            }
            var returnUrl = ctx.Request.Path + ctx.Request.QueryString;
            ctx.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        });

        app.MapGet("/login", LoginPage);
        app.MapPost("/login", Login).DisableAntiforgery();
        app.MapGet("/logout", Logout);
    }

    static async Task LoginPage(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true) { ctx.Response.Redirect("/"); return; }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(BuildLoginHtml(ctx.Request.Query["returnUrl"], null));
    }

    static async Task Login(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        string user = form["username"].ToString(), pass = form["password"].ToString();
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var ip = ClientIp(ctx);
        ctx.Response.ContentType = "text/html; charset=utf-8";

        if (Failures.TryGetValue(ip, out var f) && f.LockedUntil > DateTime.UtcNow)
        {
            var mins = (int)Math.Ceiling((f.LockedUntil - DateTime.UtcNow).TotalMinutes);
            await ctx.Response.WriteAsync(BuildLoginHtml(returnUrl, $"登入失敗次數過多，請 {mins} 分鐘後再試"));
            return;
        }

        if (!Configured)
        {
            await ctx.Response.WriteAsync(BuildLoginHtml(returnUrl, "伺服器尚未設定登入帳密（AUTH_USER / AUTH_PASSWORD）"));
            return;
        }

        if (!(Same(user, AuthUser!) & Same(pass, AuthPassword!)))
        {
            var count = (f.LockedUntil > DateTime.UtcNow ? 0 : f.Count) + 1;
            Failures[ip] = count >= MaxFailures ? (0, DateTime.UtcNow.AddMinutes(LockMinutes)) : (count, DateTime.MinValue);
            await Task.Delay(1000);
            await ctx.Response.WriteAsync(BuildLoginHtml(returnUrl, "帳號或密碼錯誤"));
            return;
        }

        Failures.TryRemove(ip, out _);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = form["remember"] == "on" });
        ctx.Response.Redirect(returnUrl);
    }

    static async Task Logout(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        ctx.Response.Redirect("/login");
    }

    // 以雜湊後的固定長度做定時比較，避免由回應時間推測帳密
    static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    // 只允許站內相對路徑，避免被拿來導向外部網站
    static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    static string ClientIp(HttpContext ctx) =>
        ctx.Request.Headers["Fly-Client-IP"].FirstOrDefault()
        ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    static string BuildLoginHtml(string? returnUrl, string? error) => $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>登入</title>
<style>
*{{box-sizing:border-box}}
body{{font-family:'Microsoft JhengHei','Noto Sans TC',sans-serif;background:#f5f7fa;color:#333;margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:16px}}
form{{background:#fff;border-radius:12px;box-shadow:0 2px 12px rgba(0,0,0,.1);padding:32px 28px;width:100%;max-width:360px}}
h1{{font-size:1.3rem;margin:0 0 20px;text-align:center}}
label{{display:block;font-size:.88rem;font-weight:600;margin:14px 0 6px}}
input[type=text],input[type=password]{{width:100%;padding:10px 12px;border:1px solid #ccc;border-radius:6px;font-size:1rem}}
input:focus{{outline:none;border-color:#0055cc}}
.remember{{display:flex;align-items:center;gap:6px;font-weight:400;margin-top:14px}}
button{{width:100%;margin-top:20px;padding:11px;border:none;border-radius:6px;background:#0055cc;color:#fff;font-size:1rem;font-weight:600;cursor:pointer}}
button:hover{{background:#0044aa}}
.err{{background:#fdecea;color:#b00;border-radius:6px;padding:9px 12px;font-size:.88rem;margin-bottom:6px}}
</style></head><body>
<form method='post' action='/login'>
<h1>🔒 個人網站登入</h1>
{(error == null ? "" : $"<div class='err'>{WebUtility.HtmlEncode(error)}</div>")}
<input type='hidden' name='returnUrl' value='{WebUtility.HtmlEncode(SafeReturnUrl(returnUrl))}'>
<label for='username'>帳號</label>
<input type='text' id='username' name='username' autocomplete='username' required autofocus>
<label for='password'>密碼</label>
<input type='password' id='password' name='password' autocomplete='current-password' required>
<label class='remember'><input type='checkbox' name='remember' checked> 記住我（30 天）</label>
<button type='submit'>登入</button>
</form>
</body></html>";
}
