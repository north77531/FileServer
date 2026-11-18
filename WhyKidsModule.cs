using System.Collections.Concurrent;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

// 「為什麼小學堂」：給兒子（藍寶，小跑車）與女兒（豆豆，小狗）問問題的動畫機器人。
// 前端在 /why（同一頁含 SVG 動畫、語音辨識、語音朗讀），後端 /api/why/ask 轉呼叫 Claude。
// API 金鑰來自環境變數 ANTHROPIC_API_KEY（Fly 用 secrets 設定），不會送到瀏覽器。
public static class WhyKidsModule
{
    const string ModelId = "claude-opus-5-5";
    const int DailyLimitPerKid = 80;      // 每個孩子每天最多問幾題（防止玩到停不下來 / 費用暴衝）
    const int MaxHistory = 12;            // 最多帶幾則對話紀錄給 AI（讓「為什麼」可以一路追問）
    const int MaxTextLength = 400;

    static readonly ConcurrentDictionary<string, int> DailyCount = new();
    static AnthropicClient? _client;

    record Persona(string Name, string SystemPrompt);

    const string CommonRules = """
        你在一個給台灣幼兒的問答網頁裡，用語音和小朋友聊天。你說的每個字都會被語音唸出來。

        回答規則：
        - 一律用台灣繁體中文和台灣日常用語（例如「腳踏車」不是「自行車」、「垃圾車」、「捷運」）。
        - 回答要短：2 到 4 個短句，每句不超過 20 個字。小朋友聽完就要能懂。
        - 用小朋友生活中看得到、摸得到的東西來比喻。
        - 只用純文字。不要用 Markdown、條列符號、括號註解、表情符號、英文縮寫，因為會被唸出來。
        - 語氣溫暖、開心、有好奇心，可以偶爾稱讚「好棒的問題！」，但不要每次都講。
        - 回答完，偶爾（大約三次一次）用一個簡單的問題反問小朋友，或邀請他一起觀察，例如「下次下雨時，你可以看看……」。
        - 小朋友連續追問「為什麼」時，順著上一個答案再往下解釋一層，不要重複前面說過的話。
        - 聽不懂或像是語音辨識錯字時，猜最可能的意思來回答；真的猜不出來，就請他再說一次。

        安全規則（最重要）：
        - 內容一定要適合四、五歲小孩。不講恐怖、暴力、血腥或嚇人的細節。
        - 遇到死亡、生病、身體隱私、性、宗教、政治、家庭問題、或任何會讓小孩擔心的話題：用一兩句溫柔、正面、不嚇人的話回應，然後說「這是很重要的問題，我們一起去問爸爸媽媽好不好？」
        - 遇到危險的事（火、刀子、插座、藥、爬高、玩水、跟陌生人走、自己過馬路等）：清楚提醒「這很危險，一定要有大人在旁邊」。
        - 不問、也不要小朋友說出住址、學校、電話等個人資料。
        - 不知道答案或不確定時，就老實說「這個我也不太確定，我們可以一起問爸爸媽媽或查書」，不要亂編。
        - 你是可愛的卡通朋友，不是真的車子或狗。小朋友問你是不是真的，就說你是住在手機裡的卡通朋友。
        """;

    static readonly Dictionary<string, Persona> Personas = new()
    {
        ["car"] = new("藍寶", $"""
            你是「藍寶」，一台藍色、圓滾滾、很愛笑的卡通小跑車，造型像 BMW 小跑車。
            跟你聊天的是「哥哥」，5 歲男生，超級喜歡 BMW 和各種車子。
            說話時可以偶爾加一點車子的口頭禪，例如「叭叭！」「引擎轉起來！」，但不要每句都加。
            很適合時，用車子來比喻（引擎、輪胎、油門、雨刷、車燈、加油、充電、保養），但不要硬扯到車子。
            哥哥問車子或 BMW 的問題時，可以多講一點點有趣的知識，例如 BMW 的車頭有兩個像腎臟一樣的水箱罩、BMW 來自德國。

            {CommonRules}
            """),
        ["dog"] = new("豆豆", $"""
            你是「豆豆」，一隻咖啡色、耳朵垂垂、很溫柔的卡通小狗。
            跟你聊天的是「妹妹」，4 歲女生，最喜歡狗狗。
            說話時可以偶爾加一點小狗的聲音，例如「汪汪！」「我的尾巴搖起來了！」，但不要每句都加。
            很適合時，用狗狗來比喻（鼻子很靈、搖尾巴、吃飯、散步、睡覺、洗澡），但不要硬扯到狗。
            妹妹年紀比較小，用字要更簡單，句子更短，多一點鼓勵。

            {CommonRules}
            """),
    };

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/why", Page);
        app.MapPost("/api/why/ask", Ask).DisableAntiforgery();
    }

    static async Task Page(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync(WhyKidsPage.Html);
    }

    record ChatTurn(string Role, string Text);
    record AskRequest(string Kid, List<ChatTurn> Messages);

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    static async Task Ask(HttpContext ctx)
    {
        AskRequest? req;
        try { req = await JsonSerializer.DeserializeAsync<AskRequest>(ctx.Request.Body, JsonOpts); }
        catch (JsonException) { req = null; }

        if (req is null || !Personas.TryGetValue(req.Kid ?? "", out var persona) || !Valid(req.Messages))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("bad request");
            return;
        }

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("伺服器尚未設定 ANTHROPIC_API_KEY");
            return;
        }

        // 每日題數限制（以台灣日期計）
        var today = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd");
        var key = $"{today}:{req.Kid}";
        var count = DailyCount.AddOrUpdate(key, 1, (_, c) => c + 1);
        foreach (var old in DailyCount.Keys.Where(k => !k.StartsWith(today))) DailyCount.TryRemove(old, out _);
        if (count > DailyLimitPerKid)
        {
            ctx.Response.StatusCode = 429;
            await ctx.Response.WriteAsync("limit");
            return;
        }

        _client ??= new AnthropicClient { ApiKey = apiKey };

        var messages = req.Messages.TakeLast(MaxHistory).ToList();
        if (messages[0].Role != "user") messages.RemoveAt(0);
        var apiMessages = messages
            .Select(m => m.Role == "user"
                ? (BetaMessageParam)new BetaUserMessageParam { Content = m.Text.Trim() }
                : new BetaAssistantMessageParam { Content = m.Text.Trim() })
            .ToList();

        ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        async Task Send(object obj)
        {
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(obj, JsonOpts) + "\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }

        try
        {
            var parameters = new MessageCreateParams
            {
                Model = ModelId,
                MaxTokens = 2000,
                System = persona.SystemPrompt,
                Messages = apiMessages,
                // 聊天用低 effort：回得快，小朋友不用等
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                // 被安全分類器擋下時，由伺服器自動改用合適的備援模型回答
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
            };

            var refused = false;
            await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, ctx.RequestAborted))
            {
                if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                    await Send(new { t = text.Text });
                else if (ev.TryPickDelta(out var md) && md.Delta.StopReason == "refusal")
                    refused = true;
            }
            await Send(new { done = true, refused });
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // 小朋友按下按鈕打斷，正常結束
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[why] Claude error: {ex.GetType().Name}: {ex.Message}");
            if (!ctx.RequestAborted.IsCancellationRequested)
                await Send(new { error = true });
        }
    }

    static bool Valid(List<ChatTurn>? msgs)
    {
        if (msgs is null || msgs.Count == 0 || msgs.Count > 40) return false;
        if (msgs[^1].Role != "user") return false;
        for (int i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (m.Role is not ("user" or "assistant")) return false;
            if (string.IsNullOrWhiteSpace(m.Text) || m.Text.Length > (m.Role == "user" ? MaxTextLength : 2000)) return false;
            if (i > 0 && msgs[i - 1].Role == m.Role) return false;   // 必須一問一答交錯
        }
        return true;
    }
}
