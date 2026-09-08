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
builder.Services.AddHttpClient("goodinfo", client =>
{
    client.BaseAddress = new Uri("https://goodinfo.tw");
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    client.DefaultRequestHeaders.Add("Accept-Language", "zh-TW,zh;q=0.9");
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient();

var app = builder.Build();

StocksModule.MapRoutes(app);
PledgeModule.MapRoutes(app);
DebtModule.MapRoutes(app);
HouseModule.MapRoutes(app);
CarModule.MapRoutes(app);
WorkLogModule.MapRoutes(app);
LifeModule.MapRoutes(app);
TakachihoModule.MapRoutes(app);
ExportModule.MapRoutes(app);

// 首頁 → 導向股票庫存
app.MapGet("/", ctx =>
{
    ctx.Response.Redirect("/stocks");
    return Task.CompletedTask;
});

app.Run();
