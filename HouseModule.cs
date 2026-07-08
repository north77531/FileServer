using System.Text.Json;
using System.Text.Json.Serialization;

public static class HouseModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string PropertiesFile = Path.Combine(DataDir, "properties.json");
    static readonly string PropertyExpensesFile = Path.Combine(DataDir, "property_expenses.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/house", ctx => { ctx.Response.Redirect("/house/properties"); return Task.CompletedTask; });
        app.MapGet("/house/properties", PropertiesPage);
        app.MapGet("/house/properties/add", AddPropertyPage);
        app.MapGet("/house/properties/{id}/edit", EditPropertyPage);
        app.MapGet("/house/properties/{id}/expenses", PropertyExpensesPage);
        app.MapGet("/house/properties/{id}/expenses/add", AddPropertyExpensePage);
        app.MapGet("/house/properties/{id}/sell", SellPropertyPage);
        app.MapGet("/house/realestate", RealEstatePage);

        app.MapPost("/api/house/properties", AddProperty);
        app.MapPut("/api/house/properties/{id}", UpdateProperty);
        app.MapDelete("/api/house/properties/{id}", DeleteProperty);
        app.MapPost("/api/house/properties/{id}/expenses", AddPropertyExpense);
        app.MapDelete("/api/house/expenses/{id}", DeletePropertyExpense);
        app.MapPost("/api/house/properties/{id}/sell", RecordSale);
        app.MapDelete("/api/house/properties/{id}/sell", ClearSale);
    }

    static readonly Dictionary<string, string> CountyCodes = new()
    {
        ["臺北市"] = "A", ["台北市"] = "A",
        ["新北市"] = "F",
        ["桃園市"] = "H",
        ["臺中市"] = "B", ["台中市"] = "B",
        ["臺南市"] = "D", ["台南市"] = "D",
        ["高雄市"] = "E",
        ["基隆市"] = "C",
        ["新竹市"] = "O",
        ["新竹縣"] = "J",
        ["苗栗縣"] = "K",
        ["南投縣"] = "M",
        ["彰化縣"] = "N",
        ["雲林縣"] = "P",
        ["嘉義市"] = "I",
        ["嘉義縣"] = "Q",
        ["屏東縣"] = "T",
        ["宜蘭縣"] = "G",
        ["臺東縣"] = "V", ["台東縣"] = "V",
        ["花蓮縣"] = "U",
    };

    static (string? County, string Keyword) ParseAddress(string address)
    {
        address = address.Trim();
        if (address.Length < 3) return (null, "");
        var cityName = address[..3];
        if (!CountyCodes.TryGetValue(cityName, out var county)) return (null, "");

        var remainder = address[3..];
        var m = System.Text.RegularExpressions.Regex.Match(remainder, "^.{0,6}?(區|鎮|鄉)");
        var keyword = m.Success ? m.Value : "";
        return (county, keyword);
    }

    // ── Pages ─────────────────────────────────────────────────────────────

    static async Task PropertiesPage(HttpContext ctx)
    {
        var props = LoadProperties()
            .OrderBy(p => p.SalePrice.HasValue && p.SalePrice > 0 ? 1 : 0)
            .ThenByDescending(p => p.PurchaseDate)
            .ToList();
        var expenses = LoadExpenses();

        var cards = props.Count == 0
            ? "<div class='empty-state'><div class='icon'>🏡</div><p>尚無房產資料</p><a href='/house/properties/add' class='btn'>新增房產</a></div>"
            : string.Join("", props.Select(p =>
            {
                var propExp = expenses.Where(e => e.PropertyId == p.Id).Sum(e => e.Amount);
                var totalCost = p.PurchasePrice + propExp;
                var isSold = p.SalePrice.HasValue && p.SalePrice > 0;
                var profitHtml = "";
                if (isSold)
                {
                    var profit = p.SalePrice!.Value - totalCost;
                    var profitSign = profit >= 0 ? "+" : "";
                    var profitColor = profit >= 0 ? "#27ae60" : "#c0392b";
                    profitHtml = $@"
    <div class='card'><div class='lbl'>賣出價格</div><div class='val' style='font-size:1rem'>{p.SalePrice.Value:N0} 元</div></div>
    <div class='card'><div class='lbl'>賣出日期</div><div class='val' style='font-size:.95rem'>{p.SaleDate}</div></div>
    <div class='card'><div class='lbl'>損益</div><div class='val' style='color:{profitColor};font-weight:700'>{profitSign}{profit:N0} 元</div></div>";
                }
                var sellBtn = isSold
                    ? $"<a href='/house/properties/{p.Id}/sell' class='btn btn-sm btn-outline' style='color:#27ae60;border-color:#27ae60'>已售出</a>"
                    : $"<a href='/house/properties/{p.Id}/sell' class='btn btn-sm btn-outline'>記錄賣出</a>";
                var landPingHtml = p.LandPing > 0 ? $"<div class='card'><div class='lbl'>地坪</div><div class='val'>{p.LandPing:F1} 坪</div></div>" : "";
                var ageHtml = "";
                if (p.BuildYear > 0)
                {
                    var age = DateTime.Today.Year - p.BuildYear;
                    ageHtml = $"<div class='card'><div class='lbl'>屋齡</div><div class='val'>{age} 年<span style='font-size:.78rem;color:#999;margin-left:4px'>({p.BuildYear}年建)</span></div></div>";
                }
                var floorsHtml = p.TotalFloors > 0 ? $"<div class='card'><div class='lbl'>樓層數</div><div class='val'>{p.TotalFloors} 層</div></div>" : "";
                return $@"
<div class='section'>
  <div class='actions' style='margin-bottom:12px'>
    <div style='flex:1'>
      <div style='font-size:1.1rem;font-weight:700'>{System.Net.WebUtility.HtmlEncode(p.Name)}{(isSold ? " <span style='background:#27ae60;color:#fff;font-size:.7rem;padding:2px 7px;border-radius:10px;vertical-align:middle'>已售出</span>" : "")}</div>
      <div style='color:#666;font-size:.88rem'>{System.Net.WebUtility.HtmlEncode(p.Address)}</div>
    </div>
    <a href='/house/properties/{p.Id}/expenses' class='btn btn-sm btn-outline'>花費紀錄</a>
    <a href='/house/properties/{p.Id}/expenses/add' class='btn btn-sm'>新增花費</a>
    <a href='/house/properties/{p.Id}/edit' class='btn btn-sm btn-outline'>編輯</a>
    {sellBtn}
    <button class='btn btn-sm btn-danger' onclick='delProp(""{p.Id}"")'>刪除</button>
  </div>
  <div class='cards' style='margin-bottom:0'>
    <div class='card'><div class='lbl'>類型</div><div class='val' style='font-size:1rem'><span class='tag'>{System.Net.WebUtility.HtmlEncode(p.PropertyType)}</span></div></div>
    <div class='card'><div class='lbl'>建坪</div><div class='val'>{p.AreaPing:F1} 坪</div></div>
    {landPingHtml}
    {ageHtml}
    {floorsHtml}
    <div class='card'><div class='lbl'>購入價格</div><div class='val'>{p.PurchasePrice:N0} 元</div></div>
    <div class='card'><div class='lbl'>購入日期</div><div class='val' style='font-size:1rem'>{p.PurchaseDate}</div></div>
    <div class='card'><div class='lbl'>累計花費</div><div class='val pos'>{propExp:N0} 元</div></div>
    {profitHtml}
  </div>
  {(string.IsNullOrEmpty(p.Notes) ? "" : $"<div style='margin-top:10px;color:#666;font-size:.88rem'>備註：{System.Net.WebUtility.HtmlEncode(p.Notes)}</div>")}
  {(isSold && !string.IsNullOrEmpty(p.SaleNotes) ? $"<div style='margin-top:6px;color:#666;font-size:.88rem'>賣出備註：{System.Net.WebUtility.HtmlEncode(p.SaleNotes)}</div>" : "")}
</div>";
            }));

        var exportRows = string.Join("", props.Select(p =>
        {
            var propExp = expenses.Where(e => e.PropertyId == p.Id).Sum(e => e.Amount);
            var isSold = p.SalePrice.HasValue && p.SalePrice > 0;
            return $@"<tr>
  <td>{System.Net.WebUtility.HtmlEncode(p.Name)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(p.PropertyType)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(p.Address)}</td>
  <td>{p.AreaPing}</td>
  <td>{p.LandPing}</td>
  <td>{p.BuildYear}</td>
  <td>{p.TotalFloors}</td>
  <td>{p.PurchasePrice}</td>
  <td>{p.PurchaseDate}</td>
  <td>{propExp}</td>
  <td>{(isSold ? p.SalePrice!.Value.ToString() : "")}</td>
  <td>{(isSold ? p.SaleDate : "")}</td>
  <td>{System.Net.WebUtility.HtmlEncode(p.Notes)}</td>
</tr>";
        }));

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>🏡 目前房產</h1>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('prop-export-table','目前房產',{{btn:this}})"">⬇ 下載Excel</button>
  <a href='/house/properties/add' class='btn'>＋ 新增房產</a>
</div>
{cards}
<table id='prop-export-table' style='display:none'>
<thead><tr><th>房產名稱</th><th>類型</th><th>地址</th><th>建坪</th><th>地坪</th><th>建築年份</th><th>樓層數</th><th>購入價格</th><th>購入日期</th><th>累計花費</th><th>賣出價格</th><th>賣出日期</th><th>備註</th></tr></thead>
<tbody>{exportRows}</tbody>
</table>
<div id='msg'></div>
<script>
async function delProp(id) {{
  if (!confirm('確定刪除此房產？相關花費紀錄也會一併刪除。')) return;
  const r = await fetch('/api/house/properties/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">刪除失敗</div>';
}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("目前房產", "house", "properties", body));
    }

    static async Task AddPropertyPage(HttpContext ctx)
    {
        var body = @"
<h1>🏡 新增房產</h1>
<div class='form-card'>
<div id='msg'></div>
<div class='row2'>
  <div class='field'>
    <label>房產名稱</label>
    <input id='name' placeholder='如：自住房、投資套房'>
  </div>
  <div class='field'>
    <label>類型</label>
    <select id='propertyType'>
      <option>公寓</option><option>大樓</option><option>透天</option><option>套房</option><option>別墅</option><option>其他</option>
    </select>
  </div>
</div>
<div class='field'>
  <label>地址</label>
  <input id='address' placeholder='如：台北市大安區忠孝東路四段XXX號'>
</div>
<div class='row3'>
  <div class='field'>
    <label>建坪（坪）</label>
    <input type='number' id='areaPing' step='0.01' placeholder='如：30.5'>
  </div>
  <div class='field'>
    <label>地坪（坪）</label>
    <input type='number' id='landPing' step='0.01' placeholder='選填'>
  </div>
  <div class='field'>
    <label>建築年份（西元）</label>
    <input type='number' id='buildYear' placeholder='如：2005（選填）' min='1900' max='2100' oninput='updateAge()'>
  </div>
  <div class='field'>
    <label>樓層數</label>
    <input type='number' id='totalFloors' placeholder='如：12（選填）' min='1'>
  </div>
</div>
<div id='ageHint' style='display:none;margin:-8px 0 12px;color:#888;font-size:.88rem'></div>
<div class='row2'>
  <div class='field'>
    <label>購入價格（元）</label>
    <input type='number' id='purchasePrice' placeholder='如：20000000'>
  </div>
  <div class='field'>
    <label>購入日期</label>
    <input type='date' id='purchaseDate'>
  </div>
</div>
<div class='field'>
  <label>備註</label>
  <textarea id='notes' rows='2' placeholder='選填'></textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()'>確認新增</button>
  <a href='/house/properties' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
document.getElementById('purchaseDate').value = new Date().toISOString().slice(0,10);
function updateAge() {
  const y = parseInt(document.getElementById('buildYear').value);
  const hint = document.getElementById('ageHint');
  if (y > 1900 && y <= new Date().getFullYear()) {
    const age = new Date().getFullYear() - y;
    hint.textContent = `屋齡約 ${age} 年`;
    hint.style.display = '';
  } else {
    hint.style.display = 'none';
  }
}
async function submit() {
  const req = {
    name: document.getElementById('name').value.trim(),
    propertyType: document.getElementById('propertyType').value,
    address: document.getElementById('address').value.trim(),
    areaPing: parseFloat(document.getElementById('areaPing').value) || 0,
    landPing: parseFloat(document.getElementById('landPing').value) || 0,
    buildYear: parseInt(document.getElementById('buildYear').value) || 0,
    totalFloors: parseInt(document.getElementById('totalFloors').value) || 0,
    purchasePrice: parseFloat(document.getElementById('purchasePrice').value) || 0,
    purchaseDate: document.getElementById('purchaseDate').value,
    notes: document.getElementById('notes').value.trim()
  };
  if (!req.name || !req.address) { showMsg('請填寫房產名稱與地址', 'err'); return; }
  const r = await fetch('/api/house/properties', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(req)});
  if (r.ok) { showMsg('✓ 已新增！', 'ok'); setTimeout(() => location.href='/house/properties', 1200); }
  else { const t = await r.text(); showMsg(t || '新增失敗', 'err'); }
}
function showMsg(m,t){document.getElementById('msg').innerHTML=`<div class='alert ${t}'>${m}</div>`;}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("新增房產", "house", "properties", body));
    }

    static async Task EditPropertyPage(string id, HttpContext ctx)
    {
        var props = LoadProperties();
        var prop = props.FirstOrDefault(p => p.Id == id);
        if (prop == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("房產不存在"); return; }

        var types = new[] { "公寓", "大樓", "透天", "套房", "別墅", "其他" };
        var typeOptions = string.Join("", types.Select(t =>
            $"<option{(t == prop.PropertyType ? " selected" : "")}>{t}</option>"));

        var body = $@"
<h1>🏡 編輯房產</h1>
<div class='form-card'>
<div id='msg'></div>
<div class='row2'>
  <div class='field'>
    <label>房產名稱</label>
    <input id='name' value='{System.Net.WebUtility.HtmlEncode(prop.Name)}'>
  </div>
  <div class='field'>
    <label>類型</label>
    <select id='propertyType'>{typeOptions}</select>
  </div>
</div>
<div class='field'>
  <label>地址</label>
  <input id='address' value='{System.Net.WebUtility.HtmlEncode(prop.Address)}'>
</div>
<div class='row3'>
  <div class='field'>
    <label>建坪（坪）</label>
    <input type='number' id='areaPing' step='0.01' value='{prop.AreaPing}'>
  </div>
  <div class='field'>
    <label>地坪（坪）</label>
    <input type='number' id='landPing' step='0.01' value='{prop.LandPing}'>
  </div>
  <div class='field'>
    <label>建築年份（西元）</label>
    <input type='number' id='buildYear' value='{prop.BuildYear}' min='1900' max='2100' oninput='updateAge()'>
  </div>
  <div class='field'>
    <label>樓層數</label>
    <input type='number' id='totalFloors' value='{prop.TotalFloors}' min='1'>
  </div>
</div>
<div id='ageHint' style='display:none;margin:-8px 0 12px;color:#888;font-size:.88rem'></div>
<div class='row2'>
  <div class='field'>
    <label>購入價格（元）</label>
    <input type='number' id='purchasePrice' value='{prop.PurchasePrice}'>
  </div>
  <div class='field'>
    <label>購入日期</label>
    <input type='date' id='purchaseDate' value='{prop.PurchaseDate}'>
  </div>
</div>
<div class='field'>
  <label>備註</label>
  <textarea id='notes' rows='2'>{System.Net.WebUtility.HtmlEncode(prop.Notes)}</textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()'>儲存變更</button>
  <a href='/house/properties' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
function updateAge() {{
  const y = parseInt(document.getElementById('buildYear').value);
  const hint = document.getElementById('ageHint');
  if (y > 1900 && y <= new Date().getFullYear()) {{
    const age = new Date().getFullYear() - y;
    hint.textContent = `屋齡約 ${{age}} 年`;
    hint.style.display = '';
  }} else {{
    hint.style.display = 'none';
  }}
}}
updateAge();
async function submit() {{
  const req = {{
    name: document.getElementById('name').value.trim(),
    propertyType: document.getElementById('propertyType').value,
    address: document.getElementById('address').value.trim(),
    areaPing: parseFloat(document.getElementById('areaPing').value) || 0,
    landPing: parseFloat(document.getElementById('landPing').value) || 0,
    buildYear: parseInt(document.getElementById('buildYear').value) || 0,
    totalFloors: parseInt(document.getElementById('totalFloors').value) || 0,
    purchasePrice: parseFloat(document.getElementById('purchasePrice').value) || 0,
    purchaseDate: document.getElementById('purchaseDate').value,
    notes: document.getElementById('notes').value.trim()
  }};
  if (!req.name || !req.address) {{ showMsg('請填寫房產名稱與地址', 'err'); return; }}
  const r = await fetch('/api/house/properties/{id}', {{method:'PUT', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) {{ showMsg('✓ 已儲存！', 'ok'); setTimeout(() => location.href='/house/properties', 1200); }}
  else {{ const t = await r.text(); showMsg(t || '儲存失敗', 'err'); }}
}}
function showMsg(m,t){{document.getElementById('msg').innerHTML=`<div class='alert ${{t}}'>${{m}}</div>`;}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("編輯房產", "house", "properties", body));
    }

    static async Task SellPropertyPage(string id, HttpContext ctx)
    {
        var props = LoadProperties();
        var prop = props.FirstOrDefault(p => p.Id == id);
        if (prop == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("房產不存在"); return; }

        var expenses = LoadExpenses().Where(e => e.PropertyId == id).Sum(e => e.Amount);
        var totalCost = prop.PurchasePrice + expenses;
        var isSold = prop.SalePrice.HasValue && prop.SalePrice > 0;

        var existingSalePrice = prop.SalePrice.HasValue ? prop.SalePrice.Value.ToString() : "";
        var existingSaleDate = prop.SaleDate ?? "";
        var existingSaleNotes = System.Net.WebUtility.HtmlEncode(prop.SaleNotes ?? "");

        var clearBtn = isSold ? $@"
<div style='margin-top:16px;padding-top:16px;border-top:1px solid #eee'>
  <button class='btn btn-outline btn-danger' onclick='clearSale()' style='font-size:.88rem'>清除賣出紀錄</button>
</div>" : "";

        var body = $@"
<div>
  <a href='/house/properties' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回房產列表</a>
  <h1 style='margin:4px 0 20px'>💰 賣出資訊 — {System.Net.WebUtility.HtmlEncode(prop.Name)}</h1>
</div>
<div class='cards' style='margin-bottom:20px'>
  <div class='card'><div class='lbl'>購入價格</div><div class='val'>{prop.PurchasePrice:N0} 元</div></div>
  <div class='card'><div class='lbl'>累計花費</div><div class='val pos'>{expenses:N0} 元</div></div>
  <div class='card'><div class='lbl'>持有總成本</div><div class='val' style='font-weight:700'>{totalCost:N0} 元</div></div>
</div>
<div class='form-card'>
<div id='msg'></div>
<div class='row2'>
  <div class='field'>
    <label>賣出價格（元）</label>
    <input type='number' id='salePrice' placeholder='如：25000000' value='{existingSalePrice}'>
  </div>
  <div class='field'>
    <label>賣出日期</label>
    <input type='date' id='saleDate' value='{existingSaleDate}'>
  </div>
</div>
<div class='field'>
  <label>損益預覽</label>
  <div id='profitPreview' style='padding:10px;background:#f8f8f8;border-radius:8px;font-size:1rem;color:#666'>輸入賣出價格後自動計算</div>
</div>
<div class='field'>
  <label>備註</label>
  <textarea id='saleNotes' rows='2' placeholder='選填，如：自售、透過仲介'>{existingSaleNotes}</textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()'>儲存賣出資訊</button>
  <a href='/house/properties' class='btn btn-outline'>取消</a>
</div>
{clearBtn}
</div>
<script>
const totalCost = {totalCost};
if (!document.getElementById('saleDate').value)
  document.getElementById('saleDate').value = new Date().toISOString().slice(0,10);
document.getElementById('salePrice').addEventListener('input', updatePreview);
updatePreview();
function updatePreview() {{
  const sp = parseFloat(document.getElementById('salePrice').value) || 0;
  if (sp <= 0) {{ document.getElementById('profitPreview').textContent = '輸入賣出價格後自動計算'; document.getElementById('profitPreview').style.color='#666'; return; }}
  const profit = sp - totalCost;
  const sign = profit >= 0 ? '+' : '';
  const color = profit >= 0 ? '#27ae60' : '#c0392b';
  document.getElementById('profitPreview').innerHTML = `<span style='color:${{color}};font-weight:700'>${{sign}}${{profit.toLocaleString()}} 元</span>（賣出 ${{sp.toLocaleString()}} − 成本 ${{totalCost.toLocaleString()}}）`;
}}
async function submit() {{
  const req = {{
    salePrice: parseFloat(document.getElementById('salePrice').value) || 0,
    saleDate: document.getElementById('saleDate').value,
    saleNotes: document.getElementById('saleNotes').value.trim()
  }};
  if (req.salePrice <= 0) {{ showMsg('請輸入賣出價格', 'err'); return; }}
  const r = await fetch('/api/house/properties/{id}/sell', {{method:'POST', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) {{ showMsg('✓ 已儲存！', 'ok'); setTimeout(() => location.href='/house/properties', 1200); }}
  else {{ const t = await r.text(); showMsg(t || '儲存失敗', 'err'); }}
}}
async function clearSale() {{
  if (!confirm('確定清除賣出紀錄？')) return;
  const r = await fetch('/api/house/properties/{id}/sell', {{method:'DELETE'}});
  if (r.ok) location.reload();
  else showMsg('清除失敗', 'err');
}}
function showMsg(m,t){{document.getElementById('msg').innerHTML=`<div class='alert ${{t}}'>${{m}}</div>`;}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("賣出資訊", "house", "properties", body));
    }

    static async Task PropertyExpensesPage(string id, HttpContext ctx)
    {
        var props = LoadProperties();
        var prop = props.FirstOrDefault(p => p.Id == id);
        if (prop == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("房產不存在"); return; }

        var expenses = LoadExpenses().Where(e => e.PropertyId == id).OrderByDescending(e => e.Date).ToList();
        var total = expenses.Sum(e => e.Amount);

        var categories = expenses.GroupBy(e => e.Category)
            .Select(g => $"<div class='card'><div class='lbl'>{g.Key}</div><div class='val' style='font-size:1rem'>{g.Sum(x => x.Amount):N0} 元</div></div>");

        var rows = expenses.Count == 0
            ? "<tr><td colspan='8' style='text-align:center;padding:30px;color:#999'>尚無花費紀錄</td></tr>"
            : string.Join("", expenses.Select(e => $@"
<tr>
  <td>{e.Date}</td>
  <td><span class='tag'>{System.Net.WebUtility.HtmlEncode(e.Category)}</span></td>
  <td>{System.Net.WebUtility.HtmlEncode(e.ItemName)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(e.Brand)}</td>
  <td style='text-align:right;color:#c00'>{e.Amount:N0}</td>
  <td>{System.Net.WebUtility.HtmlEncode(e.Vendor)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(e.Notes)}</td>
  <td><button class='btn btn-danger btn-sm' onclick='del(""{e.Id}"")'>刪除</button></td>
</tr>"));

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <div>
    <a href='/house/properties' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回房產列表</a>
    <h1 style='margin:4px 0 0'>{System.Net.WebUtility.HtmlEncode(prop.Name)} — 花費紀錄</h1>
  </div>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('house-exp-table','{System.Net.WebUtility.HtmlEncode(prop.Name)}花費紀錄',{{skipCols:[7],btn:this}})"">⬇ 下載Excel</button>
  <a href='/house/properties/{id}/expenses/add' class='btn'>＋ 新增花費</a>
</div>
<div class='cards'>
  <div class='card'><div class='lbl'>累計花費</div><div class='val pos'>{total:N0} 元</div></div>
  <div class='card'><div class='lbl'>紀錄筆數</div><div class='val'>{expenses.Count} 筆</div></div>
  {string.Join("", categories)}
</div>
<div class='table-wrap'>
<table id='house-exp-table'>
<thead><tr><th>日期</th><th>類別</th><th>品項</th><th>品牌</th><th style='text-align:right'>金額</th><th>店家／廠商</th><th>備註</th><th></th></tr></thead>
<tbody>{rows}</tbody>
<tfoot><tr>
  <td colspan='4'>篩選合計</td>
  <td style='text-align:right;font-weight:600' id='hexp-tf-total'>{total:N0}</td>
  <td colspan='3'></td>
</tr></tfoot>
</table>
</div>
<div id='msg' style='margin-top:12px'></div>
<script>
async function del(id) {{
  if (!confirm('確定刪除？')) return;
  const r = await fetch('/api/house/expenses/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">刪除失敗</div>';
}}
initTable('house-exp-table', {{
  cols: 8,
  noFilter: [4, 7],
  sumCols: [{{col: 4, id: 'hexp-tf-total'}}]
}});
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page($"{prop.Name} 花費紀錄", "house", "properties", body));
    }

    static async Task AddPropertyExpensePage(string id, HttpContext ctx)
    {
        var props = LoadProperties();
        var prop = props.FirstOrDefault(p => p.Id == id);
        if (prop == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("房產不存在"); return; }

        var body = $@"
<div>
  <a href='/house/properties/{id}/expenses' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回花費紀錄</a>
  <h1 style='margin:4px 0 20px'>新增花費 — {System.Net.WebUtility.HtmlEncode(prop.Name)}</h1>
</div>
<div class='form-card'>
<div id='msg'></div>
<div class='row2'>
  <div class='field'>
    <label>類別</label>
    <select id='category' onchange='onCategoryChange()'>
      <option>管理費</option><option>修繕費</option><option>地價稅</option><option>房屋稅</option>
      <option>水電費</option><option>裝潢費</option><option>仲介費</option>
      <option>家電</option><option>家具</option><option>其他</option>
    </select>
  </div>
  <div class='field' id='subCategoryField' style='display:none'>
    <label>子分類</label>
    <select id='subCategory'></select>
  </div>
</div>
<div class='row2'>
  <div class='field'>
    <label>品項</label>
    <input id='itemName' placeholder='選填，如：變頻冷氣、馬桶'>
  </div>
  <div class='field'>
    <label>品牌</label>
    <input id='brand' placeholder='選填，如：大金、TOTO'>
  </div>
</div>
<div class='row2'>
  <div class='field'>
    <label>日期</label>
    <input type='date' id='date'>
  </div>
  <div class='field'>
    <label>金額（元）</label>
    <input type='number' id='amount' placeholder='如：5000'>
  </div>
</div>
<div class='field'>
  <label>店家／廠商</label>
  <input id='vendor' placeholder='選填，如：XX水電行、OO家電'>
</div>
<div class='field'>
  <label>備註</label>
  <textarea id='notes' rows='2' placeholder='選填'></textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()'>確認新增</button>
  <a href='/house/properties/{id}/expenses' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
const subCategories = {{
  '家電': ['冷氣', '冰箱', '洗衣機', '電視', '熱水器', '抽油煙機', '洗碗機', '烘乾機', '除濕機', '空氣清淨機', '其他家電'],
  '家具': ['沙發', '床組', '衣櫃', '書桌椅', '餐桌椅', '電視櫃', '茶几', '窗簾', '收納櫃', '其他家具']
}};
document.getElementById('date').value = new Date().toISOString().slice(0,10);
function onCategoryChange() {{
  const cat = document.getElementById('category').value;
  const field = document.getElementById('subCategoryField');
  const sel = document.getElementById('subCategory');
  if (subCategories[cat]) {{
    sel.innerHTML = subCategories[cat].map(s => `<option>${{s}}</option>`).join('');
    field.style.display = '';
  }} else {{
    field.style.display = 'none';
  }}
}}
async function submit() {{
  const cat = document.getElementById('category').value;
  const sub = document.getElementById('subCategoryField').style.display !== 'none'
    ? document.getElementById('subCategory').value : '';
  const category = sub ? cat + '－' + sub : cat;
  const req = {{
    category,
    date: document.getElementById('date').value,
    amount: parseFloat(document.getElementById('amount').value) || 0,
    vendor: document.getElementById('vendor').value.trim(),
    notes: document.getElementById('notes').value.trim(),
    itemName: document.getElementById('itemName').value.trim(),
    brand: document.getElementById('brand').value.trim()
  }};
  if (req.amount <= 0) {{ showMsg('請輸入金額', 'err'); return; }}
  const r = await fetch('/api/house/properties/{id}/expenses', {{method:'POST', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) {{ showMsg('✓ 已新增！', 'ok'); setTimeout(() => location.href='/house/properties/{id}/expenses', 1200); }}
  else {{ const t = await r.text(); showMsg(t || '新增失敗', 'err'); }}
}}
function showMsg(m,t){{document.getElementById('msg').innerHTML=`<div class='alert ${{t}}'>${{m}}</div>`;}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("新增房產花費", "house", "properties", body));
    }

    static async Task RealEstatePage(HttpContext ctx, IHttpClientFactory httpFactory)
    {
        var disabledBody = "<h1>🔍 實價登入</h1><div class='empty-state'><div class='icon'>🚧</div><p>此功能目前已停用</p></div>";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("實價登入", "house", "realestate", disabledBody));
        return;
#pragma warning disable CS0162
        var props = LoadProperties().Where(p => !(p.SalePrice.HasValue && p.SalePrice > 0)).ToList();

        if (props.Count == 0)
        {
            var emptyBody = "<h1>🔍 實價登入</h1><div class='empty-state'><div class='icon'>🏡</div><p>目前沒有持有中（未賣出）的房產</p></div>";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(SharedLayout.Page("實價登入", "house", "realestate", emptyBody));
            return;
        }

        var results = await Task.WhenAll(props.Select(async p =>
        {
            var (county, keyword) = ParseAddress(p.Address);
            if (county == null) return (Prop: p, Records: (List<Dictionary<string, string?>>?)null, Note: (string?)null, Error: "無法從地址辨識縣市，請至官方網站查詢");
            try
            {
                var (records, note) = await FetchRealEstateWithFallback(county, keyword, httpFactory);
                return (Prop: p, Records: (List<Dictionary<string, string?>>?)records, Note: note, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Prop: p, Records: (List<Dictionary<string, string?>>?)null, Note: (string?)null, Error: $"查詢失敗：{ex.Message}");
            }
        }));

        var sections = string.Join("", results.Select(r =>
        {
            var p = r.Prop;
            string content;
            if (r.Error != null)
            {
                content = $"<div class='alert err'>{System.Net.WebUtility.HtmlEncode(r.Error)}<br><a href='https://lvr.land.moi.gov.tw/' target='_blank'>前往官方實價登錄查詢網站</a></div>";
            }
            else if (r.Records == null || r.Records.Count == 0)
            {
                content = "<div class='alert' style='background:#fff9e6;border:1px solid #e6d87a'>查無近2年附近成交資料。<a href='https://lvr.land.moi.gov.tw/' target='_blank'>可至官方網站查詢</a></div>";
            }
            else
            {
                var tid = "re-" + p.Id;
                var rows = string.Join("", r.Records.Select(rec => $@"
<tr>
  <td>{rec.GetValueOrDefault("date")}</td>
  <td>{rec.GetValueOrDefault("address")}</td>
  <td>{rec.GetValueOrDefault("buildingType")}</td>
  <td style='text-align:right'>{rec.GetValueOrDefault("area")}</td>
  <td style='text-align:right;color:#c00;font-weight:600'>{rec.GetValueOrDefault("totalPrice")}</td>
  <td style='text-align:right'>{rec.GetValueOrDefault("unitPrice")}</td>
  <td>{rec.GetValueOrDefault("floor")}</td>
</tr>"));
                var noteHtml = string.IsNullOrEmpty(r.Note) ? "" : $"<p style='color:#c77700;font-size:.85rem;margin:0 0 10px'>⚠ {System.Net.WebUtility.HtmlEncode(r.Note)}</p>";
                content = $@"
{noteHtml}
<div class='actions' style='margin-bottom:10px'>
  <p style='color:#888;font-size:.85rem;margin:0;flex:1'>共 {r.Records.Count} 筆</p>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('{tid}','{System.Net.WebUtility.HtmlEncode(p.Name)}實價登入',{{btn:this}})"">⬇ 下載Excel</button>
</div>
<div class='table-wrap'>
<table id='{tid}'>
<thead><tr><th>交易日期</th><th>地址</th><th>建物型態</th><th style='text-align:right'>坪數</th><th style='text-align:right'>總價(萬)</th><th style='text-align:right'>單價(萬/坪)</th><th>樓層</th></tr></thead>
<tbody>{rows}</tbody>
</table>
</div>
<script>initTable('{tid}', {{ cols: 7 }});</script>";
            }

            return $@"
<div class='section'>
  <h2 style='margin-top:0'>{System.Net.WebUtility.HtmlEncode(p.Name)} — {System.Net.WebUtility.HtmlEncode(p.Address)}</h2>
  {content}
</div>";
        }));

        var body = $@"
<h1>🔍 實價登入 — 持有房產附近成交資料</h1>
<p style='color:#666;font-size:.9rem;margin:0 0 16px'>資料來源：內政部不動產成交案件資訊。僅顯示目前持有中（未賣出）的房產，預設查詢近2年鄰近區域，查無資料時自動擴大範圍或延長至近3年。</p>
{sections}";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("實價登入", "house", "realestate", body));
#pragma warning restore CS0162
    }

    static async Task<(List<Dictionary<string, string?>> Records, string? Note)> FetchRealEstateWithFallback(
        string county, string keyword, IHttpClientFactory httpFactory)
    {
        var attempts = new List<(string? Keyword, int Years, string? Note)>
        {
            (keyword, 2, null),
        };
        if (!string.IsNullOrEmpty(keyword))
            attempts.Add((null, 2, "鄰近區域近2年查無資料，已擴大為全縣市範圍"));
        attempts.Add((keyword, 3, string.IsNullOrEmpty(keyword) ? "全縣市範圍近2年查無資料，已延長為近3年" : "鄰近區域近2年查無資料，已延長為近3年"));
        if (!string.IsNullOrEmpty(keyword))
            attempts.Add((null, 3, "鄰近區域近3年仍查無資料，已擴大為全縣市範圍並延長為近3年"));

        foreach (var (kw, years, note) in attempts)
        {
            var records = await FetchRealEstate(county, kw ?? "", years, httpFactory);
            if (records.Count > 0) return (records, note);
        }

        return ([], null);
    }

    // The per-county CSV endpoint (type=a&county=X) was retired by the government site; it now serves a season-wide
    // zip containing one CSV per county (e.g. "a_lvr_land_a.csv" for 臺北市/buy). Cache the zip per season since
    // multiple properties/fallback attempts often need the same season.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]>> SeasonZipCache = new();

    static Task<byte[]> GetSeasonZip(string season, IHttpClientFactory httpFactory) =>
        SeasonZipCache.GetOrAdd(season, s => DownloadSeasonZip(s, httpFactory));

    static async Task<byte[]> DownloadSeasonZip(string season, IHttpClientFactory httpFactory)
    {
        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        client.Timeout = TimeSpan.FromSeconds(60);
        var url = $"https://plvr.land.moi.gov.tw/DownloadSeason?season={season}&fileName=lvr_landcsv.zip";
        return await client.GetByteArrayAsync(url);
    }

    static async Task<List<Dictionary<string, string?>>> FetchRealEstate(string county, string keyword, int yearsBack, IHttpClientFactory httpFactory)
    {
        var today = DateTime.Today;

        var seasons = new List<(int year, int season)>();
        var cur = today;
        var quarterCount = yearsBack * 4;
        for (int i = 0; i < quarterCount; i++)
        {
            int s = (cur.Month - 1) / 3 + 1;
            var entry = (cur.Year, s);
            if (!seasons.Contains(entry)) seasons.Add(entry);
            cur = cur.AddMonths(-3);
        }

        var entryName = $"{county.ToLowerInvariant()}_lvr_land_a.csv";
        var records = new List<Dictionary<string, string?>>();
        foreach (var (yr, s) in seasons)
        {
            var season = $"{yr - 1911}S{s}";
            try
            {
                var zipBytes = await GetSeasonZip(season, httpFactory);
                using var ms = new MemoryStream(zipBytes);
                using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
                var entry = archive.GetEntry(entryName);
                if (entry == null) continue;
                using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
                var csv = await reader.ReadToEndAsync();
                var parsed = ParseCsv(csv, keyword, "buy", 50);
                records.AddRange(parsed);
                if (records.Count >= 150) break;
            }
            catch { }
        }

        return records.Take(150).ToList();
    }

    // ── API handlers ──────────────────────────────────────────────────────

    static async Task<IResult> AddProperty(PropertyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Address))
            return Results.BadRequest("請填寫房產名稱與地址");

        var prop = new Property(
            Guid.NewGuid().ToString("N")[..8],
            req.Name, req.Address, req.AreaPing, req.LandPing, req.BuildYear, req.TotalFloors, req.PurchasePrice,
            req.PurchaseDate ?? "", req.PropertyType ?? "其他", req.Notes ?? "",
            null, "", "");

        var props = LoadProperties();
        props.Add(prop);
        SaveProperties(props);
        return Results.Ok();
    }

    static async Task<IResult> UpdateProperty(string id, PropertyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Address))
            return Results.BadRequest("請填寫房產名稱與地址");

        var props = LoadProperties();
        var idx = props.FindIndex(p => p.Id == id);
        if (idx < 0) return Results.NotFound("房產不存在");

        props[idx] = props[idx] with
        {
            Name = req.Name, Address = req.Address, AreaPing = req.AreaPing, LandPing = req.LandPing,
            BuildYear = req.BuildYear, TotalFloors = req.TotalFloors, PurchasePrice = req.PurchasePrice,
            PurchaseDate = req.PurchaseDate ?? "", PropertyType = req.PropertyType ?? "其他", Notes = req.Notes ?? ""
        };
        SaveProperties(props);

        var expenses = LoadExpenses();
        for (int i = 0; i < expenses.Count; i++)
            if (expenses[i].PropertyId == id) expenses[i] = expenses[i] with { PropertyName = req.Name };
        SaveExpenses(expenses);

        return Results.Ok();
    }

    static IResult DeleteProperty(string id)
    {
        var props = LoadProperties();
        var target = props.FirstOrDefault(p => p.Id == id);
        if (target == null) return Results.NotFound();
        props.Remove(target);
        SaveProperties(props);

        var expenses = LoadExpenses();
        expenses.RemoveAll(e => e.PropertyId == id);
        SaveExpenses(expenses);
        return Results.Ok();
    }

    static async Task<IResult> AddPropertyExpense(string id, PropertyExpenseRequest req)
    {
        if (req.Amount <= 0) return Results.BadRequest("請輸入金額");
        var props = LoadProperties();
        var prop = props.FirstOrDefault(p => p.Id == id);
        if (prop == null) return Results.NotFound("房產不存在");

        var exp = new PropertyExpense(
            Guid.NewGuid().ToString("N")[..8],
            id, prop.Name, req.Date ?? DateTime.Today.ToString("yyyy-MM-dd"),
            req.Category ?? "其他", req.Amount, req.Vendor ?? "", req.Notes ?? "",
            req.ItemName ?? "", req.Brand ?? "");

        var expenses = LoadExpenses();
        expenses.Add(exp);
        SaveExpenses(expenses);
        return Results.Ok();
    }

    static IResult DeletePropertyExpense(string id)
    {
        var expenses = LoadExpenses();
        var target = expenses.FirstOrDefault(e => e.Id == id);
        if (target == null) return Results.NotFound();
        expenses.Remove(target);
        SaveExpenses(expenses);
        return Results.Ok();
    }

    static async Task<IResult> RecordSale(string id, PropertySaleRequest req)
    {
        if (req.SalePrice <= 0) return Results.BadRequest("請輸入賣出價格");
        var props = LoadProperties();
        var idx = props.FindIndex(p => p.Id == id);
        if (idx < 0) return Results.NotFound("房產不存在");

        props[idx] = props[idx] with
        {
            SalePrice = req.SalePrice,
            SaleDate = req.SaleDate ?? "",
            SaleNotes = req.SaleNotes ?? ""
        };
        SaveProperties(props);
        return Results.Ok();
    }

    static IResult ClearSale(string id)
    {
        var props = LoadProperties();
        var idx = props.FindIndex(p => p.Id == id);
        if (idx < 0) return Results.NotFound();
        props[idx] = props[idx] with { SalePrice = null, SaleDate = "", SaleNotes = "" };
        SaveProperties(props);
        return Results.Ok();
    }

    static List<Dictionary<string, string?>> ParseCsv(string csv, string? keyword, string tradeType, int maxRows)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return [];

        int headerIdx = 0;
        for (int i = 0; i < Math.Min(3, lines.Length); i++)
            if (lines[i].Contains("鄉鎮市區") || lines[i].Contains("交易年月日") || lines[i].Contains("總價元"))
                { headerIdx = i; break; }

        var headers = SplitCsvLine(lines[headerIdx]);
        var result = new List<Dictionary<string, string?>>();

        var dataStart = headerIdx + 1;
        // The row right after the Chinese header is an English translation header, not data — skip it.
        if (dataStart < lines.Length && System.Text.RegularExpressions.Regex.IsMatch(SplitCsvLine(lines[dataStart]).FirstOrDefault() ?? "", "^[A-Za-z]"))
            dataStart++;

        for (int i = dataStart; i < lines.Length && result.Count < maxRows; i++)
        {
            var cols = SplitCsvLine(lines[i]);
            if (cols.Length < 5) continue;

            var row = new Dictionary<string, string?>();
            for (int j = 0; j < Math.Min(headers.Length, cols.Length); j++)
                row[headers[j].Trim()] = cols[j].Trim().Trim('"');

            if (!string.IsNullOrEmpty(keyword))
            {
                var addr = row.GetValueOrDefault("土地位置建物門牌") ?? row.GetValueOrDefault("土地區段位置或建物區門牌") ?? row.GetValueOrDefault("租賃標的") ?? "";
                var district = row.GetValueOrDefault("鄉鎮市區") ?? "";
                if (!addr.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    !district.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var date = row.GetValueOrDefault("交易年月日") ?? "";
            if (date.Length >= 7 && int.TryParse(date[..3], out var rocY))
                date = (rocY + 1911) + "/" + date[3..5] + "/" + date[5..7];

            var district2 = row.GetValueOrDefault("鄉鎮市區") ?? "";
            var addr2 = row.GetValueOrDefault("土地位置建物門牌") ?? row.GetValueOrDefault("土地區段位置或建物區門牌") ?? row.GetValueOrDefault("租賃標的") ?? "";
            var fullAddr = district2 + addr2;

            var totalPriceRaw = row.GetValueOrDefault("總價元") ?? row.GetValueOrDefault("租賃總額元") ?? "0";
            decimal.TryParse(totalPriceRaw, out var totalPriceNum);
            var totalPriceWan = (totalPriceNum / 10000m).ToString("N0");

            var areaRaw = row.GetValueOrDefault("建物移轉總面積平方公尺") ?? row.GetValueOrDefault("建物移轉總面積") ?? "";
            decimal.TryParse(areaRaw, out var areaM2);
            var areaPing = areaM2 > 0 ? (areaM2 / 3.3058m).ToString("F1") : "";

            var unitPriceWan = "";
            if (decimal.TryParse(areaPing.Replace(",", ""), out var pingNum) && pingNum > 0 && totalPriceNum > 0)
                unitPriceWan = (totalPriceNum / 10000m / pingNum).ToString("N1");

            var buildingType = row.GetValueOrDefault("建物型態") ?? row.GetValueOrDefault("租賃型態") ?? "";
            var floor = row.GetValueOrDefault("移轉層次") ?? row.GetValueOrDefault("租賃層次") ?? "";

            result.Add(new Dictionary<string, string?>
            {
                ["date"] = date,
                ["address"] = fullAddr.Length > 40 ? fullAddr[..40] + "…" : fullAddr,
                ["buildingType"] = buildingType,
                ["area"] = areaPing,
                ["totalPrice"] = totalPriceWan,
                ["unitPrice"] = unitPriceWan,
                ["floor"] = floor,
            });
        }
        return result;
    }

    static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuote = false;
        var current = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '"') { inQuote = !inQuote; }
            else if (ch == ',' && !inQuote) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        result.Add(current.ToString());
        return [.. result];
    }

    // ── Data helpers ──────────────────────────────────────────────────────

    static List<Property> LoadProperties()
    {
        if (!File.Exists(PropertiesFile)) return [];
        return JsonSerializer.Deserialize<List<Property>>(File.ReadAllText(PropertiesFile), JsonOpts) ?? [];
    }

    static void SaveProperties(List<Property> props) =>
        File.WriteAllText(PropertiesFile, JsonSerializer.Serialize(props, JsonOpts));

    static List<PropertyExpense> LoadExpenses()
    {
        if (!File.Exists(PropertyExpensesFile)) return [];
        return JsonSerializer.Deserialize<List<PropertyExpense>>(File.ReadAllText(PropertyExpensesFile), JsonOpts) ?? [];
    }

    static void SaveExpenses(List<PropertyExpense> expenses) =>
        File.WriteAllText(PropertyExpensesFile, JsonSerializer.Serialize(expenses, JsonOpts));
}

// ── Models ────────────────────────────────────────────────────────────────────

public record Property(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("areaPing")] decimal AreaPing,
    [property: JsonPropertyName("landPing")] decimal LandPing,
    [property: JsonPropertyName("buildYear")] int BuildYear,
    [property: JsonPropertyName("totalFloors")] int TotalFloors,
    [property: JsonPropertyName("purchasePrice")] long PurchasePrice,
    [property: JsonPropertyName("purchaseDate")] string PurchaseDate,
    [property: JsonPropertyName("propertyType")] string PropertyType,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("salePrice")] long? SalePrice,
    [property: JsonPropertyName("saleDate")] string SaleDate,
    [property: JsonPropertyName("saleNotes")] string SaleNotes);

public record PropertyExpense(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("propertyId")] string PropertyId,
    [property: JsonPropertyName("propertyName")] string PropertyName,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("itemName")] string ItemName = "",
    [property: JsonPropertyName("brand")] string Brand = "");

public record PropertyRequest(
    string Name, string Address, decimal AreaPing, decimal LandPing, int BuildYear, int TotalFloors, long PurchasePrice,
    string? PurchaseDate, string? PropertyType, string? Notes);

public record PropertyExpenseRequest(
    string? Date, string? Category, long Amount, string? Vendor, string? Notes, string? ItemName, string? Brand);

public record PropertySaleRequest(long SalePrice, string? SaleDate, string? SaleNotes);
