public static class SharedLayout
{
    static readonly (string href, string label, string key)[] MainItems =
    [
        ("/stocks", "📈 股票", "stocks"),
        ("/debt",   "💳 負債", "debt"),
        ("/house",  "🏠 房子", "house"),
        ("/car",    "🚗 車子", "car"),
        ("/life",   "🌿 生活", "life"),
        ("/work",   "💼 工作", "work"),
    ];

    static readonly Dictionary<string, (string href, string label, string key)[]> SubItems = new()
    {
        ["stocks"] = [
            ("/stocks",           "庫存",   "stocks"),
            ("/stocks/trade",     "新增交易", "trade"),
            ("/stocks/history",   "交易紀錄", "history"),
            ("/stocks/dividends", "歷史配息", "dividends"),
        ],
        ["debt"] = [
            ("/debt",     "貸款列表", "list"),
            ("/debt/add", "新增貸款", "add"),
        ],
        ["house"] = [
            ("/house/properties", "目前房產", "properties"),
            ("/house/realestate", "實價登入", "realestate"),
        ],
        ["car"] = [
            ("/car",              "車輛管理", "list"),
            ("/car/add",          "新增車輛", "add"),
            ("/car/expenses",     "花費紀錄", "expenses"),
            ("/car/expenses/add", "新增花費", "addexp"),
        ],
        ["life"] = [],
        ["work"] = [
            ("/work", "檔案下載", "files"),
        ],
    };

    public static string Nav(string cat, string sub = "")
    {
        var mainLinks = string.Join("", MainItems.Select(n =>
            $"<a href='{n.href}'{(n.key == cat ? " class='active'" : "")}>{n.label}</a>"));

        SubItems.TryGetValue(cat, out var subs);
        subs ??= [];
        var subLinks = string.Join("", subs.Select(n =>
            $"<a href='{n.href}'{(n.key == sub ? " class='active'" : "")}>{n.label}</a>"));

        var subNav = subs.Length > 0
            ? $"<nav class='sub-nav'>{subLinks}</nav>"
            : "<div class='sub-spacer'></div>";

        return $"<nav class='main-nav'>{mainLinks}</nav>{subNav}";
    }

    public static string Css => @"
*{box-sizing:border-box}
body{font-family:'Microsoft JhengHei','Noto Sans TC',sans-serif;max-width:1200px;margin:0 auto;padding:16px;background:#f5f7fa;color:#333}
.main-nav{display:flex;gap:4px;flex-wrap:wrap;margin-bottom:0}
.main-nav a{padding:9px 18px;border-radius:8px 8px 0 0;font-size:.9rem;font-weight:600;text-decoration:none;color:#666;background:#dde2e8;border:1px solid transparent;border-bottom:none;transition:background .15s}
.main-nav a:hover:not(.active){background:#c8d0da;color:#333}
.main-nav a.active{background:#fff;color:#0055cc;border-color:#ddd;border-bottom-color:#fff;margin-bottom:-1px;position:relative;z-index:1}
.sub-nav{background:#fff;padding:10px 16px;border-radius:0 8px 8px 8px;box-shadow:0 2px 8px rgba(0,0,0,.1);margin-bottom:24px;display:flex;gap:6px;flex-wrap:wrap;border:1px solid #ddd;position:relative;z-index:0}
.sub-spacer{height:20px}
.sub-nav a{padding:5px 16px;border-radius:20px;font-size:.88rem;text-decoration:none;color:#555;transition:background .15s}
.sub-nav a:hover{background:#f0f4ff;color:#0055cc}
.sub-nav a.active{background:#0055cc;color:#fff}
h1{font-size:1.4rem;margin:0 0 20px;color:#222}
h2{font-size:1.1rem;margin:20px 0 12px;color:#333;border-bottom:2px solid #eee;padding-bottom:6px}
table{width:100%;border-collapse:collapse;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 1px 4px rgba(0,0,0,.08)}
th{text-align:left;padding:10px 14px;background:#f0f4ff;color:#444;font-size:.85rem;white-space:nowrap}
td{padding:9px 14px;border-bottom:1px solid #f0f0f0;font-size:.9rem;vertical-align:middle}
tr:last-child td{border-bottom:none}
.tr-right th,.tr-right td{text-align:right}
.tr-right th:first-child,.tr-right td:first-child,.tr-right th:nth-child(2),.tr-right td:nth-child(2){text-align:left}
.pos{color:#c00}
.neg{color:#080}
.loading{color:#999;font-style:italic}
tfoot td{font-weight:600;background:#f8f8f8;border-top:2px solid #ddd}
.table-wrap{overflow-x:auto}
.form-card{background:#fff;border-radius:8px;padding:24px;box-shadow:0 1px 4px rgba(0,0,0,.08);max-width:600px}
.field{margin-bottom:16px}
label{display:block;font-size:.85rem;color:#555;margin-bottom:4px;font-weight:500}
input,select,textarea{width:100%;padding:9px 12px;border:1px solid #ddd;border-radius:6px;font-size:.95rem;font-family:inherit}
input:focus,select:focus,textarea:focus{outline:none;border-color:#0055cc;box-shadow:0 0 0 2px rgba(0,85,204,.15)}
.row2{display:grid;grid-template-columns:1fr 1fr;gap:12px}
.row3{display:grid;grid-template-columns:1fr 1fr 1fr;gap:12px}
.btn{padding:10px 24px;background:#0055cc;color:#fff;border:none;border-radius:6px;font-size:.95rem;cursor:pointer;font-weight:500;text-decoration:none;display:inline-block;line-height:1.4}
.btn:hover{background:#0044aa}
.btn-sm{padding:5px 14px;font-size:.82rem}
.btn-danger{background:#c0392b}
.btn-danger:hover{background:#a93226}
.btn-outline{background:#fff;color:#0055cc;border:1.5px solid #0055cc}
.btn-outline:hover{background:#f0f4ff}
.alert{padding:10px 14px;border-radius:6px;margin-bottom:16px;font-size:.9rem}
.alert.ok{background:#e6f4ea;color:#1e7e34;border:1px solid #b8dfc0}
.alert.err{background:#fde8e8;color:#c0392b;border:1px solid #f5b8b8}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:14px;margin-bottom:24px}
.card{background:#fff;border-radius:10px;padding:16px 20px;box-shadow:0 1px 4px rgba(0,0,0,.08)}
.card .lbl{font-size:.78rem;color:#888;margin-bottom:4px}
.card .val{font-size:1.3rem;font-weight:700}
.empty-state{text-align:center;padding:60px 20px;color:#999}
.empty-state .icon{font-size:3rem;margin-bottom:12px}
.tag{display:inline-block;padding:2px 10px;border-radius:12px;font-size:.8rem;font-weight:500;background:#eef2ff;color:#445}
.actions{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.section{background:#fff;border-radius:8px;padding:20px;box-shadow:0 1px 4px rgba(0,0,0,.08);margin-bottom:20px}
.calc-box{background:#f5f8ff;border:1px solid #dce6ff;border-radius:6px;padding:12px 14px;font-size:.88rem;margin-bottom:16px}
.calc-box div{display:flex;justify-content:space-between;padding:2px 0}
.calc-box .total{font-weight:600;border-top:1px solid #c0d0f0;margin-top:6px;padding-top:6px}
";

    public static string Page(string title, string cat, string sub, string body) =>
        $@"<!DOCTYPE html><html lang='zh-TW'><head>
<meta charset='UTF-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>{title}</title><style>{Css}</style></head><body>
{Nav(cat, sub)}{body}</body></html>";
}
