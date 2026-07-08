using System.Text.Json;
using System.Text.Json.Serialization;

public static class WorkLogModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string LogsFile = Path.Combine(DataDir, "work_logs.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // ── 預設值與選項 ────────────────────────────────────────────────────────
    const string DefProject = "CyntecITR(ITCYTER)";
    const string DefIssueType = "Epic";
    const string DefEpicCategory = "Request";
    const string DefItPlatform = "ERP-Enterprise Resource Planning";
    const string DefModule = "FI/TR/RE";
    const string DefLinkedIssue = "is child of";
    const string DefIssue = "CIPR-3816";
    const string DefItpm = "ERIC.CY.CHOU";

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
        app.MapGet("/api/worklog/issuers", GetIssuers);
    }

    // ── Pages ─────────────────────────────────────────────────────────────

    static async Task LogsPage(HttpContext ctx)
    {
        var logs = LoadLogs().OrderByDescending(l => l.PlanStartDate).ThenByDescending(l => l.CreatedAt).ToList();
        var totalMin = logs.Sum(l => l.Minutes);

        var rows = logs.Count == 0
            ? "<tr><td colspan='9' style='text-align:center;padding:30px;color:#999'>尚無工作紀錄</td></tr>"
            : string.Join("", logs.Select(l => $@"<tr data-id='{l.Id}'>
  <td>{l.PlanStartDate}</td>
  <td>{System.Net.WebUtility.HtmlEncode(l.Summary)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(l.EpicName)}</td>
  <td><span class='tag'>{System.Net.WebUtility.HtmlEncode(l.Module)}</span></td>
  <td>{System.Net.WebUtility.HtmlEncode(l.RequirementId)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(l.JiraIssue)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(l.Labels)}</td>
  <td style='text-align:right;font-weight:600'>{l.Minutes:N0}</td>
  <td><div class='actions'>
    <a href='/work/log/{l.Id}/edit' class='btn btn-sm btn-outline'>編輯</a>
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
  <button class='btn btn-sm btn-outline' onclick=""['filterYear','filterMonth','filterWeek'].forEach(id=>document.getElementById(id).value='');clearTodayFilter();logTable.run()"">清除日期篩選</button>
</div>
<div class='table-wrap'>
<table id='log-table'>
<thead><tr>
  <th>日期</th><th>Summary</th><th>Epic Name</th><th>Module</th><th>Requirement ID</th><th>Jira Issue</th><th>Labels</th><th style='text-align:right'>耗時(分)</th><th></th>
</tr></thead>
<tbody>{rows}</tbody>
<tfoot><tr>
  <td colspan='7'>篩選合計</td>
  <td style='text-align:right;font-weight:600' id='tf-min'>{totalMin:N0}</td>
  <td></td>
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
let filterExactDate = '';
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
function dateExtraFilter(row) {{
  const log = fullLogsData[row.dataset.id];
  if (!log || !log.planStartDate) return true;
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

const logTable = initTable('log-table', {{
  cols: 9,
  noFilter: [2, 7, 8],
  sumCols: [{{col: 7, id: 'tf-min'}}],
  extraFilter: dateExtraFilter,
  onFilter: function(vis) {{
    let sum = 0;
    for (const r of vis) sum += parseFloat(r.cells[7].textContent.replace(/,/g,'')) || 0;
    const countEl = document.getElementById('sum-count');
    if (countEl) countEl.textContent = vis.length.toLocaleString('zh-TW') + ' 筆';
    const el = document.getElementById('sum-min');
    if (el) el.textContent = Math.round(sum).toLocaleString('zh-TW') + ' 分';
    const hr = document.getElementById('sum-hr');
    if (hr) hr.textContent = (sum / 60).toFixed(1) + ' 小時';
  }}
}});
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("工作紀錄", "work", "log", body));
    }

    static async Task DashboardPage(HttpContext ctx)
    {
        var logs = LoadLogs();

        if (logs.Count == 0)
        {
            var emptyBody = "<h1>📊 工作紀錄 Dashboard</h1><div class='empty-state'><div class='icon'>📭</div><p>尚無工作紀錄</p></div>";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(SharedLayout.Page("Dashboard", "work", "dashboard", emptyBody));
            return;
        }

        var totalMin = logs.Sum(l => l.Minutes);
        var epicCount = logs.Select(l => l.EpicName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count();
        var moduleCount = logs.Select(l => l.Module).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count();
        var issuerCount = logs.Select(l => l.RequestIssuer).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Count();
        var activeMonths = logs.Select(l => l.PlanStartDate.Length >= 7 ? l.PlanStartDate[..7] : l.PlanStartDate).Distinct().Count();
        var avgMinPerMonth = activeMonths == 0 ? 0 : totalMin / (double)activeMonths;

        string BarRows(IEnumerable<(string Label, int Minutes, int Count)> items)
        {
            var list = items.OrderByDescending(i => i.Minutes).ToList();
            var max = list.Count == 0 ? 1 : list.Max(i => i.Minutes);
            return string.Join("", list.Select(i => $@"
<div class='bar-row'>
  <div class='bar-label'>{System.Net.WebUtility.HtmlEncode(i.Label)}</div>
  <div class='bar-track'><div class='bar-fill' style='width:{(max == 0 ? 0 : i.Minutes * 100.0 / max):F1}%'></div></div>
  <div class='bar-value'>{i.Minutes:N0} 分（{i.Count} 筆，{(totalMin == 0 ? 0 : i.Minutes * 100.0 / totalMin):F0}%）</div>
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
  <td>{System.Net.WebUtility.HtmlEncode(e.EpicName)}</td>
  <td><span class='tag'>{System.Net.WebUtility.HtmlEncode(e.Module)}</span></td>
  <td>{System.Net.WebUtility.HtmlEncode(e.Summary)}</td>
  <td style='text-align:right;font-weight:600'>{e.Minutes:N0} 分</td>
  <td style='text-align:right'>{e.Count} 筆</td>
</tr>"));

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

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>📊 工作紀錄 Dashboard</h1>
  <a href='/work/log' class='btn btn-outline btn-sm'>查看明細</a>
</div>
<div class='cards'>
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
</style>
<div class='section'>
  <h2 style='margin-top:0'>🌟 效益亮點（用於績效面談佐證）</h2>
  {highlightCards}
</div>
<div class='section'>
  <h2 style='margin-top:0'>🏆 主要貢獻項目 Top 10（依投入工時排序）</h2>
  <div class='table-wrap'>
  <table>
  <thead><tr><th>#</th><th>Epic Name</th><th>Module</th><th>類型</th><th style='text-align:right'>耗時</th><th style='text-align:right'>紀錄數</th></tr></thead>
  <tbody>{topEpicRows}</tbody>
  </table>
  </div>
</div>
<div class='section'>
  <h2 style='margin-top:0'>近 12 個月貢獻趨勢</h2>
  {BarRows(byMonth)}
</div>
<div class='section'>
  <h2 style='margin-top:0'>貢獻類型分布（展現工作廣度）</h2>
  {BarRows(bySummary)}
</div>
<div class='section'>
  <h2 style='margin-top:0'>模組涵蓋分布（展現多元支援能力）</h2>
  {BarRows(byModule)}
</div>
<div class='section'>
  <h2 style='margin-top:0'>服務需求方統計</h2>
  {BarRows(byIssuer)}
</div>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("Dashboard", "work", "dashboard", body));
    }

    static async Task AddLogPage(HttpContext ctx)
    {
        var body = FormHtml(null);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("新增工作紀錄", "work", "logadd", body));
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

    static string FormHtml(WorkLog? log)
    {
        bool edit = log != null;
        string V(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        string Opts(string[] options, string selected) => string.Join("", options.Select(o =>
            $"<option value='{V(o)}'{(o == selected ? " selected" : "")}>{V(o)}</option>"));

        var project = edit ? log!.Project : DefProject;
        var issueType = edit ? log!.IssueType : DefIssueType;
        var summarySel = edit ? log!.Summary : SummaryOptions[0];
        var epicName = edit ? log!.EpicName : "";
        var epicCatSel = edit ? log!.EpicCategory : DefEpicCategory;
        var itPlatform = edit ? log!.ItPlatform : DefItPlatform;
        var moduleSel = edit ? log!.Module : DefModule;
        var requirementId = edit ? log!.RequirementId : "";
        var jiraIssue = edit ? log!.JiraIssue : "";
        var benefit = edit ? log!.BenefitDescription : "";
        var labels = edit ? log!.Labels : "";
        var description = edit ? log!.Description : "";
        var linkedIssue = edit ? log!.LinkedIssue : DefLinkedIssue;
        var issue = edit ? log!.Issue : DefIssue;
        var itpm = edit ? log!.Itpm : DefItpm;
        var requestIssuer = edit ? log!.RequestIssuer : "";
        var planStart = edit ? log!.PlanStartDate : "";
        var minutes = edit ? log!.Minutes : 0;

        var heading = edit ? "✏️ 編輯工作紀錄" : "📝 新增工作紀錄";
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
  <div class='field'><label>Epic Name</label><input id='epicName' value='{V(epicName)}' placeholder='本次工作主題'></div>
  <div class='field'><label>Epic Category</label><select id='epicCategory'>{Opts(EpicCategoryOptions, epicCatSel)}</select></div>
</div>

<div class='row3'>
  <div class='field'><label>IT Platform</label><input id='itPlatform' value='{V(itPlatform)}'></div>
  <div class='field'><label>Module</label><select id='module'>{Opts(ModuleOptions, moduleSel)}</select></div>
  <div class='field'><label>Requirement ID</label><input id='requirementId' value='{V(requirementId)}'></div>
</div>

<div class='field'><label>Jira Issue 單號</label><input id='jiraIssue' value='{V(jiraIssue)}' placeholder='例如 CIPR-1234'></div>
<div class='field'><label>Benefit Description</label><input id='benefitDescription' value='{V(benefit)}' placeholder='效益說明'></div>
<div class='field'><label>Labels（SAP TCODE）</label><input id='labels' value='{V(labels)}' placeholder='如：ME21N、VA01'></div>
<div class='field'><label>Description</label><textarea id='description' rows='3' placeholder='工作內容描述'>{V(description)}</textarea></div>

<div class='row3'>
  <div class='field'><label>Linked Issue</label><input id='linkedIssue' value='{V(linkedIssue)}'></div>
  <div class='field'><label>Issue</label><input id='issue' value='{V(issue)}'></div>
  <div class='field'><label>ITPM</label><input id='itpm' value='{V(itpm)}'></div>
</div>

<div class='row2'>
  <div class='field'><label>Request Issuer</label><input id='requestIssuer' list='issuerList' value='{V(requestIssuer)}' placeholder='需求提出者'><datalist id='issuerList'></datalist></div>
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

<div class='actions' style='margin-top:8px'>
  <button class='btn' onclick='submit()'>{submitLabel}</button>
  <a href='/work/log' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
// 預設日期為今天（僅新增時）
if (!document.getElementById('planStartDate').value)
  document.getElementById('planStartDate').value = new Date().toISOString().slice(0,10);

// Request Issuer 自動完成（帶出曾輸入過的紀錄，仍可自行輸入）
fetch('/api/worklog/issuers').then(r => r.json()).then(list => {{
  const dl = document.getElementById('issuerList');
  for (const s of list) {{
    const opt = document.createElement('option');
    opt.value = s;
    dl.appendChild(opt);
  }}
}});

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
            req.PlanStartDate ?? DateTime.Today.ToString("yyyy-MM-dd"), req.Minutes,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var logs = LoadLogs();
        logs.Add(log);
        SaveLogs(logs);
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
        return Results.Ok();
    }

    static IResult GetIssuers()
    {
        var issuers = LoadLogs()
            .Select(l => l.RequestIssuer)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        return Results.Ok(issuers);
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
