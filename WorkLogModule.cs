using System.Text.Json;
using System.Text.Json.Serialization;

public static class WorkLogModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string LogsFile = Path.Combine(DataDir, "work_logs.json");
    static readonly string FieldOptionsFile = Path.Combine(DataDir, "worklog_field_options.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // 可自由輸入的下拉選單欄位（使用者可自行新增／刪除選項）
    static readonly string[] ComboFieldKeys = ["epicNames", "labels", "issuers"];

    // ── 預設值與選項 ────────────────────────────────────────────────────────
    const string DefProject = "CyntecITR(ITCYTER)";
    const string DefIssueType = "Epic";
    const string DefEpicCategory = "Request";
    const string DefItPlatform = "ERP-Enterprise Resource Planning";
    const string DefModule = "FI/TR/RE";
    const string DefLinkedIssue = "is child of";
    const string DefIssue = "CIPR-3816";
    const string DefItpm = "ERIC.CY.CHOU";

    // DS4X Epic 專屬帶入值
    const string Ds4xEpicName = "DS4X";
    const string Ds4xLinkedIssue = "is child of";
    const string Ds4xIssue = "CIPR-3169";
    const string Ds4xRequestIssuer = "ZOE.HY.LEE";

    // 客戶標籤 Epic 專屬帶入值
    const string CustomerTagEpicName = "客戶標籤";
    const string CustomerTagModule = "SD";

    // Dashboard → 工作紀錄明細 深連結時，代表「該欄位為空白」的保留值
    const string BlankMarker = "__BLANK__";

    // Labels 為自由輸入欄位，可能一次填多個（如「ME21N、VA01」）
    static readonly char[] LabelSeparators = ['、', ',', '，', ';', '；', '/'];
    const string NoLabelMarker = "(未填 Label)";

    static List<string> SplitLabels(string? raw)
    {
        var parts = (raw ?? "")
            .Split(LabelSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();
        return parts.Count == 0 ? [NoLabelMarker] : parts;
    }

    static readonly string[] SummaryOptions =
        ["Change Requests", "Issue Solving", "Operation Tickets", "Training & Enablement"];
    static readonly string[] EpicCategoryOptions =
        ["None", "Project", "Request", "Issue", "Enhancement"];
    static readonly string[] ModuleOptions =
        ["ALL", "AUDIT", "CO", "SD", "MM", "PP", "FI/TR/RE", "FMR", "IMP/EXP", "QM"];

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/work/log", LogsPage);
        app.MapGet("/work/log/add", AddLogPage);
        app.MapGet("/work/log/{id}/edit", EditLogPage);
        app.MapGet("/work/dashboard", DashboardPage);

        app.MapPost("/api/worklog", AddLog);
        app.MapPut("/api/worklog/{id}", UpdateLog);
        app.MapDelete("/api/worklog/{id}", DeleteLog);
        app.MapGet("/api/worklog/options", GetFieldOptions);
        app.MapDelete("/api/worklog/options", DeleteFieldOption);
        app.MapPost("/api/worklog/create-folder", CreateFolder);
    }

    // ── 日期篩選（供工作紀錄／Dashboard 頁面互相連結使用）──────────────────────

    // 只接受乾淨的數字/日期格式，避免查詢字串內容被直接嵌入頁面 <script> 造成注入
    static (string Year, string Month, string Week, string Date) ParseDateFilter(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        string year = q["year"].ToString();
        if (year.Length != 4 || !year.All(char.IsAsciiDigit)) year = "";

        string month = q["month"].ToString();
        month = int.TryParse(month, out var m) && m is >= 1 and <= 12 ? m.ToString() : "";

        string week = q["week"].ToString();
        week = int.TryParse(week, out var w) && w is >= 1 and <= 53 ? w.ToString() : "";

        string date = q["date"].ToString();
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _)) date = "";

        return (year, month, week, date);
    }

    static string BuildDateQuery((string Year, string Month, string Week, string Date) f)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(f.Date)) parts.Add("date=" + Uri.EscapeDataString(f.Date));
        else
        {
            if (!string.IsNullOrEmpty(f.Year)) parts.Add("year=" + f.Year);
            if (!string.IsNullOrEmpty(f.Month)) parts.Add("month=" + f.Month);
            if (!string.IsNullOrEmpty(f.Week)) parts.Add("week=" + f.Week);
        }
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    static List<WorkLog> FilterByDate(List<WorkLog> logs, (string Year, string Month, string Week, string Date) f)
    {
        if (!string.IsNullOrEmpty(f.Date))
            return logs.Where(l => l.PlanStartDate == f.Date).ToList();
        if (string.IsNullOrEmpty(f.Year) && string.IsNullOrEmpty(f.Month) && string.IsNullOrEmpty(f.Week))
            return logs;
        return logs.Where(l =>
        {
            if (!DateTime.TryParse(l.PlanStartDate, out var d)) return false;
            if (!string.IsNullOrEmpty(f.Year) && d.Year.ToString() != f.Year) return false;
            if (!string.IsNullOrEmpty(f.Month) && d.Month.ToString() != f.Month) return false;
            if (!string.IsNullOrEmpty(f.Week) && System.Globalization.ISOWeek.GetWeekOfYear(d).ToString() != f.Week) return false;
            return true;
        }).ToList();
    }

    // ── Pages ─────────────────────────────────────────────────────────────

    static async Task LogsPage(HttpContext ctx)
    {
        var logs = LoadLogs().OrderByDescending(l => l.PlanStartDate).ThenByDescending(l => l.CreatedAt).ToList();
        var totalMin = logs.Sum(l => l.Minutes);
        var linkFilter = ParseDateFilter(ctx);
        var initEpicName = ctx.Request.Query["epicName"].ToString();
        var initSummary = ctx.Request.Query["summary"].ToString();
        var initModule = ctx.Request.Query["module"].ToString();
        var initIssuer = ctx.Request.Query["issuer"].ToString();

        string DimLabel(string v) => v == BlankMarker ? "(空白)" : v;
        var linkInfoParts = new List<string>();
        if (!string.IsNullOrEmpty(initEpicName)) linkInfoParts.Add("Epic Name = " + DimLabel(initEpicName));
        if (!string.IsNullOrEmpty(initSummary)) linkInfoParts.Add("Summary = " + DimLabel(initSummary));
        if (!string.IsNullOrEmpty(initModule)) linkInfoParts.Add("Module = " + DimLabel(initModule));
        if (!string.IsNullOrEmpty(initIssuer)) linkInfoParts.Add("Request Issuer = " + DimLabel(initIssuer));
        var linkInfoHtml = linkInfoParts.Count == 0 ? "" :
            $"<div id='link-filter-info' style='color:#0055cc;font-size:.82rem;margin:-4px 0 10px'>🔗 從 Dashboard 篩選：{System.Net.WebUtility.HtmlEncode(string.Join("、", linkInfoParts))}</div>";

        string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var rows = logs.Count == 0
            ? "<tr><td colspan='20' style='text-align:center;padding:30px;color:#999'>尚無工作紀錄</td></tr>"
            : string.Join("", logs.Select(l => $@"<tr data-id='{l.Id}'>
  <td>{l.PlanStartDate}</td>
  <td>{E(l.Project)}</td>
  <td>{E(l.IssueType)}</td>
  <td>{E(l.Summary)}</td>
  <td>{E(l.EpicName)}</td>
  <td>{E(l.EpicCategory)}</td>
  <td>{E(l.ItPlatform)}</td>
  <td><span class='tag'>{E(l.Module)}</span></td>
  <td>{E(l.RequirementId)}</td>
  <td>{E(l.JiraIssue)}</td>
  <td>{E(l.BenefitDescription)}</td>
  <td>{E(l.Labels)}</td>
  <td>{E(l.Description)}</td>
  <td>{E(l.LinkedIssue)}</td>
  <td>{E(l.Issue)}</td>
  <td>{E(l.Itpm)}</td>
  <td>{E(l.RequestIssuer)}</td>
  <td style='text-align:right;font-weight:600'>{l.Minutes:N0}</td>
  <td>{E(l.CreatedAt)}</td>
  <td><div class='actions'>
    <a href='/work/log/{l.Id}/edit' class='btn btn-sm btn-outline'>編輯</a>
    <a href='/work/log/add?copy={l.Id}' class='btn btn-sm btn-outline'>複製</a>
    <button class='btn btn-danger btn-sm' onclick='del(""{l.Id}"")'>刪除</button>
  </div></td>
</tr>"));

        var fullLogsJson = JsonSerializer.Serialize(
            logs.ToDictionary(l => l.Id, l => l), new JsonSerializerOptions());

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>📝 工作紀錄</h1>
  <button class='btn btn-sm btn-outline' onclick='exportFullLog(this)'>⬇ 下載Excel</button>
  <button class='btn btn-sm btn-outline' onclick='exportFullLogCsv()'>⬇ 下載CSV</button>
  <a href='/work/log/add' class='btn'>＋ 新增紀錄</a>
</div>
<div class='cards'>
  <div class='card'><div class='lbl'>紀錄筆數</div><div class='val' id='sum-count'>{logs.Count} 筆</div></div>
  <div class='card'><div class='lbl'>總耗時</div><div class='val pos' id='sum-min'>{totalMin:N0} 分</div></div>
  <div class='card'><div class='lbl'>約計</div><div class='val' style='font-size:1rem' id='sum-hr'>{totalMin / 60.0:F1} 小時</div></div>
</div>
<div class='actions' style='margin-bottom:10px;gap:8px'>
  <label style='display:flex;align-items:center;gap:4px;font-size:.85rem;color:#666'>年
    <select id='filterYear'><option value=''>全部</option></select>
  </label>
  <label style='display:flex;align-items:center;gap:4px;font-size:.85rem;color:#666'>月
    <select id='filterMonth'><option value=''>全部</option></select>
  </label>
  <label style='display:flex;align-items:center;gap:4px;font-size:.85rem;color:#666'>週
    <select id='filterWeek'><option value=''>全部</option></select>
  </label>
  <button class='btn btn-sm btn-outline' id='btnToday' onclick='toggleTodayFilter()'>📅 只看今天</button>
  <button class='btn btn-sm btn-outline' onclick='clearAllLinkFilters()'>清除篩選</button>
</div>
{linkInfoHtml}
<div class='table-wrap'>
<table id='log-table'>
<thead><tr>
  <th>日期</th><th>Project</th><th>Issue Type</th><th>Summary</th><th>Epic Name</th><th>Epic Category</th><th>IT Platform</th><th>Module</th><th>Requirement ID</th><th>Jira Issue</th><th>Benefit Description</th><th>Labels</th><th>Description</th><th>Linked Issue</th><th>Issue</th><th>ITPM</th><th>Request Issuer</th><th style='text-align:right'>耗時(分)</th><th>建立時間</th><th></th>
</tr></thead>
<tbody>{rows}</tbody>
<tfoot><tr>
  <td>篩選合計</td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td><td></td>
  <td style='text-align:right;font-weight:600' id='tf-min'>{totalMin:N0}</td>
  <td></td><td></td>
</tr></tfoot>
</table>
</div>
<div id='msg' style='margin-top:12px'></div>
<script>
const fullLogsData = {fullLogsJson};
const fullLogHeaders = ['日期','Project','Issue Type','Summary','Epic Name','Epic Category','IT Platform','Module','Requirement ID','Jira Issue','Benefit Description','Labels','Description','Linked Issue','Issue','ITPM','Request Issuer','耗時(分)','建立時間'];
function logToRow(l) {{
  return [l.planStartDate, l.project, l.issueType, l.summary, l.epicName, l.epicCategory, l.itPlatform,
    l.module, l.requirementId, l.jiraIssue, l.benefitDescription, l.labels, l.description, l.linkedIssue, l.issue,
    l.itpm, l.requestIssuer, String(l.minutes), l.createdAt];
}}
async function exportFullLog(btn) {{
  const rows = [];
  for (const r of document.getElementById('log-table').tBodies[0].rows) {{
    if (r.style.display === 'none') continue;
    const log = fullLogsData[r.dataset.id];
    if (log) rows.push(logToRow(log));
  }}
  const oldText = btn.textContent;
  btn.textContent = '匯出中...'; btn.disabled = true;
  try {{
    const res = await fetch('/api/export/excel', {{
      method: 'POST',
      headers: {{ 'Content-Type': 'application/json' }},
      body: JSON.stringify({{ fileName: '工作紀錄', sheetName: '工作紀錄', headers: fullLogHeaders, rows }})
    }});
    if (!res.ok) {{ alert('匯出失敗'); return; }}
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = '工作紀錄.xlsx';
    document.body.appendChild(a); a.click(); document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }} finally {{
    btn.textContent = oldText; btn.disabled = false;
  }}
}}
function exportFullLogCsv() {{
  const rows = [];
  for (const r of document.getElementById('log-table').tBodies[0].rows) {{
    if (r.style.display === 'none') continue;
    const log = fullLogsData[r.dataset.id];
    if (log) rows.push(logToRow(log));
  }}
  downloadCsv('工作紀錄', fullLogHeaders, rows);
}}
async function del(id) {{
  if (!confirm('確定刪除此紀錄？')) return;
  const r = await fetch('/api/worklog/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">刪除失敗</div>';
}}

// ── 依年/月/週篩選 ──
function isoWeek(dateStr) {{
  const d = new Date(dateStr + 'T00:00:00');
  d.setDate(d.getDate() + 3 - (d.getDay() + 6) % 7);
  const week1 = new Date(d.getFullYear(), 0, 4);
  return 1 + Math.round(((d - week1) / 86400000 - 3 + (week1.getDay() + 6) % 7) / 7);
}}
(function populateDateFilters() {{
  const years = new Set(), months = new Set(), weeks = new Set();
  for (const id in fullLogsData) {{
    const ds = fullLogsData[id].planStartDate;
    if (!ds) continue;
    const d = new Date(ds + 'T00:00:00');
    years.add(d.getFullYear());
    months.add(d.getMonth() + 1);
    weeks.add(isoWeek(ds));
  }}
  const fill = (selId, vals, label) => {{
    const sel = document.getElementById(selId);
    for (const v of [...vals].sort((a, b) => a - b)) {{
      const opt = document.createElement('option');
      opt.value = String(v); opt.textContent = label ? label(v) : String(v);
      sel.appendChild(opt);
    }}
  }};
  fill('filterYear', years, y => y + ' 年');
  fill('filterMonth', months, m => m + ' 月');
  fill('filterWeek', weeks, w => '第 ' + w + ' 週');
}})();
let filterExactDate = '{linkFilter.Date}';
// 從網址帶入初始篩選（例如從 Dashboard 點擊各項紀錄數連結進入）
const BLANK_MARKER = '{BlankMarker}';
let initEpicName = {JsonSerializer.Serialize(initEpicName)};
let initSummary = {JsonSerializer.Serialize(initSummary)};
let initModule = {JsonSerializer.Serialize(initModule)};
let initIssuer = {JsonSerializer.Serialize(initIssuer)};
if (filterExactDate) {{
  document.getElementById('btnToday').classList.remove('btn-outline');
  document.getElementById('btnToday').classList.add('btn-danger');
}} else {{
  if ('{linkFilter.Year}') document.getElementById('filterYear').value = '{linkFilter.Year}';
  if ('{linkFilter.Month}') document.getElementById('filterMonth').value = '{linkFilter.Month}';
  if ('{linkFilter.Week}') document.getElementById('filterWeek').value = '{linkFilter.Week}';
}}
function todayStr() {{ return new Date().toLocaleDateString('sv-SE'); }} // yyyy-MM-dd（本地時區）
function toggleTodayFilter() {{
  filterExactDate = filterExactDate ? '' : todayStr();
  document.getElementById('btnToday').classList.toggle('btn-outline', !filterExactDate);
  document.getElementById('btnToday').classList.toggle('btn-danger', !!filterExactDate);
  logTable.run();
}}
function clearTodayFilter() {{
  filterExactDate = '';
  document.getElementById('btnToday').classList.add('btn-outline');
  document.getElementById('btnToday').classList.remove('btn-danger');
}}
function clearAllLinkFilters() {{
  ['filterYear', 'filterMonth', 'filterWeek'].forEach(id => document.getElementById(id).value = '');
  clearTodayFilter();
  initEpicName = ''; initSummary = ''; initModule = ''; initIssuer = '';
  const info = document.getElementById('link-filter-info');
  if (info) info.remove();
  logTable.run();
}}
function dimMatch(want, actual) {{
  if (!want) return true;
  return want === BLANK_MARKER ? !actual : actual === want;
}}
function dateExtraFilter(row) {{
  const log = fullLogsData[row.dataset.id];
  if (!log) return true;
  if (!dimMatch(initEpicName, log.epicName)) return false;
  if (!dimMatch(initSummary, log.summary)) return false;
  if (!dimMatch(initModule, log.module)) return false;
  if (!dimMatch(initIssuer, log.requestIssuer)) return false;
  if (!log.planStartDate) return true;
  if (filterExactDate) return log.planStartDate === filterExactDate;
  const d = new Date(log.planStartDate + 'T00:00:00');
  const fy = document.getElementById('filterYear').value;
  const fm = document.getElementById('filterMonth').value;
  const fw = document.getElementById('filterWeek').value;
  if (fy && fy !== String(d.getFullYear())) return false;
  if (fm && fm !== String(d.getMonth() + 1)) return false;
  if (fw && fw !== String(isoWeek(log.planStartDate))) return false;
  return true;
}}
['filterYear', 'filterMonth', 'filterWeek'].forEach(id =>
  document.getElementById(id).addEventListener('change', () => {{ clearTodayFilter(); logTable.run(); }}));

// 全欄位皆可於『欄位設定』挑選；預設僅顯示常用欄位，其餘預設隱藏
setupColumns('log-table', 'worklog-log', {{
  labels: {{ 19: '操作' }},
  defaultHidden: [1, 2, 5, 6, 10, 12, 13, 14, 15, 16, 18]
}});
const logTable = initTable('log-table', {{
  cols: 20,
  noFilter: [17, 19],
  sumCols: [{{col: 17, id: 'tf-min'}}],
  extraFilter: dateExtraFilter,
  onFilter: function(vis) {{
    let sum = 0;
    for (const r of vis) {{ const lg = fullLogsData[r.dataset.id]; sum += lg ? lg.minutes : 0; }}
    const countEl = document.getElementById('sum-count');
    if (countEl) countEl.textContent = vis.length.toLocaleString('zh-TW') + ' 筆';
    const el = document.getElementById('sum-min');
    if (el) el.textContent = Math.round(sum).toLocaleString('zh-TW') + ' 分';
    const hr = document.getElementById('sum-hr');
    if (hr) hr.textContent = (sum / 60).toFixed(1) + ' 小時';
  }}
}});
if ('{linkFilter.Year}' || '{linkFilter.Month}' || '{linkFilter.Week}' || '{linkFilter.Date}' || initEpicName || initSummary || initModule || initIssuer) logTable.run();
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("工作紀錄", "work", "log", body));
    }

    static async Task DashboardPage(HttpContext ctx)
    {
        var allLogs = LoadLogs();

        if (allLogs.Count == 0)
        {
            var emptyBody = "<h1>📊 工作紀錄 Dashboard</h1><div class='empty-state'><div class='icon'>📭</div><p>尚無工作紀錄</p></div>";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(SharedLayout.Page("Dashboard", "work", "dashboard", emptyBody));
            return;
        }

        var filter = ParseDateFilter(ctx);
        var logs = FilterByDate(allLogs, filter);

        // 明細連結：帶入目前日期篩選 + 指定維度（epicName/summary/module/issuer）的精確篩選
        string DimLink(string param, string value)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(filter.Year)) parts.Add("year=" + filter.Year);
            if (!string.IsNullOrEmpty(filter.Month)) parts.Add("month=" + filter.Month);
            if (!string.IsNullOrEmpty(filter.Week)) parts.Add("week=" + filter.Week);
            if (!string.IsNullOrEmpty(filter.Date)) parts.Add("date=" + Uri.EscapeDataString(filter.Date));
            parts.Add(param + "=" + Uri.EscapeDataString(value));
            return "/work/log?" + string.Join("&", parts);
        }

        // 月份長條圖連結：改用該月份本身作為日期篩選（取代週/日篩選，避免衝突）
        string MonthLink(string monthKey)
        {
            var parts = monthKey.Split('-');
            return parts.Length == 2 && int.TryParse(parts[1], out var m)
                ? $"/work/log?year={parts[0]}&month={m}"
                : "/work/log";
        }

        var dateParsed = allLogs.Select(l => (Log: l, Ok: DateTime.TryParse(l.PlanStartDate, out var d), Date: d)).Where(x => x.Ok).ToList();
        var yearOpts = dateParsed.Select(x => x.Date.Year).Distinct().OrderBy(y => y).ToList();
        var monthOpts = dateParsed.Select(x => x.Date.Month).Distinct().OrderBy(m => m).ToList();
        var weekOpts = dateParsed.Select(x => System.Globalization.ISOWeek.GetWeekOfYear(x.Date)).Distinct().OrderBy(w => w).ToList();

        string OptsHtml<T>(List<T> vals, string current, Func<T, string> label) => string.Join("", vals.Select(v =>
            $"<option value='{v}'{(v!.ToString() == current ? " selected" : "")}>{label(v)}</option>"));

        var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        var filterBar = $@"
<div class='actions' style='margin-bottom:16px;gap:8px'>
  <label style='display:flex;align-items:center;gap:4px;font-size:.85rem;color:#666'>年
    <select id='dashYear' onchange='applyDashFilter()'><option value=''>全部</option>{OptsHtml(yearOpts, filter.Year, y => y + " 年")}</select>
  </label>
  <label style='display:flex;align-items:center;gap:4px;font-size:.85rem;color:#666'>月
    <select id='dashMonth' onchange='applyDashFilter()'><option value=''>全部</option>{OptsHtml(monthOpts, filter.Month, m => m + " 月")}</select>
  </label>
  <label style='display:flex;align-items:center;gap:4px;font-size:.85rem;color:#666'>週
    <select id='dashWeek' onchange='applyDashFilter()'><option value=''>全部</option>{OptsHtml(weekOpts, filter.Week, w => "第 " + w + " 週")}</select>
  </label>
  <button class='btn btn-sm {(filter.Date == todayStr ? "btn-danger" : "btn-outline")}' onclick='goDashToday()'>📅 只看今天</button>
  <a href='/work/dashboard' class='btn btn-sm btn-outline'>清除篩選</a>
</div>
<script>
function applyDashFilter() {{
  const y = document.getElementById('dashYear').value;
  const m = document.getElementById('dashMonth').value;
  const w = document.getElementById('dashWeek').value;
  const params = [];
  if (y) params.push('year=' + y);
  if (m) params.push('month=' + m);
  if (w) params.push('week=' + w);
  location.href = '/work/dashboard' + (params.length ? '?' + params.join('&') : '');
}}
function goDashToday() {{
  location.href = '/work/dashboard?date=' + new Date().toLocaleDateString('sv-SE');
}}
</script>";

        var totalMin = logs.Sum(l => l.Minutes);
        var epicCount = logs.Select(l => l.EpicName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count();
        var moduleCount = logs.Select(l => l.Module).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count();
        var issuerCount = logs.Select(l => l.RequestIssuer).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count();
        var activeMonths = logs.Select(l => l.PlanStartDate.Length >= 7 ? l.PlanStartDate[..7] : l.PlanStartDate).Distinct().Count();
        var avgMinPerMonth = activeMonths == 0 ? 0 : totalMin / (double)activeMonths;

        string BarRows(IEnumerable<(string Label, int Minutes, int Count)> items, Func<string, string> linkFor)
        {
            var list = items.OrderByDescending(i => i.Minutes).ToList();
            var max = list.Count == 0 ? 1 : list.Max(i => i.Minutes);
            return string.Join("", list.Select(i => $@"
<div class='bar-row'>
  <div class='bar-label'>{System.Net.WebUtility.HtmlEncode(i.Label)}</div>
  <div class='bar-track'><div class='bar-fill' style='width:{(max == 0 ? 0 : i.Minutes * 100.0 / max):F1}%'></div></div>
  <div class='bar-value'>{i.Minutes:N0} 分（<a href='{linkFor(i.Label)}' style='color:#0055cc;text-decoration:none'>{i.Count} 筆</a>，{(totalMin == 0 ? 0 : i.Minutes * 100.0 / totalMin):F0}%）</div>
</div>"));
        }

        var byMonth = logs
            .GroupBy(l => l.PlanStartDate.Length >= 7 ? l.PlanStartDate[..7] : l.PlanStartDate)
            .Select(g => (Label: g.Key, Minutes: g.Sum(l => l.Minutes), Count: g.Count()))
            .OrderByDescending(i => i.Label)
            .Take(12)
            .ToList();

        var bySummary = logs
            .GroupBy(l => string.IsNullOrWhiteSpace(l.Summary) ? "(未分類)" : l.Summary)
            .Select(g => (Label: g.Key, Minutes: g.Sum(l => l.Minutes), Count: g.Count()));

        var byModule = logs
            .GroupBy(l => string.IsNullOrWhiteSpace(l.Module) ? "(未分類)" : l.Module)
            .Select(g => (Label: g.Key, Minutes: g.Sum(l => l.Minutes), Count: g.Count()));

        var byIssuer = logs
            .GroupBy(l => string.IsNullOrWhiteSpace(l.RequestIssuer) ? "(未填寫)" : l.RequestIssuer)
            .Select(g => (Label: g.Key, Minutes: g.Sum(l => l.Minutes), Count: g.Count()));

        // 主要貢獻項目：依 Epic 彙總耗時，呈現投入最多心力的工作項目
        var topEpics = logs
            .Where(l => !string.IsNullOrWhiteSpace(l.EpicName))
            .GroupBy(l => l.EpicName)
            .Select(g => new
            {
                EpicName = g.Key,
                Minutes = g.Sum(l => l.Minutes),
                Count = g.Count(),
                Module = g.Select(l => l.Module).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "",
                Summary = g.Select(l => l.Summary).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "",
                Benefit = g.Select(l => l.BenefitDescription).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? ""
            })
            .OrderByDescending(e => e.Minutes)
            .Take(10)
            .ToList();

        var topEpicRows = string.Join("", topEpics.Select((e, i) => $@"
<tr>
  <td style='text-align:center;color:#999'>{i + 1}</td>
  <td><button type='button' class='epic-pie-link' data-epic='{System.Net.WebUtility.HtmlEncode(e.EpicName)}'
      onclick='showEpicPie(this.dataset.epic)' title='點擊查看各 Label 耗時圓餅圖'>{System.Net.WebUtility.HtmlEncode(e.EpicName)} 🥧</button></td>
  <td><span class='tag'>{System.Net.WebUtility.HtmlEncode(e.Module)}</span></td>
  <td>{System.Net.WebUtility.HtmlEncode(e.Summary)}</td>
  <td style='text-align:right;font-weight:600'>{e.Minutes:N0} 分<div class='cell-sub'>約 {e.Minutes / 60.0:F1} 小時</div></td>
  <td style='text-align:right'><a href='{DimLink("epicName", e.EpicName)}' style='color:#0055cc;text-decoration:none'>{e.Count} 筆</a></td>
</tr>"));

        var topEpicMin = topEpics.Sum(e => e.Minutes);

        // 各 Epic 依 Label 的耗時分布（供點擊 Epic Name 顯示圓餅圖）
        // 一筆紀錄若填了多個 Label，將其耗時平均分攤，使圓餅總和等於該 Epic 的總耗時
        var epicLabelData = topEpics.ToDictionary(
            e => e.EpicName,
            e => logs.Where(l => l.EpicName == e.EpicName)
                .SelectMany(l =>
                {
                    var parts = SplitLabels(l.Labels);
                    return parts.Select(p => (Label: p, Minutes: l.Minutes / (double)parts.Count));
                })
                .GroupBy(x => x.Label)
                .Select(g => new { label = g.Key, minutes = Math.Round(g.Sum(x => x.Minutes), 1) })
                .OrderByDescending(x => x.minutes)
                .ToList());

        // 效益亮點：挑選有填寫效益說明、且投入時間較多的項目，作為績效面談佐證
        var highlights = logs
            .Where(l => !string.IsNullOrWhiteSpace(l.BenefitDescription))
            .GroupBy(l => l.EpicName)
            .Select(g => new
            {
                EpicName = g.Key,
                Minutes = g.Sum(l => l.Minutes),
                Benefit = g.Select(l => l.BenefitDescription).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? ""
            })
            .OrderByDescending(h => h.Minutes)
            .Take(8)
            .ToList();

        var highlightCards = highlights.Count == 0
            ? "<p style='color:#999'>尚無填寫效益說明的紀錄，建議於新增/編輯紀錄時補上「Benefit Description」，更利於展現工作成效。</p>"
            : string.Join("", highlights.Select(h => $@"
<div class='highlight-card'>
  <div class='highlight-title'>📌 {System.Net.WebUtility.HtmlEncode(h.EpicName)}</div>
  <div class='highlight-benefit'>{System.Net.WebUtility.HtmlEncode(h.Benefit)}</div>
  <div class='highlight-min'>投入 {h.Minutes:N0} 分（約 {h.Minutes / 60.0:F1} 小時）</div>
</div>"));

        var detailQuery = BuildDateQuery(filter);
        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>📊 工作紀錄 Dashboard</h1>
  <a href='/work/log{detailQuery}' class='btn btn-outline btn-sm'>查看明細</a>
</div>
{filterBar}
<div class='cards'>
  <div class='card'><div class='lbl'>紀錄筆數</div><div class='val'><a href='/work/log{detailQuery}' style='color:#0055cc;text-decoration:none'>{logs.Count} 筆 →</a></div></div>
  <div class='card'><div class='lbl'>總投入工時</div><div class='val pos'>{totalMin / 60.0:F1} 小時</div></div>
  <div class='card'><div class='lbl'>完成項目數</div><div class='val'>{epicCount} 項</div></div>
  <div class='card'><div class='lbl'>涵蓋模組數</div><div class='val'>{moduleCount} 個</div></div>
  <div class='card'><div class='lbl'>服務需求方</div><div class='val'>{issuerCount} 位</div></div>
  <div class='card'><div class='lbl'>平均每月產出</div><div class='val' style='font-size:1rem'>{avgMinPerMonth / 60.0:F1} 小時/月</div></div>
</div>
<style>
.bar-row{{display:flex;align-items:center;gap:10px;margin:8px 0;font-size:.85rem}}
.bar-label{{width:160px;flex:none;color:#555;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}}
.bar-track{{flex:1;background:#eef1f6;border-radius:4px;height:14px;overflow:hidden}}
.bar-fill{{background:#3b7ddd;height:100%;border-radius:4px}}
.bar-value{{width:170px;flex:none;text-align:right;color:#666}}
.highlight-card{{border:1px solid #e6ecf5;border-left:4px solid #3b7ddd;border-radius:6px;padding:10px 14px;margin:10px 0;background:#fafcff}}
.highlight-title{{font-weight:600;margin-bottom:4px}}
.highlight-benefit{{color:#444;font-size:.88rem;margin-bottom:4px;white-space:pre-wrap}}
.highlight-min{{color:#888;font-size:.78rem}}
.cell-sub{{color:#888;font-size:.75rem;font-weight:400;margin-top:2px}}
.epic-pie-link{{background:none;border:none;padding:0;font:inherit;color:#0055cc;cursor:pointer;text-align:left}}
.epic-pie-link:hover{{text-decoration:underline}}
.pie-mask{{position:fixed;inset:0;background:rgba(0,0,0,.45);z-index:2000;display:flex;align-items:center;justify-content:center;padding:16px}}
.pie-modal{{background:#fff;border-radius:10px;padding:20px;width:520px;max-width:100%;max-height:90vh;overflow:auto;box-shadow:0 10px 40px rgba(0,0,0,.25)}}
.pie-head{{display:flex;align-items:flex-start;gap:10px;margin-bottom:4px}}
.pie-title{{font-weight:600;flex:1;overflow-wrap:anywhere}}
.pie-close{{background:none;border:none;font-size:1.3rem;color:#888;cursor:pointer;line-height:1;padding:0 4px}}
.pie-close:hover{{color:#333}}
.pie-sub{{color:#888;font-size:.8rem;margin-bottom:12px}}
.pie-body{{display:flex;gap:18px;align-items:center;flex-wrap:wrap}}
.pie-legend{{flex:1;min-width:210px;font-size:.85rem}}
.pie-legend-row{{display:flex;align-items:center;gap:8px;padding:4px 2px;border-bottom:1px solid #f2f2f2}}
.pie-legend-row:last-child{{border-bottom:none}}
.pie-chip{{width:12px;height:12px;border-radius:3px;flex:none}}
.pie-legend-name{{flex:1;min-width:0;overflow-wrap:anywhere;color:#333}}
.pie-legend-val{{flex:none;color:#555;text-align:right;white-space:nowrap}}
.pie-slice{{transition:opacity .12s}}
.pie-slice:hover{{opacity:.75}}
.contrib-pie-wrap{{display:flex;gap:20px;align-items:center;flex-wrap:wrap;margin:4px 0 18px}}
.contrib-pie-wrap .pie-legend{{min-width:240px}}
</style>
<div class='section'>
  <h2 style='margin-top:0'>🌟 效益亮點（用於績效面談佐證）</h2>
  {highlightCards}
</div>
<div class='section'>
  <h2 style='margin-top:0'>🏆 主要貢獻項目 Top 10（依投入工時排序）</h2>
  <p style='color:#888;font-size:.8rem;margin:-8px 0 10px'>點擊 Epic Name 可查看該項目各 Label 的耗時圓餅圖</p>
  <div class='contrib-pie-wrap'>
    <svg id='contribPieSvg' viewBox='0 0 220 220' width='220' height='220' role='img' aria-label='主要貢獻項目耗時佔比圓餅圖'></svg>
    <div class='pie-legend' id='contribPieLegend'></div>
  </div>
  <div class='table-wrap'>
  <table>
  <thead><tr><th>#</th><th>Epic Name</th><th>Module</th><th>類型</th><th style='text-align:right'>耗時</th><th style='text-align:right'>紀錄數</th></tr></thead>
  <tbody>{topEpicRows}</tbody>
  <tfoot><tr>
    <td colspan='4' style='font-weight:600'>Top {topEpics.Count} 合計</td>
    <td style='text-align:right;font-weight:600'>{topEpicMin:N0} 分<div class='cell-sub'>約 {topEpicMin / 60.0:F1} 小時</div></td>
    <td style='text-align:right'>{topEpics.Sum(e => e.Count)} 筆</td>
  </tr></tfoot>
  </table>
  </div>
</div>
<div class='section'>
  <h2 style='margin-top:0'>近 12 個月貢獻趨勢</h2>
  {BarRows(byMonth, MonthLink)}
</div>
<div class='section'>
  <h2 style='margin-top:0'>貢獻類型分布（展現工作廣度）</h2>
  {BarRows(bySummary, l => DimLink("summary", l == "(未分類)" ? BlankMarker : l))}
</div>
<div class='section'>
  <h2 style='margin-top:0'>模組涵蓋分布（展現多元支援能力）</h2>
  {BarRows(byModule, l => DimLink("module", l == "(未分類)" ? BlankMarker : l))}
</div>
<div class='section'>
  <h2 style='margin-top:0'>服務需求方統計</h2>
  {BarRows(byIssuer, l => DimLink("issuer", l == "(未填寫)" ? BlankMarker : l))}
</div>
<script>
const epicLabelData = {JsonSerializer.Serialize(epicLabelData)};
// 主要貢獻項目（Top）各 Epic 的耗時，供繪製整體佔比圓餅圖
const contribData = {JsonSerializer.Serialize(topEpics.Select(e => new { label = e.EpicName, minutes = e.Minutes }))};
// 固定順序的分類色（前 6 slot），第 7 項以後併為「其他」以維持可讀性
const PIE_COLORS = ['#2a78d6', '#1baf7a', '#eda100', '#008300', '#4a3aa7', '#e34948'];
const PIE_OTHER = '#9aa0a6';
const MAX_SLICES = 6;

function fmtMin(m) {{
  const mm = Math.round(m * 10) / 10;
  return mm.toLocaleString('zh-TW') + ' 分（約 ' + (m / 60).toFixed(1) + ' 小時）';
}}

function pieEsc(s) {{
  const d = document.createElement('div');
  d.textContent = s;
  return d.innerHTML;
}}

function buildSlices(items) {{
  if (items.length <= MAX_SLICES) return items.map((it, i) => ({{ ...it, color: PIE_COLORS[i] }}));
  const head = items.slice(0, MAX_SLICES - 1).map((it, i) => ({{ ...it, color: PIE_COLORS[i] }}));
  const rest = items.slice(MAX_SLICES - 1);
  head.push({{ label: '其他（' + rest.length + ' 項）', minutes: rest.reduce((s, x) => s + x.minutes, 0), color: PIE_OTHER }});
  return head;
}}

function arcPath(cx, cy, r, ir, a0, a1) {{
  const p = (ang, rad) => [cx + rad * Math.cos(ang), cy + rad * Math.sin(ang)];
  const [x0, y0] = p(a0, r), [x1, y1] = p(a1, r);
  const [ix1, iy1] = p(a1, ir), [ix0, iy0] = p(a0, ir);
  const big = a1 - a0 > Math.PI ? 1 : 0;
  return `M ${{x0}} ${{y0}} A ${{r}} ${{r}} 0 ${{big}} 1 ${{x1}} ${{y1}} L ${{ix1}} ${{iy1}} A ${{ir}} ${{ir}} 0 ${{big}} 0 ${{ix0}} ${{iy0}} Z`;
}}

function showEpicPie(epic) {{
  const items = epicLabelData[epic] || [];
  const total = items.reduce((s, x) => s + x.minutes, 0);
  const slices = buildSlices(items);

  const cx = 110, cy = 110, r = 100, ir = 56;
  let svg = '';
  if (slices.length === 1) {{
    // 單一 Label：整圈同色，環形無法用 arc 畫滿圈
    svg = `<circle cx='${{cx}}' cy='${{cy}}' r='${{(r + ir) / 2}}' fill='none' stroke='${{slices[0].color}}' stroke-width='${{r - ir}}'></circle>`;
  }} else {{
    let a = -Math.PI / 2;
    for (const s of slices) {{
      const sweep = total > 0 ? (s.minutes / total) * Math.PI * 2 : 0;
      svg += `<path class='pie-slice' d='${{arcPath(cx, cy, r, ir, a, a + sweep)}}' fill='${{s.color}}' stroke='#fff' stroke-width='2'>`
           + `<title>${{pieEsc(s.label)}}：${{fmtMin(s.minutes)}}</title></path>`;
      a += sweep;
    }}
  }}
  // 圓心放總時數，讓「這個 Epic 花了多久」不必靠讀圖推算
  svg += `<text x='${{cx}}' y='${{cy - 4}}' text-anchor='middle' style='font-size:18px;font-weight:700;fill:#222'>${{(total / 60).toFixed(1)}}</text>`
       + `<text x='${{cx}}' y='${{cy + 14}}' text-anchor='middle' style='font-size:11px;fill:#888'>小時</text>`;

  const legend = slices.map(s => `<div class='pie-legend-row'>
      <span class='pie-chip' style='background:${{s.color}}'></span>
      <span class='pie-legend-name'>${{pieEsc(s.label)}}</span>
      <span class='pie-legend-val'>${{fmtMin(s.minutes)}}<br>${{total > 0 ? (s.minutes / total * 100).toFixed(0) : 0}}%</span>
    </div>`).join('');

  const mask = document.createElement('div');
  mask.className = 'pie-mask';
  mask.innerHTML = `<div class='pie-modal'>
    <div class='pie-head'>
      <div class='pie-title'>🥧 ${{pieEsc(epic)}}</div>
      <button type='button' class='pie-close' aria-label='關閉'>×</button>
    </div>
    <div class='pie-sub'>各 Label 耗時分布　合計 ${{fmtMin(total)}}</div>
    <div class='pie-body'>
      <svg viewBox='0 0 220 220' width='220' height='220' role='img' aria-label='各 Label 耗時圓餅圖'>${{svg}}</svg>
      <div class='pie-legend'>${{legend}}</div>
    </div>
  </div>`;

  const close = () => {{ mask.remove(); document.removeEventListener('keydown', onKey); }};
  const onKey = e => {{ if (e.key === 'Escape') close(); }};
  mask.addEventListener('click', e => {{ if (e.target === mask) close(); }});
  mask.querySelector('.pie-close').addEventListener('click', close);
  document.addEventListener('keydown', onKey);
  document.body.appendChild(mask);
}}

// 整體「主要貢獻項目」耗時佔比圓餅圖（直接內嵌於區塊，非彈窗）
function renderContribPie() {{
  const svgEl = document.getElementById('contribPieSvg');
  const legendEl = document.getElementById('contribPieLegend');
  if (!svgEl || !legendEl) return;
  const items = (contribData || []).filter(x => x.minutes > 0);
  const total = items.reduce((s, x) => s + x.minutes, 0);
  if (total <= 0) {{
    svgEl.style.display = 'none';
    legendEl.innerHTML = '<div style=\'color:#999;font-size:.85rem\'>尚無可統計的耗時</div>';
    return;
  }}
  const slices = buildSlices(items);
  const cx = 110, cy = 110, r = 100, ir = 56;
  let svg = '';
  if (slices.length === 1) {{
    svg = `<circle cx='${{cx}}' cy='${{cy}}' r='${{(r + ir) / 2}}' fill='none' stroke='${{slices[0].color}}' stroke-width='${{r - ir}}'></circle>`;
  }} else {{
    let a = -Math.PI / 2;
    for (const s of slices) {{
      const sweep = (s.minutes / total) * Math.PI * 2;
      svg += `<path class='pie-slice' d='${{arcPath(cx, cy, r, ir, a, a + sweep)}}' fill='${{s.color}}' stroke='#fff' stroke-width='2'>`
           + `<title>${{pieEsc(s.label)}}：${{fmtMin(s.minutes)}}（${{(s.minutes / total * 100).toFixed(0)}}%）</title></path>`;
      a += sweep;
    }}
  }}
  svg += `<text x='${{cx}}' y='${{cy - 4}}' text-anchor='middle' style='font-size:18px;font-weight:700;fill:#222'>${{(total / 60).toFixed(1)}}</text>`
       + `<text x='${{cx}}' y='${{cy + 14}}' text-anchor='middle' style='font-size:11px;fill:#888'>小時</text>`;
  svgEl.innerHTML = svg;
  legendEl.innerHTML = slices.map(s => `<div class='pie-legend-row'>
      <span class='pie-chip' style='background:${{s.color}}'></span>
      <span class='pie-legend-name'>${{pieEsc(s.label)}}</span>
      <span class='pie-legend-val'>${{fmtMin(s.minutes)}}<br>${{(s.minutes / total * 100).toFixed(0)}}%</span>
    </div>`).join('');
}}
renderContribPie();
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("Dashboard", "work", "dashboard", body));
    }

    static async Task AddLogPage(HttpContext ctx)
    {
        // ?copy={id}：以既有紀錄為範本複製新增
        var copyId = ctx.Request.Query["copy"].ToString();
        WorkLog? src = string.IsNullOrEmpty(copyId) ? null : LoadLogs().FirstOrDefault(l => l.Id == copyId);
        var copyMode = src != null;

        var body = FormHtml(src, copyMode);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page(copyMode ? "複製新增工作紀錄" : "新增工作紀錄", "work", "logadd", body));
    }

    static async Task EditLogPage(string id, HttpContext ctx)
    {
        var log = LoadLogs().FirstOrDefault(l => l.Id == id);
        if (log == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("紀錄不存在"); return; }

        var body = FormHtml(log);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("編輯工作紀錄", "work", "log", body));
    }

    // ── Shared form (新增 / 編輯共用) ────────────────────────────────────────

    // 預設 Description 內容（新增時自動帶入）
    const string DefDescription = "[ITCYTER]";

    static string FormHtml(WorkLog? log, bool copyMode = false)
    {
        // edit：真正的編輯既有紀錄；prefill：帶入既有紀錄資料（編輯或複製新增皆是）
        bool edit = log != null && !copyMode;
        bool prefill = log != null;
        string V(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        string Opts(string[] options, string selected) => string.Join("", options.Select(o =>
            $"<option value='{V(o)}'{(o == selected ? " selected" : "")}>{V(o)}</option>"));

        var project = prefill ? log!.Project : DefProject;
        var issueType = prefill ? log!.IssueType : DefIssueType;
        var summarySel = prefill ? log!.Summary : SummaryOptions[0];
        var epicName = prefill ? log!.EpicName : "";
        var epicCatSel = prefill ? log!.EpicCategory : DefEpicCategory;
        var itPlatform = prefill ? log!.ItPlatform : DefItPlatform;
        var moduleSel = prefill ? log!.Module : DefModule;
        var requirementId = prefill ? log!.RequirementId : "";
        var jiraIssue = prefill ? log!.JiraIssue : "";
        var benefit = prefill ? log!.BenefitDescription : "";
        var labels = prefill ? log!.Labels : "";
        var description = prefill ? log!.Description : DefDescription;
        var linkedIssue = prefill ? log!.LinkedIssue : DefLinkedIssue;
        var issue = prefill ? log!.Issue : DefIssue;
        var itpm = prefill ? log!.Itpm : DefItpm;
        var requestIssuer = prefill ? log!.RequestIssuer : "";
        // 複製新增時：Plan Start Date 預設帶入今天、耗時歸零（視為新的工作），僅編輯沿用原值；一般新增則留白
        var planStart = edit ? log!.PlanStartDate : (copyMode ? DateTime.Today.ToString("yyyy-MM-dd") : "");
        var minutes = edit ? log!.Minutes : 0;

        var heading = edit ? "✏️ 編輯工作紀錄" : (copyMode ? "📋 複製新增工作紀錄" : "📝 新增工作紀錄");
        var submitLabel = edit ? "儲存變更" : "確認新增";
        var apiUrl = edit ? $"/api/worklog/{log!.Id}" : "/api/worklog";
        var apiMethod = edit ? "PUT" : "POST";

        return $@"
<div>
  <a href='/work/log' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回工作紀錄</a>
  <h1 style='margin:4px 0 20px'>{heading}</h1>
</div>
<div class='form-card' style='max-width:760px'>
<div id='msg'></div>

<div class='row3'>
  <div class='field'><label>Project</label><input id='project' value='{V(project)}'></div>
  <div class='field'><label>Issue Type</label><input id='issueType' value='{V(issueType)}'></div>
  <div class='field'><label>Summary</label><select id='summary'>{Opts(SummaryOptions, summarySel)}</select></div>
</div>

<div class='row2'>
  <div class='field'><label>Epic Name</label>
    <div class='combo' id='epicNameCombo'>
      <input id='epicName' autocomplete='off' value='{V(epicName)}' placeholder='本次工作主題，可選也可自行輸入'>
      <div class='combo-panel' id='epicNamePanel'></div>
    </div>
  </div>
  <div class='field'><label>Epic Category</label><select id='epicCategory'>{Opts(EpicCategoryOptions, epicCatSel)}</select></div>
</div>

<div class='row3'>
  <div class='field'><label>IT Platform</label><input id='itPlatform' value='{V(itPlatform)}'></div>
  <div class='field'><label>Module</label><select id='module'>{Opts(ModuleOptions, moduleSel)}</select></div>
  <div class='field'><label>Requirement ID</label><input id='requirementId' value='{V(requirementId)}'></div>
</div>

<div class='field'><label>Jira Issue 單號</label><input id='jiraIssue' value='{V(jiraIssue)}' placeholder='例如 CIPR-1234'></div>
<div class='field'><label>Benefit Description</label><input id='benefitDescription' value='{V(benefit)}' placeholder='效益說明'></div>
<div class='field'><label>Labels（SAP TCODE）</label>
  <div class='combo' id='labelsCombo'>
    <input id='labels' autocomplete='off' value='{V(labels)}' placeholder='如：ME21N、VA01，可選也可自行輸入'>
    <div class='combo-panel' id='labelsPanel'></div>
  </div>
</div>
<div class='field'><label>Description</label><textarea id='description' rows='3' placeholder='工作內容描述'>{V(description)}</textarea></div>

<div class='row3'>
  <div class='field'><label>Linked Issue</label><input id='linkedIssue' value='{V(linkedIssue)}'></div>
  <div class='field'><label>Issue</label><input id='issue' value='{V(issue)}'></div>
  <div class='field'><label>ITPM</label><input id='itpm' value='{V(itpm)}'></div>
</div>

<div class='row2'>
  <div class='field'><label>Request Issuer</label>
    <div class='combo' id='requestIssuerCombo'>
      <input id='requestIssuer' autocomplete='off' value='{V(requestIssuer)}' placeholder='需求提出者'>
      <div class='combo-panel' id='requestIssuerPanel'></div>
    </div>
  </div>
  <div class='field'><label>Plan Start Date</label><input type='date' id='planStartDate' value='{V(planStart)}'></div>
</div>

<div class='field'>
  <label>耗時（分鐘）</label>
  <div style='display:flex;gap:12px;align-items:center;flex-wrap:wrap'>
    <input type='number' id='minutes' value='{minutes}' min='0' style='width:120px'>
    <button type='button' class='btn btn-sm' id='timerBtn' onclick='toggleTimer()'>▶ 開始計時</button>
    <button type='button' class='btn btn-sm' id='continueBtn' style='display:none' onclick='continueTimer()'>▶ 繼續計時</button>
    <span id='timerDisplay' style='font-family:monospace;font-size:1.1rem;color:#0055cc;font-weight:600'>00:00:00</span>
    <button type='button' class='btn btn-sm btn-outline' onclick='resetTimer()'>歸零</button>
  </div>
  <div style='color:#999;font-size:.78rem;margin-top:4px'>按「開始計時」開始工作，「結束計時」後自動累加分鐘數；可多段累計，也可手動微調。</div>
</div>

<div class='field'>
  <label>建立資料夾</label>
  <div style='display:flex;gap:8px;align-items:center;flex-wrap:wrap'>
    <input id='folderBasePath' placeholder='指定路徑，例如 D:\WorkDocs 或 \\server\share' style='flex:1;min-width:220px'>
    <button type='button' class='btn btn-sm btn-outline' id='createFolderBtn' onclick='createFolder()'>📁 建立資料夾</button>
  </div>
  <div style='color:#999;font-size:.78rem;margin-top:4px'>會在上方路徑下建立「Requirement ID Request Issuer Description」子資料夾（瀏覽器會記住此路徑）。</div>
  <div id='folderMsg' style='font-size:.85rem;margin-top:6px'></div>
</div>

<div class='actions' style='margin-top:8px'>
  <button class='btn' onclick='submit()'>{submitLabel}</button>
  <a href='/work/log' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
{(edit ? "// 編輯時沿用該紀錄原本的 Plan Start Date" : @"
// 新增（含複製）紀錄時，Plan Start Date 預設帶入今天日期；使用者仍可自行清除或修改
if (!document.getElementById('planStartDate').value)
  document.getElementById('planStartDate').value = new Date().toLocaleDateString('sv-SE');
")}

// 自由輸入下拉選單（Epic Name／Labels／Request Issuer）：可自行輸入新值，也可從選單挑選或刪除既有選項
function initCombo(inputId, panelId, deleteField) {{
  const inp = document.getElementById(inputId), panel = document.getElementById(panelId);
  let options = [];

  function render(filterText) {{
    const q = (filterText || '').trim().toLowerCase();
    const filtered = options.filter(o => !q || o.toLowerCase().indexOf(q) >= 0);
    panel.innerHTML = '';
    if (filtered.length === 0) {{ panel.classList.remove('open'); return; }}
    for (const o of filtered) {{
      const row = document.createElement('div');
      row.className = 'combo-option';
      const txt = document.createElement('span');
      txt.className = 'combo-option-text';
      txt.textContent = o;
      txt.addEventListener('mousedown', e => {{
        e.preventDefault();
        inp.value = o;
        inp.dispatchEvent(new Event('change', {{ bubbles: true }}));
        closePanel();
      }});
      row.appendChild(txt);
      const del = document.createElement('span');
      del.className = 'combo-option-del';
      del.textContent = '✕';
      del.title = '刪除此選項';
      del.addEventListener('mousedown', async e => {{
        e.preventDefault();
        e.stopPropagation();
        if (!confirm('確定刪除下拉選項「' + o + '」？（不會刪除已使用此值的既有紀錄）')) return;
        try {{
          const r = await fetch('/api/worklog/options?field=' + encodeURIComponent(deleteField) + '&value=' + encodeURIComponent(o), {{ method: 'DELETE' }});
          if (r.ok) {{ options = options.filter(x => x !== o); render(inp.value); }}
          else alert('刪除失敗');
        }} catch (err) {{ alert('刪除失敗：' + err.message); }}
      }});
      row.appendChild(del);
      panel.appendChild(row);
    }}
    panel.classList.add('open');
  }}
  function closePanel() {{ panel.classList.remove('open'); }}

  inp.addEventListener('focus', () => render(inp.value));
  inp.addEventListener('input', () => render(inp.value));
  inp.addEventListener('blur', () => setTimeout(closePanel, 150));

  return {{
    setOptions(list) {{ options = list.slice(); }},
    addOption(v) {{ if (v && options.indexOf(v) === -1) options.push(v); }}
  }};
}}
const comboEpicName = initCombo('epicName', 'epicNamePanel', 'epicNames');
const comboLabels = initCombo('labels', 'labelsPanel', 'labels');
const comboRequestIssuer = initCombo('requestIssuer', 'requestIssuerPanel', 'issuers');
fetch('/api/worklog/options').then(r => r.json()).then(opts => {{
  comboEpicName.setOptions(opts.epicNames);
  comboLabels.setOptions(opts.labels);
  comboRequestIssuer.setOptions(opts.issuers);
}});

// Epic Name 特定值時，自動帶入相關欄位
document.getElementById('epicName').addEventListener('change', function() {{
  const v = this.value.trim();
  comboEpicName.addOption(v);
  if (v === '{V(Ds4xEpicName)}') {{
    document.getElementById('linkedIssue').value = '{V(Ds4xLinkedIssue)}';
    document.getElementById('issue').value = '{V(Ds4xIssue)}';
    document.getElementById('requestIssuer').value = '{V(Ds4xRequestIssuer)}';
  }} else if (v === '{V(CustomerTagEpicName)}') {{
    document.getElementById('module').value = '{V(CustomerTagModule)}';
  }}
}});
document.getElementById('labels').addEventListener('change', function() {{ comboLabels.addOption(this.value.trim()); }});
document.getElementById('requestIssuer').addEventListener('change', function() {{ comboRequestIssuer.addOption(this.value.trim()); }});

// Summary 連動 Linked Issue / Issue / Issue Type / Epic Category
document.getElementById('summary').addEventListener('change', function() {{
  if (this.value === 'Change Requests') {{
    document.getElementById('linkedIssue').value = '{V(DefLinkedIssue)}';
    document.getElementById('issue').value = '{V(DefIssue)}';
    document.getElementById('issueType').value = '{V(DefIssueType)}';
    document.getElementById('epicCategory').value = 'Request';
  }} else {{
    document.getElementById('linkedIssue').value = '';
    document.getElementById('issue').value = '';
    document.getElementById('issueType').value = 'Other';
    if (this.value === 'Issue Solving') document.getElementById('epicCategory').value = 'Issue';
    else if (this.value === 'Operation Tickets') {{
      const epicInput = document.getElementById('epicName');
      epicInput.value = 'AUTH';
      epicInput.dispatchEvent(new Event('change', {{ bubbles: true }}));
    }}
  }}
}});

// ── 計時器 ──
let timerStart = null, accumulatedMs = (parseInt(document.getElementById('minutes').value) || 0) * 60000, tick = null;
if (accumulatedMs > 0) {{
  document.getElementById('timerDisplay').textContent = fmt(accumulatedMs);
  document.getElementById('continueBtn').style.display = 'inline-block';
}}
function fmt(ms) {{
  const s = Math.floor(ms/1000);
  const h = String(Math.floor(s/3600)).padStart(2,'0');
  const m = String(Math.floor(s%3600/60)).padStart(2,'0');
  const ss = String(s%60).padStart(2,'0');
  return h+':'+m+':'+ss;
}}
function refresh() {{
  const cur = accumulatedMs + (timerStart ? Date.now()-timerStart : 0);
  document.getElementById('timerDisplay').textContent = fmt(cur);
  document.getElementById('minutes').value = Math.round(cur/60000);
}}
function toggleTimer() {{
  const btn = document.getElementById('timerBtn');
  if (timerStart === null) {{
    timerStart = Date.now();
    btn.textContent = '⏹ 結束計時';
    btn.classList.add('btn-danger');
    document.getElementById('continueBtn').style.display = 'none';
    tick = setInterval(refresh, 1000);
    refresh();
  }} else {{
    accumulatedMs += Date.now() - timerStart;
    timerStart = null;
    clearInterval(tick);
    btn.textContent = '▶ 開始計時';
    btn.classList.remove('btn-danger');
    document.getElementById('continueBtn').style.display = accumulatedMs > 0 ? 'inline-block' : 'none';
    refresh();
  }}
}}
function continueTimer() {{
  if (timerStart !== null) return;
  timerStart = Date.now();
  const btn = document.getElementById('timerBtn');
  btn.textContent = '⏹ 結束計時';
  btn.classList.add('btn-danger');
  document.getElementById('continueBtn').style.display = 'none';
  tick = setInterval(refresh, 1000);
  refresh();
}}
function resetTimer() {{
  if (timerStart !== null) {{ clearInterval(tick); timerStart = null;
    document.getElementById('timerBtn').textContent = '▶ 開始計時';
    document.getElementById('timerBtn').classList.remove('btn-danger'); }}
  accumulatedMs = 0;
  document.getElementById('continueBtn').style.display = 'none';
  document.getElementById('timerDisplay').textContent = '00:00:00';
  document.getElementById('minutes').value = 0;
}}

{(edit ? "" : "toggleTimer(); // 新增紀錄時自動開始計時")}

async function submit() {{
  if (timerStart !== null) toggleTimer(); // 送出前自動停止計時
  const req = {{
    project: document.getElementById('project').value.trim(),
    issueType: document.getElementById('issueType').value.trim(),
    summary: document.getElementById('summary').value,
    epicName: document.getElementById('epicName').value.trim(),
    epicCategory: document.getElementById('epicCategory').value,
    itPlatform: document.getElementById('itPlatform').value.trim(),
    module: document.getElementById('module').value,
    requirementId: document.getElementById('requirementId').value.trim(),
    jiraIssue: document.getElementById('jiraIssue').value.trim(),
    benefitDescription: document.getElementById('benefitDescription').value.trim(),
    labels: document.getElementById('labels').value.trim(),
    description: document.getElementById('description').value.trim(),
    linkedIssue: document.getElementById('linkedIssue').value.trim(),
    issue: document.getElementById('issue').value.trim(),
    itpm: document.getElementById('itpm').value.trim(),
    requestIssuer: document.getElementById('requestIssuer').value.trim(),
    planStartDate: document.getElementById('planStartDate').value,
    minutes: parseInt(document.getElementById('minutes').value) || 0
  }};
  if (!req.epicName) {{ showMsg('請填寫 Epic Name', 'err'); return; }}
  const r = await fetch('{apiUrl}', {{method:'{apiMethod}', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) {{ showMsg('✓ 已儲存！', 'ok'); setTimeout(() => location.href='/work/log', 1000); }}
  else {{ const t = await r.text(); showMsg(t || '儲存失敗', 'err'); }}
}}
function showMsg(m,t){{document.getElementById('msg').innerHTML=`<div class='alert ${{t}}'>${{m}}</div>`;}}

// ── 建立資料夾（路徑瀏覽器記住，資料夾名稱＝Requirement ID／Request Issuer／Description）──
document.getElementById('folderBasePath').value = localStorage.getItem('workLogFolderBasePath') || '';
function showFolderMsg(m, ok) {{
  const el = document.getElementById('folderMsg');
  el.textContent = m;
  el.style.color = ok ? '#1e7e34' : '#c0392b';
}}
async function createFolder() {{
  const basePath = document.getElementById('folderBasePath').value.trim();
  if (!basePath) {{ showFolderMsg('請先輸入資料夾建立路徑', false); return; }}
  localStorage.setItem('workLogFolderBasePath', basePath);

  const folderName = [
    document.getElementById('requirementId').value.trim(),
    document.getElementById('requestIssuer').value.trim(),
    document.getElementById('description').value.trim()
  ].filter(Boolean).join(' ');
  if (!folderName) {{ showFolderMsg('請至少填寫 Requirement ID、Request Issuer 或 Description 其中一項', false); return; }}

  const btn = document.getElementById('createFolderBtn');
  const oldText = btn.textContent;
  btn.disabled = true; btn.textContent = '建立中...';
  showFolderMsg('', true);
  try {{
    const r = await fetch('/api/worklog/create-folder', {{
      method: 'POST', headers: {{'Content-Type':'application/json'}},
      body: JSON.stringify({{ basePath, folderName }})
    }});
    if (r.ok) {{
      const data = await r.json();
      showFolderMsg('✓ 已建立資料夾：' + data.path, true);
    }} else {{
      const t = await r.text();
      showFolderMsg(t || ('建立資料夾失敗（HTTP ' + r.status + '）'), false);
    }}
  }} catch (err) {{
    showFolderMsg('建立資料夾失敗：無法連線到伺服器（' + err.message + '）', false);
  }} finally {{
    btn.disabled = false; btn.textContent = oldText;
  }}
}}
</script>";
    }

    // ── API handlers ──────────────────────────────────────────────────────

    static IResult AddLog(WorkLogRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.EpicName))
            return Results.BadRequest("請填寫 Epic Name");

        var log = new WorkLog(
            Guid.NewGuid().ToString("N")[..8],
            req.Project ?? "", req.IssueType ?? "", req.Summary ?? "", req.EpicName,
            req.EpicCategory ?? "", req.ItPlatform ?? "", req.Module ?? "", req.RequirementId ?? "",
            req.JiraIssue ?? "",
            req.BenefitDescription ?? "", req.Labels ?? "", req.Description ?? "", req.LinkedIssue ?? "",
            req.Issue ?? "", req.Itpm ?? "", req.RequestIssuer ?? "",
            req.PlanStartDate ?? "", req.Minutes,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var logs = LoadLogs();
        logs.Add(log);
        SaveLogs(logs);
        TrackFieldOptions(req);
        return Results.Ok();
    }

    static IResult UpdateLog(string id, WorkLogRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.EpicName))
            return Results.BadRequest("請填寫 Epic Name");

        var logs = LoadLogs();
        var idx = logs.FindIndex(l => l.Id == id);
        if (idx < 0) return Results.NotFound("紀錄不存在");

        logs[idx] = logs[idx] with
        {
            Project = req.Project ?? "", IssueType = req.IssueType ?? "", Summary = req.Summary ?? "",
            EpicName = req.EpicName, EpicCategory = req.EpicCategory ?? "", ItPlatform = req.ItPlatform ?? "",
            Module = req.Module ?? "", RequirementId = req.RequirementId ?? "", JiraIssue = req.JiraIssue ?? "",
            BenefitDescription = req.BenefitDescription ?? "",
            Labels = req.Labels ?? "", Description = req.Description ?? "", LinkedIssue = req.LinkedIssue ?? "",
            Issue = req.Issue ?? "", Itpm = req.Itpm ?? "", RequestIssuer = req.RequestIssuer ?? "",
            PlanStartDate = req.PlanStartDate ?? "", Minutes = req.Minutes
        };
        SaveLogs(logs);
        TrackFieldOptions(req);
        return Results.Ok();
    }

    static IResult GetFieldOptions()
    {
        var opts = LoadFieldOptions();
        return Results.Ok(new
        {
            issuers = opts["issuers"],
            epicNames = opts["epicNames"],
            labels = opts["labels"]
        });
    }

    static IResult DeleteFieldOption(HttpContext ctx)
    {
        string field = ctx.Request.Query["field"].ToString();
        string value = ctx.Request.Query["value"].ToString();
        if (!ComboFieldKeys.Contains(field)) return Results.BadRequest("不支援的欄位");
        if (string.IsNullOrEmpty(value)) return Results.BadRequest("缺少選項值");

        var opts = LoadFieldOptions();
        opts[field].RemoveAll(v => v == value);
        SaveFieldOptions(opts);
        return Results.Ok();
    }

    static IResult CreateFolder(CreateFolderRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.BasePath))
            return Results.BadRequest("請輸入資料夾建立路徑");
        if (string.IsNullOrWhiteSpace(req.FolderName))
            return Results.BadRequest("資料夾名稱不可為空（請至少填寫 Requirement ID、Request Issuer 或 Description 其中一項）");

        // 過濾檔名不合法字元，並確認結果仍落在指定的基準路徑內，避免用 ".." 等方式逸出
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = new string(req.FolderName.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
            return Results.BadRequest("資料夾名稱不合法");

        try
        {
            var basePath = Path.GetFullPath(req.BasePath);
            var target = Path.GetFullPath(Path.Combine(basePath, safeName));
            if (!target.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("資料夾名稱不合法");

            Directory.CreateDirectory(target);
            return Results.Ok(new { path = target });
        }
        catch (Exception ex)
        {
            return Results.BadRequest("建立資料夾失敗：" + ex.Message);
        }
    }

    static IResult DeleteLog(string id)
    {
        var logs = LoadLogs();
        var target = logs.FirstOrDefault(l => l.Id == id);
        if (target == null) return Results.NotFound();
        logs.Remove(target);
        SaveLogs(logs);
        return Results.Ok();
    }

    // ── Data helpers ──────────────────────────────────────────────────────

    static List<WorkLog> LoadLogs()
    {
        if (!File.Exists(LogsFile)) return [];
        return JsonSerializer.Deserialize<List<WorkLog>>(File.ReadAllText(LogsFile), JsonOpts) ?? [];
    }

    static void SaveLogs(List<WorkLog> logs)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(LogsFile, JsonSerializer.Serialize(logs, JsonOpts));
    }

    // ── 自由輸入下拉選單選項（Epic Name／Labels／Request Issuer）──────────────
    // 選項清單獨立存放，使用者可個別刪除選項而不影響既有紀錄的資料。

    static Dictionary<string, List<string>> LoadFieldOptions()
    {
        Dictionary<string, List<string>>? opts = null;
        if (File.Exists(FieldOptionsFile))
            opts = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(FieldOptionsFile), JsonOpts);
        opts ??= [];

        // 首次啟用／欄位缺漏時，用既有紀錄的歷史值補齊初始選項
        var logs = LoadLogs();
        List<string> Distinct(Func<WorkLog, string> sel) => logs
            .Select(sel).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

        var changed = false;
        void Ensure(string key, Func<WorkLog, string> sel)
        {
            if (opts.ContainsKey(key)) return;
            opts[key] = Distinct(sel);
            changed = true;
        }
        Ensure("epicNames", l => l.EpicName);
        Ensure("labels", l => l.Labels);
        Ensure("issuers", l => l.RequestIssuer);

        foreach (var key in ComboFieldKeys)
            opts[key] = opts[key].OrderBy(s => s).ToList();

        if (changed) SaveFieldOptions(opts);
        return opts;
    }

    static void SaveFieldOptions(Dictionary<string, List<string>> opts)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FieldOptionsFile, JsonSerializer.Serialize(opts, JsonOpts));
    }

    static void TrackFieldOptions(WorkLogRequest req)
    {
        var opts = LoadFieldOptions();
        var changed = false;
        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (opts[key].Contains(value)) return;
            opts[key].Add(value);
            changed = true;
        }
        Add("epicNames", req.EpicName);
        Add("labels", req.Labels);
        Add("issuers", req.RequestIssuer);
        if (changed) SaveFieldOptions(opts);
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

public record WorkLog(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("issueType")] string IssueType,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("epicName")] string EpicName,
    [property: JsonPropertyName("epicCategory")] string EpicCategory,
    [property: JsonPropertyName("itPlatform")] string ItPlatform,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("requirementId")] string RequirementId,
    [property: JsonPropertyName("jiraIssue")] string JiraIssue,
    [property: JsonPropertyName("benefitDescription")] string BenefitDescription,
    [property: JsonPropertyName("labels")] string Labels,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("linkedIssue")] string LinkedIssue,
    [property: JsonPropertyName("issue")] string Issue,
    [property: JsonPropertyName("itpm")] string Itpm,
    [property: JsonPropertyName("requestIssuer")] string RequestIssuer,
    [property: JsonPropertyName("planStartDate")] string PlanStartDate,
    [property: JsonPropertyName("minutes")] int Minutes,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

public record WorkLogRequest(
    string? Project, string? IssueType, string? Summary, string EpicName,
    string? EpicCategory, string? ItPlatform, string? Module, string? RequirementId,
    string? JiraIssue,
    string? BenefitDescription, string? Labels, string? Description, string? LinkedIssue,
    string? Issue, string? Itpm, string? RequestIssuer, string? PlanStartDate, int Minutes);

public record CreateFolderRequest(string BasePath, string FolderName);
