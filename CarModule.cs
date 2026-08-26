using System.Text.Json;
using System.Text.Json.Serialization;

public static class CarModule
{
    static readonly string DataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    static readonly string CarsFile = Path.Combine(DataDir, "cars.json");
    static readonly string CarExpensesFile = Path.Combine(DataDir, "car_expenses.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/car", CarsPage);
        app.MapGet("/car/add", AddCarPage);
        app.MapGet("/car/expenses", ExpensesPage);
        app.MapGet("/car/expenses/add", AddExpensePage);
        app.MapGet("/car/{id}/edit", EditCarPage);
        app.MapGet("/car/{id}/sell", SellCarPage);

        app.MapPost("/api/car", AddCar);
        app.MapPut("/api/car/{id}", UpdateCar);
        app.MapDelete("/api/car/{id}", DeleteCar);
        app.MapPost("/api/car/expenses", AddExpense);
        app.MapDelete("/api/car/expenses/{id}", DeleteExpense);
        app.MapPost("/api/car/{id}/sell", RecordSale);
        app.MapDelete("/api/car/{id}/sell", ClearSale);
    }

    // ── Pages ─────────────────────────────────────────────────────────────

    static async Task CarsPage(HttpContext ctx)
    {
        var cars = LoadCars()
            .OrderBy(c => c.SalePrice.HasValue && c.SalePrice > 0 ? 1 : 0)
            .ThenByDescending(c => c.PurchaseDate)
            .ToList();
        var expenses = LoadExpenses();
        var totalExpense = expenses.Sum(e => e.Amount);

        var cards = cars.Count == 0
            ? "<div class='empty-state'><div class='icon'>🚗</div><p>尚無車輛資料</p><a href='/car/add' class='btn'>新增車輛</a></div>"
            : string.Join("", cars.Select(c =>
            {
                var carExp = expenses.Where(e => e.CarId == c.Id).Sum(e => e.Amount);
                var totalCost = c.PurchasePrice + carExp;
                var isSold = c.SalePrice.HasValue && c.SalePrice > 0;
                var profitHtml = "";
                if (isSold)
                {
                    var profit = c.SalePrice!.Value - totalCost;
                    var profitSign = profit >= 0 ? "+" : "";
                    var profitColor = profit >= 0 ? "#27ae60" : "#c0392b";
                    profitHtml = $@"
    <div class='card'><div class='lbl'>賣出價格</div><div class='val' style='font-size:1rem'>{c.SalePrice.Value:N0} 元</div></div>
    <div class='card'><div class='lbl'>賣出日期</div><div class='val' style='font-size:.95rem'>{c.SaleDate}</div></div>
    <div class='card'><div class='lbl'>損益</div><div class='val' style='color:{profitColor};font-weight:700'>{profitSign}{profit:N0} 元</div></div>";
                }
                var sellBtn = isSold
                    ? $"<a href='/car/{c.Id}/sell' class='btn btn-sm btn-outline' style='color:#27ae60;border-color:#27ae60'>已售出</a>"
                    : $"<a href='/car/{c.Id}/sell' class='btn btn-sm btn-outline'>記錄賣出</a>";

                var priceDetailHtml = "";
                if (c.ListPrice > 0 || c.Discount > 0 || c.DownPayment > 0)
                {
                    if (c.ListPrice > 0)
                        priceDetailHtml += $"<div class='card'><div class='lbl'>牌價</div><div class='val'>{c.ListPrice:N0} 元</div></div>";
                    if (c.Discount > 0)
                        priceDetailHtml += $"<div class='card'><div class='lbl'>折讓</div><div class='val' style='color:#27ae60'>－{c.Discount:N0} 元</div></div>";
                    if (c.DownPayment > 0)
                        priceDetailHtml += $"<div class='card'><div class='lbl'>頭款</div><div class='val'>{c.DownPayment:N0} 元</div></div>";
                }

                var warrantyHtml = "";
                if (!string.IsNullOrEmpty(c.WarrantyUntil) && DateTime.TryParse(c.WarrantyUntil, out var wu))
                {
                    var daysLeft = (wu.Date - DateTime.Today).Days;
                    var badge = daysLeft < 0
                        ? "<span style='color:#c0392b;font-size:.78rem'>（已過保）</span>"
                        : $"<span style='color:{(daysLeft <= 90 ? "#d80" : "#27ae60")};font-size:.78rem'>（剩 {daysLeft} 天）</span>";
                    warrantyHtml = $"<div class='card'><div class='lbl'>保固截止</div><div class='val' style='font-size:1rem'>{c.WarrantyUntil} {badge}</div></div>";
                }

                var mileageHtml = c.Mileage > 0
                    ? $"<div class='card'><div class='lbl'>里程數</div><div class='val' style='font-size:1rem'>{c.Mileage:N0} km</div></div>"
                    : "";

                var appraisalHtml = "";
                if (c.AppraisalValue.HasValue && c.AppraisalValue > 0)
                {
                    var meta = "";
                    if (DateTime.TryParse(c.AppraisalDate, out var ad))
                    {
                        var age = (DateTime.Today - ad.Date).Days;
                        meta = age > 30
                            ? $"<div style='font-size:.72rem;color:#d80'>{c.AppraisalDate} 更新 · 已 {age} 天，建議重查</div>"
                            : $"<div style='font-size:.72rem;color:#888'>{c.AppraisalDate} 更新</div>";
                    }
                    appraisalHtml = $"<div class='card'><div class='lbl'>鑑價參考</div><div class='val' style='color:#2c6fbb'>{c.AppraisalValue.Value:N0} 元</div>{meta}</div>";
                }

                return $@"
<div class='section'>
  <div class='actions' style='margin-bottom:12px'>
    <div style='flex:1'>
      <div style='font-size:1.1rem;font-weight:700'>{System.Net.WebUtility.HtmlEncode(c.Brand)} {System.Net.WebUtility.HtmlEncode(c.Model)}{(isSold ? " <span style='background:#27ae60;color:#fff;font-size:.7rem;padding:2px 7px;border-radius:10px;vertical-align:middle'>已售出</span>" : "")}</div>
      <div style='color:#666;font-size:.88rem'>車牌：{System.Net.WebUtility.HtmlEncode(c.PlateNo)}{(string.IsNullOrEmpty(c.Owner) ? "" : $" ／ 車主：{System.Net.WebUtility.HtmlEncode(c.Owner)}")}</div>
    </div>
    <a href='/car/expenses/add?carId={c.Id}' class='btn btn-sm'>新增花費</a>
    <a href='/car/expenses?carId={c.Id}' class='btn btn-sm btn-outline'>花費紀錄</a>
    <a href='/car/{c.Id}/edit' class='btn btn-sm btn-outline'>編輯</a>
    {sellBtn}
    <button class='btn btn-sm btn-danger' onclick='delCar(""{c.Id}"")'>刪除</button>
  </div>
  <div class='cards' style='margin-bottom:0'>
    <div class='card'><div class='lbl'>年份</div><div class='val'>{c.Year} 年</div></div>
    <div class='card'><div class='lbl'>顏色</div><div class='val' style='font-size:1rem'>{System.Net.WebUtility.HtmlEncode(c.Color)}</div></div>
    <div class='card'><div class='lbl'>購入價格</div><div class='val'>{c.PurchasePrice:N0} 元</div></div>
    {priceDetailHtml}
    <div class='card'><div class='lbl'>購入日期</div><div class='val' style='font-size:1rem'>{c.PurchaseDate}</div></div>
    {warrantyHtml}
    {mileageHtml}
    {appraisalHtml}
    <div class='card'><div class='lbl'>累計花費</div><div class='val pos'>{carExp:N0} 元</div></div>
    {profitHtml}
  </div>
  {(string.IsNullOrEmpty(c.Notes) ? "" : $"<div style='margin-top:10px;color:#666;font-size:.88rem'>備註：{System.Net.WebUtility.HtmlEncode(c.Notes)}</div>")}
  {(isSold && !string.IsNullOrEmpty(c.SaleNotes) ? $"<div style='margin-top:6px;color:#666;font-size:.88rem'>賣出備註：{System.Net.WebUtility.HtmlEncode(c.SaleNotes)}</div>" : "")}
</div>";
            }));

        var exportRows = string.Join("", cars.Select(c =>
        {
            var carExp = expenses.Where(e => e.CarId == c.Id).Sum(e => e.Amount);
            var isSold = c.SalePrice.HasValue && c.SalePrice > 0;
            var apprExport = c.AppraisalValue.HasValue ? c.AppraisalValue.Value.ToString() : "";
            return $@"<tr>
  <td>{System.Net.WebUtility.HtmlEncode(c.Brand)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(c.Model)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(c.PlateNo)}</td>
  <td>{c.Year}</td>
  <td>{System.Net.WebUtility.HtmlEncode(c.Color)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(c.Owner)}</td>
  <td>{c.PurchasePrice}</td>
  <td>{c.PurchaseDate}</td>
  <td>{c.WarrantyUntil}</td>
  <td>{(c.Mileage > 0 ? c.Mileage.ToString() : "")}</td>
  <td>{apprExport}</td>
  <td>{carExp}</td>
  <td>{(isSold ? c.SalePrice!.Value.ToString() : "")}</td>
  <td>{(isSold ? c.SaleDate : "")}</td>
  <td>{System.Net.WebUtility.HtmlEncode(c.Notes)}</td>
</tr>";
        }));

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <h1 style='margin:0;flex:1'>🚗 車輛管理</h1>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('car-export-table','車輛管理',{{btn:this}})"">⬇ 下載Excel</button>
  <a href='/car/expenses' class='btn btn-outline'>花費紀錄</a>
  <a href='/car/add' class='btn'>＋ 新增車輛</a>
</div>
{(cars.Count > 0 ? $@"<div class='cards'>
  <div class='card'><div class='lbl'>車輛數量</div><div class='val'>{cars.Count} 輛</div></div>
  <div class='card'><div class='lbl'>累計花費合計</div><div class='val pos'>{totalExpense:N0} 元</div></div>
</div>" : "")}
{cards}
<table id='car-export-table' style='display:none'>
<thead><tr><th>品牌</th><th>型號</th><th>車牌</th><th>出廠年份</th><th>顏色</th><th>車主</th><th>購入價格</th><th>購入日期</th><th>保固截止</th><th>里程數</th><th>鑑價參考</th><th>累計花費</th><th>賣出價格</th><th>賣出日期</th><th>備註</th></tr></thead>
<tbody>{exportRows}</tbody>
</table>
<div id='msg'></div>
<script>
async function delCar(id) {{
  if (!confirm('確定刪除此車輛？相關花費紀錄也會一併刪除。')) return;
  const r = await fetch('/api/car/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">刪除失敗</div>';
}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("車輛管理", "car", "list", body));
    }

    static async Task AddCarPage(HttpContext ctx)
    {
        var body = @"
<h1>🚗 新增車輛</h1>
<div class='form-card'>
<div id='msg'></div>
<div class='row2'>
  <div class='field'>
    <label>品牌</label>
    <input id='brand' placeholder='如：Toyota、Honda'>
  </div>
  <div class='field'>
    <label>型號</label>
    <input id='model' placeholder='如：Corolla、CR-V'>
  </div>
</div>
<div class='row3'>
  <div class='field'>
    <label>車牌號碼</label>
    <input id='plateNo' placeholder='如：ABC-1234'>
  </div>
  <div class='field'>
    <label>出廠年份</label>
    <input type='number' id='year' placeholder='如：2022' min='1990' max='2030'>
  </div>
  <div class='field'>
    <label>顏色</label>
    <input id='color' placeholder='如：白、銀、黑'>
  </div>
</div>
<div class='field'>
  <label>車主</label>
  <input id='owner' placeholder='選填，如：本人、配偶、公司'>
</div>
<div style='margin:0 0 8px;font-weight:600;color:#555;font-size:.9rem'>價格資訊</div>
<div class='row3'>
  <div class='field'>
    <label>汽車牌價（元）</label>
    <input type='number' id='listPrice' placeholder='廠商建議售價' oninput='calcPurchase()'>
  </div>
  <div class='field'>
    <label>折讓金額（元）</label>
    <input type='number' id='discount' placeholder='優惠折扣金額' oninput='calcPurchase()'>
  </div>
  <div class='field'>
    <label>頭款金額（元）</label>
    <input type='number' id='downPayment' placeholder='選填'>
  </div>
</div>
<div class='row2'>
  <div class='field'>
    <label>購入價格（元）</label>
    <input type='number' id='purchasePrice' placeholder='如：700000'>
  </div>
  <div class='field'>
    <label>購入日期</label>
    <input type='date' id='purchaseDate'>
  </div>
</div>
<div class='field'>
  <label>保固截止日</label>
  <input type='date' id='warrantyUntil'>
</div>
<div style='margin:0 0 8px;font-weight:600;color:#555;font-size:.9rem'>賣車參考</div>
<div class='row2'>
  <div class='field'>
    <label>里程數（公里）</label>
    <input type='number' id='mileage' placeholder='如：35000'>
  </div>
  <div class='field'>
    <label>鑑價金額（元）<small style='font-weight:400;color:#888'>　二手行情參考</small></label>
    <input type='number' id='appraisalValue' placeholder='選填'>
  </div>
</div>
<div style='font-size:.8rem;color:#888;margin:-8px 0 16px'>💡 可到 <a href='https://www.carp.com.tw/' target='_blank' rel='noopener'>CarP 汽車鑑價網</a> 輸入車型與里程查詢買賣行情後填入；此金額會存檔並記錄更新日期，超過一個月會提醒你更新。</div>
<div class='field'>
  <label>備註</label>
  <textarea id='notes' rows='2' placeholder='選填'></textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()'>確認新增</button>
  <a href='/car' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
document.getElementById('purchaseDate').value = new Date().toISOString().slice(0,10);
function calcPurchase() {
  const list = parseFloat(document.getElementById('listPrice').value) || 0;
  const disc = parseFloat(document.getElementById('discount').value) || 0;
  if (list > 0) document.getElementById('purchasePrice').value = Math.max(0, list - disc) || '';
}
async function submit() {
  const req = {
    brand: document.getElementById('brand').value.trim(),
    model: document.getElementById('model').value.trim(),
    plateNo: document.getElementById('plateNo').value.trim(),
    year: parseInt(document.getElementById('year').value) || 0,
    color: document.getElementById('color').value.trim(),
    owner: document.getElementById('owner').value.trim(),
    listPrice: parseFloat(document.getElementById('listPrice').value) || 0,
    discount: parseFloat(document.getElementById('discount').value) || 0,
    downPayment: parseFloat(document.getElementById('downPayment').value) || 0,
    purchasePrice: parseFloat(document.getElementById('purchasePrice').value) || 0,
    purchaseDate: document.getElementById('purchaseDate').value,
    warrantyUntil: document.getElementById('warrantyUntil').value,
    mileage: parseInt(document.getElementById('mileage').value) || 0,
    appraisalValue: document.getElementById('appraisalValue').value ? parseInt(document.getElementById('appraisalValue').value) : null,
    notes: document.getElementById('notes').value.trim()
  };
  if (!req.brand || !req.model || !req.plateNo) { showMsg('請填寫品牌、型號與車牌', 'err'); return; }
  const r = await fetch('/api/car', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(req)});
  if (r.ok) { showMsg('✓ 已新增！', 'ok'); setTimeout(() => location.href='/car', 1200); }
  else { const t = await r.text(); showMsg(t || '新增失敗', 'err'); }
}
function showMsg(m,t){document.getElementById('msg').innerHTML=`<div class='alert ${t}'>${m}</div>`;}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("新增車輛", "car", "add", body));
    }

    static async Task EditCarPage(string id, HttpContext ctx)
    {
        var cars = LoadCars();
        var car = cars.FirstOrDefault(c => c.Id == id);
        if (car == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("車輛不存在"); return; }

        var mileageVal = car.Mileage > 0 ? car.Mileage.ToString() : "";
        var appraisalVal = car.AppraisalValue.HasValue ? car.AppraisalValue.Value.ToString() : "";

        var body = $@"
<h1>🚗 編輯車輛</h1>
<div class='form-card'>
<div id='msg'></div>
<div class='row2'>
  <div class='field'>
    <label>品牌</label>
    <input id='brand' value='{System.Net.WebUtility.HtmlEncode(car.Brand)}'>
  </div>
  <div class='field'>
    <label>型號</label>
    <input id='model' value='{System.Net.WebUtility.HtmlEncode(car.Model)}'>
  </div>
</div>
<div class='row3'>
  <div class='field'>
    <label>車牌號碼</label>
    <input id='plateNo' value='{System.Net.WebUtility.HtmlEncode(car.PlateNo)}'>
  </div>
  <div class='field'>
    <label>出廠年份</label>
    <input type='number' id='year' value='{car.Year}' min='1990' max='2030'>
  </div>
  <div class='field'>
    <label>顏色</label>
    <input id='color' value='{System.Net.WebUtility.HtmlEncode(car.Color)}'>
  </div>
</div>
<div class='field'>
  <label>車主</label>
  <input id='owner' value='{System.Net.WebUtility.HtmlEncode(car.Owner)}'>
</div>
<div style='margin:0 0 8px;font-weight:600;color:#555;font-size:.9rem'>價格資訊</div>
<div class='row3'>
  <div class='field'>
    <label>汽車牌價（元）</label>
    <input type='number' id='listPrice' value='{car.ListPrice}' oninput='calcPurchase()'>
  </div>
  <div class='field'>
    <label>折讓金額（元）</label>
    <input type='number' id='discount' value='{car.Discount}' oninput='calcPurchase()'>
  </div>
  <div class='field'>
    <label>頭款金額（元）</label>
    <input type='number' id='downPayment' value='{car.DownPayment}'>
  </div>
</div>
<div class='row2'>
  <div class='field'>
    <label>購入價格（元）</label>
    <input type='number' id='purchasePrice' value='{car.PurchasePrice}'>
  </div>
  <div class='field'>
    <label>購入日期</label>
    <input type='date' id='purchaseDate' value='{car.PurchaseDate}'>
  </div>
</div>
<div class='field'>
  <label>保固截止日</label>
  <input type='date' id='warrantyUntil' value='{car.WarrantyUntil}'>
</div>
<div style='margin:0 0 8px;font-weight:600;color:#555;font-size:.9rem'>賣車參考</div>
<div class='row2'>
  <div class='field'>
    <label>里程數（公里）</label>
    <input type='number' id='mileage' value='{mileageVal}' placeholder='如：35000'>
  </div>
  <div class='field'>
    <label>鑑價金額（元）<small style='font-weight:400;color:#888'>　二手行情參考</small></label>
    <input type='number' id='appraisalValue' value='{appraisalVal}' placeholder='選填'>
  </div>
</div>
<div style='font-size:.8rem;color:#888;margin:-8px 0 16px'>💡 可到 <a href='https://www.carp.com.tw/' target='_blank' rel='noopener'>CarP 汽車鑑價網</a> 查詢買賣行情後填入；金額有異動才會更新「資料日期」，超過一個月會提醒更新。</div>
<div class='field'>
  <label>備註</label>
  <textarea id='notes' rows='2'>{System.Net.WebUtility.HtmlEncode(car.Notes)}</textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()'>儲存變更</button>
  <a href='/car' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
function calcPurchase() {{
  const list = parseFloat(document.getElementById('listPrice').value) || 0;
  const disc = parseFloat(document.getElementById('discount').value) || 0;
  if (list > 0) document.getElementById('purchasePrice').value = Math.max(0, list - disc) || '';
}}
async function submit() {{
  const req = {{
    brand: document.getElementById('brand').value.trim(),
    model: document.getElementById('model').value.trim(),
    plateNo: document.getElementById('plateNo').value.trim(),
    year: parseInt(document.getElementById('year').value) || 0,
    color: document.getElementById('color').value.trim(),
    owner: document.getElementById('owner').value.trim(),
    listPrice: parseFloat(document.getElementById('listPrice').value) || 0,
    discount: parseFloat(document.getElementById('discount').value) || 0,
    downPayment: parseFloat(document.getElementById('downPayment').value) || 0,
    purchasePrice: parseFloat(document.getElementById('purchasePrice').value) || 0,
    purchaseDate: document.getElementById('purchaseDate').value,
    warrantyUntil: document.getElementById('warrantyUntil').value,
    mileage: parseInt(document.getElementById('mileage').value) || 0,
    appraisalValue: document.getElementById('appraisalValue').value ? parseInt(document.getElementById('appraisalValue').value) : null,
    notes: document.getElementById('notes').value.trim()
  }};
  if (!req.brand || !req.model || !req.plateNo) {{ showMsg('請填寫品牌、型號與車牌', 'err'); return; }}
  const r = await fetch('/api/car/{id}', {{method:'PUT', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) {{ showMsg('✓ 已儲存！', 'ok'); setTimeout(() => location.href='/car', 1200); }}
  else {{ const t = await r.text(); showMsg(t || '儲存失敗', 'err'); }}
}}
function showMsg(m,t){{document.getElementById('msg').innerHTML=`<div class='alert ${{t}}'>${{m}}</div>`;}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("編輯車輛", "car", "list", body));
    }

    static async Task SellCarPage(string id, HttpContext ctx)
    {
        var cars = LoadCars();
        var car = cars.FirstOrDefault(c => c.Id == id);
        if (car == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("車輛不存在"); return; }

        var expenses = LoadExpenses().Where(e => e.CarId == id).Sum(e => e.Amount);
        var totalCost = car.PurchasePrice + expenses;
        var isSold = car.SalePrice.HasValue && car.SalePrice > 0;
        var apprCard = car.AppraisalValue.HasValue && car.AppraisalValue > 0
            ? $"<div class='card'><div class='lbl'>鑑價參考{(string.IsNullOrEmpty(car.AppraisalDate) ? "" : $" <small style='color:#888;font-weight:400'>{car.AppraisalDate}</small>")}</div><div class='val' style='color:#2c6fbb'>{car.AppraisalValue.Value:N0} 元</div></div>"
            : "";

        var existingSalePrice = car.SalePrice.HasValue ? car.SalePrice.Value.ToString() : "";
        var existingSaleDate = car.SaleDate ?? "";
        var existingSaleNotes = System.Net.WebUtility.HtmlEncode(car.SaleNotes ?? "");

        var clearBtn = isSold ? $@"
<div style='margin-top:16px;padding-top:16px;border-top:1px solid #eee'>
  <button class='btn btn-outline btn-danger' onclick='clearSale()' style='font-size:.88rem'>清除賣出紀錄</button>
</div>" : "";

        var body = $@"
<div>
  <a href='/car' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回車輛列表</a>
  <h1 style='margin:4px 0 20px'>💰 賣出資訊 — {System.Net.WebUtility.HtmlEncode(car.Brand)} {System.Net.WebUtility.HtmlEncode(car.Model)}</h1>
</div>
<div class='cards' style='margin-bottom:20px'>
  <div class='card'><div class='lbl'>購入價格</div><div class='val'>{car.PurchasePrice:N0} 元</div></div>
  <div class='card'><div class='lbl'>累計花費</div><div class='val pos'>{expenses:N0} 元</div></div>
  <div class='card'><div class='lbl'>持有總成本</div><div class='val' style='font-weight:700'>{totalCost:N0} 元</div></div>
  {apprCard}
</div>
<div class='form-card'>
<div id='msg'></div>
<div class='row2'>
  <div class='field'>
    <label>賣出價格（元）</label>
    <input type='number' id='salePrice' placeholder='如：500000' value='{existingSalePrice}'>
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
  <textarea id='saleNotes' rows='2' placeholder='選填，如：自售、透過車商'>{existingSaleNotes}</textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()'>儲存賣出資訊</button>
  <a href='/car' class='btn btn-outline'>取消</a>
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
  const r = await fetch('/api/car/{id}/sell', {{method:'POST', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) {{ showMsg('✓ 已儲存！', 'ok'); setTimeout(() => location.href='/car', 1200); }}
  else {{ const t = await r.text(); showMsg(t || '儲存失敗', 'err'); }}
}}
async function clearSale() {{
  if (!confirm('確定清除賣出紀錄？')) return;
  const r = await fetch('/api/car/{id}/sell', {{method:'DELETE'}});
  if (r.ok) location.reload();
  else showMsg('清除失敗', 'err');
}}
function showMsg(m,t){{document.getElementById('msg').innerHTML=`<div class='alert ${{t}}'>${{m}}</div>`;}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("賣出資訊", "car", "list", body));
    }

    static async Task ExpensesPage(HttpContext ctx)
    {
        var cars = LoadCars();
        var expenses = LoadExpenses().OrderByDescending(e => e.Date).ToList();
        var total = expenses.Sum(e => e.Amount);

        var filterCarId = ctx.Request.Query["carId"].ToString();
        var filtered = string.IsNullOrEmpty(filterCarId) ? expenses : expenses.Where(e => e.CarId == filterCarId).ToList();

        var carOptions = string.Join("", cars.Select(c =>
            $"<option value='{c.Id}'{(c.Id == filterCarId ? " selected" : "")}>{c.Brand} {c.Model} ({c.PlateNo})</option>"));

        var rows = filtered.Count == 0
            ? "<tr><td colspan='7' style='text-align:center;padding:30px;color:#999'>尚無花費紀錄</td></tr>"
            : string.Join("", filtered.Select(e => $@"<tr>
  <td>{e.Date}</td>
  <td>{System.Net.WebUtility.HtmlEncode(e.CarName)}</td>
  <td><span class='tag'>{System.Net.WebUtility.HtmlEncode(e.Category)}</span></td>
  <td style='text-align:right;color:#c00;font-weight:600'>{e.Amount:N0}</td>
  <td>{System.Net.WebUtility.HtmlEncode(e.Vendor)}</td>
  <td>{System.Net.WebUtility.HtmlEncode(e.Notes)}</td>
  <td><button class='btn btn-danger btn-sm' onclick='del(""{e.Id}"")'>刪除</button></td>
</tr>"));

        var catTotals = filtered.GroupBy(e => e.Category)
            .Select(g => $"<div class='card'><div class='lbl'>{g.Key}</div><div class='val' style='font-size:1rem'>{g.Sum(x => x.Amount):N0} 元</div></div>");

        var body = $@"
<div class='actions' style='margin-bottom:16px'>
  <div>
    <a href='/car' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回車輛列表</a>
    <h1 style='margin:4px 0 0'>🚗 車輛花費紀錄</h1>
  </div>
  <button class='btn btn-sm btn-outline' onclick=""exportTableToExcel('exp-table','車輛花費紀錄',{{skipCols:[6],btn:this}})"">⬇ 下載Excel</button>
  <a href='/car/expenses/add' class='btn'>＋ 新增花費</a>
</div>
<div class='cards'>
  <div class='card'><div class='lbl'>篩選後花費</div><div class='val pos' id='exp-filtered'>{filtered.Sum(e => e.Amount):N0} 元</div></div>
  <div class='card'><div class='lbl'>總花費</div><div class='val'>{total:N0} 元</div></div>
  {string.Join("", catTotals)}
</div>
<div style='margin-bottom:16px;display:flex;gap:12px;align-items:center'>
  <label style='display:inline;font-size:.88rem;color:#666'>篩選車輛：</label>
  <select style='width:auto;padding:6px 12px' onchange=""location.href='/car/expenses'+(this.value?'?carId='+this.value:'')"">
    <option value=''>全部車輛</option>
    {carOptions}
  </select>
</div>
<div class='table-wrap'>
<table id='exp-table'>
<thead><tr>
  <th>日期</th><th>車輛</th><th>類別</th><th style='text-align:right'>金額</th><th>店家／廠商</th><th>備註</th><th></th>
</tr></thead>
<tbody>{rows}</tbody>
<tfoot><tr>
  <td>篩選合計</td><td></td><td></td>
  <td style='text-align:right;font-weight:600' id='exp-tf-total'>{filtered.Sum(e => e.Amount):N0}</td>
  <td></td><td></td><td></td>
</tr></tfoot>
</table>
</div>
<div id='msg' style='margin-top:12px'></div>
<script>
async function del(id) {{
  if (!confirm('確定刪除？')) return;
  const r = await fetch('/api/car/expenses/' + id, {{method:'DELETE'}});
  if (r.ok) location.reload();
  else document.getElementById('msg').innerHTML = '<div class=""alert err"">刪除失敗</div>';
}}
setupColumns('exp-table', 'car-exp', {{ labels: {{ 6: '操作' }} }});
initTable('exp-table', {{
  cols: 7,
  noFilter: [3, 6],
  sumCols: [{{col: 3, id: 'exp-tf-total'}}],
  onFilter: function(vis) {{
    let sum = 0;
    for (const r of vis) {{ const c = cellByCi(r, 3); sum += parseFloat((c ? c.textContent : '').replace(/,/g,'')) || 0; }}
    const el = document.getElementById('exp-filtered');
    if (el) el.textContent = Math.round(sum).toLocaleString('zh-TW') + ' 元';
  }}
}});
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("車輛花費紀錄", "car", "expenses", body));
    }

    static async Task AddExpensePage(HttpContext ctx)
    {
        var cars = LoadCars();
        var preselect = ctx.Request.Query["carId"].ToString();
        var carOptions = string.Join("", cars.Select(c =>
            $"<option value='{c.Id}'{(c.Id == preselect ? " selected" : "")}>{c.Brand} {c.Model} ({c.PlateNo})</option>"));

        var noCar = cars.Count == 0
            ? "<div class='alert err'>請先<a href='/car/add'>新增車輛</a>再記錄花費</div>"
            : "";

        // derive backlink: if preselect exists go back to filtered list, else list
        var backUrl = string.IsNullOrEmpty(preselect) ? "/car/expenses" : $"/car/expenses?carId={preselect}";

        var body = $@"
<div>
  <a href='{backUrl}' style='color:#888;font-size:.88rem;text-decoration:none'>← 返回花費紀錄</a>
  <h1 style='margin:4px 0 20px'>🚗 新增車輛花費</h1>
</div>
<div class='form-card'>
<div id='msg'>{noCar}</div>
<div class='field'>
  <label>車輛</label>
  <select id='carId'>
    <option value=''>請選擇車輛</option>
    {carOptions}
  </select>
</div>
<div class='row2'>
  <div class='field'>
    <label>類別</label>
    <select id='category'>
      <option>保養</option><option>維修</option><option>保險</option><option>燃料費</option>
      <option>停車費</option><option>過路費</option><option>牌照稅</option><option>燃料稅</option>
      <option>洗車</option><option>配件</option><option>其他</option>
    </select>
  </div>
  <div class='field'>
    <label>日期</label>
    <input type='date' id='date'>
  </div>
</div>
<div class='field'>
  <label>金額（元）</label>
  <input type='number' id='amount' placeholder='如：3000'>
</div>
<div class='field'>
  <label>店家／廠商</label>
  <input id='vendor' placeholder='選填，如：XX保養廠、OO汽車'>
</div>
<div class='field'>
  <label>備註</label>
  <textarea id='notes' rows='2' placeholder='選填，如：機油更換、定期保養'></textarea>
</div>
<div class='actions'>
  <button class='btn' onclick='submit()' {(cars.Count == 0 ? "disabled" : "")}>確認新增</button>
  <a href='{backUrl}' class='btn btn-outline'>取消</a>
</div>
</div>
<script>
document.getElementById('date').value = new Date().toISOString().slice(0,10);
async function submit() {{
  const carId = document.getElementById('carId').value;
  if (!carId) {{ showMsg('請選擇車輛', 'err'); return; }}
  const req = {{
    carId,
    category: document.getElementById('category').value,
    date: document.getElementById('date').value,
    amount: parseFloat(document.getElementById('amount').value) || 0,
    vendor: document.getElementById('vendor').value.trim(),
    notes: document.getElementById('notes').value.trim()
  }};
  if (req.amount <= 0) {{ showMsg('請輸入金額', 'err'); return; }}
  const r = await fetch('/api/car/expenses', {{method:'POST', headers:{{'Content-Type':'application/json'}}, body:JSON.stringify(req)}});
  if (r.ok) {{ showMsg('✓ 已新增！', 'ok'); setTimeout(() => location.href='{backUrl}', 1200); }}
  else {{ const t = await r.text(); showMsg(t || '新增失敗', 'err'); }}
}}
function showMsg(m,t){{document.getElementById('msg').innerHTML=`<div class='alert ${{t}}'>${{m}}</div>`;}}
</script>";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(SharedLayout.Page("新增車輛花費", "car", "addexp", body));
    }

    // ── API handlers ──────────────────────────────────────────────────────

    static async Task<IResult> AddCar(CarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Brand) || string.IsNullOrWhiteSpace(req.Model) || string.IsNullOrWhiteSpace(req.PlateNo))
            return Results.BadRequest("請填寫品牌、型號與車牌");

        var car = new Car(
            Guid.NewGuid().ToString("N")[..8],
            req.Brand, req.Model, req.PlateNo, req.Year,
            req.Color ?? "", req.Owner ?? "", req.ListPrice, req.Discount, req.DownPayment,
            req.PurchasePrice, req.PurchaseDate ?? "", req.Notes ?? "", req.WarrantyUntil ?? "",
            req.Mileage,
            req.AppraisalValue,
            req.AppraisalValue.HasValue ? DateTime.Today.ToString("yyyy-MM-dd") : "",
            req.AppraisalValue.HasValue ? "手動" : "");

        var cars = LoadCars();
        cars.Add(car);
        SaveCars(cars);
        return Results.Ok();
    }

    static async Task<IResult> UpdateCar(string id, CarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Brand) || string.IsNullOrWhiteSpace(req.Model) || string.IsNullOrWhiteSpace(req.PlateNo))
            return Results.BadRequest("請填寫品牌、型號與車牌");

        var cars = LoadCars();
        var idx = cars.FindIndex(c => c.Id == id);
        if (idx < 0) return Results.NotFound("車輛不存在");

        var existing = cars[idx];
        // 鑑價：金額有異動才更新資料日期與來源（維持「最後更新時間」語意，供逾月提醒）
        var apprDate = existing.AppraisalDate;
        var apprSource = existing.AppraisalSource;
        if (!req.AppraisalValue.HasValue)
        {
            apprDate = ""; apprSource = "";
        }
        else if (req.AppraisalValue != existing.AppraisalValue || string.IsNullOrEmpty(existing.AppraisalDate))
        {
            apprDate = DateTime.Today.ToString("yyyy-MM-dd");
            apprSource = "手動";
        }

        cars[idx] = existing with
        {
            Brand = req.Brand, Model = req.Model, PlateNo = req.PlateNo, Year = req.Year,
            Color = req.Color ?? "", Owner = req.Owner ?? "", ListPrice = req.ListPrice, Discount = req.Discount,
            DownPayment = req.DownPayment, PurchasePrice = req.PurchasePrice, PurchaseDate = req.PurchaseDate ?? "",
            Notes = req.Notes ?? "", WarrantyUntil = req.WarrantyUntil ?? "",
            Mileage = req.Mileage, AppraisalValue = req.AppraisalValue,
            AppraisalDate = apprDate, AppraisalSource = apprSource
        };
        SaveCars(cars);

        var expenses = LoadExpenses();
        var carName = $"{req.Brand} {req.Model}";
        for (int i = 0; i < expenses.Count; i++)
            if (expenses[i].CarId == id) expenses[i] = expenses[i] with { CarName = carName };
        SaveExpenses(expenses);

        return Results.Ok();
    }

    static IResult DeleteCar(string id)
    {
        var cars = LoadCars();
        var target = cars.FirstOrDefault(c => c.Id == id);
        if (target == null) return Results.NotFound();
        cars.Remove(target);
        SaveCars(cars);

        var expenses = LoadExpenses();
        expenses.RemoveAll(e => e.CarId == id);
        SaveExpenses(expenses);
        return Results.Ok();
    }

    static async Task<IResult> AddExpense(CarExpenseRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CarId) || req.Amount <= 0)
            return Results.BadRequest("請選擇車輛並輸入金額");

        var cars = LoadCars();
        var car = cars.FirstOrDefault(c => c.Id == req.CarId);
        if (car == null) return Results.NotFound("車輛不存在");

        var exp = new CarExpense(
            Guid.NewGuid().ToString("N")[..8],
            req.CarId, $"{car.Brand} {car.Model}",
            req.Date ?? DateTime.Today.ToString("yyyy-MM-dd"),
            req.Category ?? "其他", req.Amount, req.Vendor ?? "", req.Notes ?? "");

        var expenses = LoadExpenses();
        expenses.Add(exp);
        SaveExpenses(expenses);
        return Results.Ok();
    }

    static IResult DeleteExpense(string id)
    {
        var expenses = LoadExpenses();
        var target = expenses.FirstOrDefault(e => e.Id == id);
        if (target == null) return Results.NotFound();
        expenses.Remove(target);
        SaveExpenses(expenses);
        return Results.Ok();
    }

    static async Task<IResult> RecordSale(string id, CarSaleRequest req)
    {
        if (req.SalePrice <= 0) return Results.BadRequest("請輸入賣出價格");
        var cars = LoadCars();
        var idx = cars.FindIndex(c => c.Id == id);
        if (idx < 0) return Results.NotFound("車輛不存在");

        cars[idx] = cars[idx] with
        {
            SalePrice = req.SalePrice,
            SaleDate = req.SaleDate ?? "",
            SaleNotes = req.SaleNotes ?? ""
        };
        SaveCars(cars);
        return Results.Ok();
    }

    static IResult ClearSale(string id)
    {
        var cars = LoadCars();
        var idx = cars.FindIndex(c => c.Id == id);
        if (idx < 0) return Results.NotFound();
        cars[idx] = cars[idx] with { SalePrice = null, SaleDate = "", SaleNotes = "" };
        SaveCars(cars);
        return Results.Ok();
    }

    // ── Data helpers ──────────────────────────────────────────────────────

    static List<Car> LoadCars()
    {
        if (!File.Exists(CarsFile)) return [];
        return JsonSerializer.Deserialize<List<Car>>(File.ReadAllText(CarsFile), JsonOpts) ?? [];
    }

    static void SaveCars(List<Car> cars) =>
        File.WriteAllText(CarsFile, JsonSerializer.Serialize(cars, JsonOpts));

    static List<CarExpense> LoadExpenses()
    {
        if (!File.Exists(CarExpensesFile)) return [];
        return JsonSerializer.Deserialize<List<CarExpense>>(File.ReadAllText(CarExpensesFile), JsonOpts) ?? [];
    }

    static void SaveExpenses(List<CarExpense> expenses) =>
        File.WriteAllText(CarExpensesFile, JsonSerializer.Serialize(expenses, JsonOpts));
}

// ── Models ────────────────────────────────────────────────────────────────────

public record Car(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("brand")] string Brand,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("plateNo")] string PlateNo,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("listPrice")] long ListPrice,
    [property: JsonPropertyName("discount")] long Discount,
    [property: JsonPropertyName("downPayment")] long DownPayment,
    [property: JsonPropertyName("purchasePrice")] long PurchasePrice,
    [property: JsonPropertyName("purchaseDate")] string PurchaseDate,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("warrantyUntil")] string WarrantyUntil = "",
    [property: JsonPropertyName("mileage")] long Mileage = 0,
    [property: JsonPropertyName("appraisalValue")] long? AppraisalValue = null,
    [property: JsonPropertyName("appraisalDate")] string AppraisalDate = "",
    [property: JsonPropertyName("appraisalSource")] string AppraisalSource = "",
    [property: JsonPropertyName("salePrice")] long? SalePrice = null,
    [property: JsonPropertyName("saleDate")] string SaleDate = "",
    [property: JsonPropertyName("saleNotes")] string SaleNotes = "");

public record CarExpense(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("carId")] string CarId,
    [property: JsonPropertyName("carName")] string CarName,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("notes")] string Notes);

public record CarRequest(
    string Brand, string Model, string PlateNo, int Year,
    string? Color, string? Owner, long ListPrice, long Discount, long DownPayment,
    long PurchasePrice, string? PurchaseDate, string? Notes, string? WarrantyUntil = null,
    long Mileage = 0, long? AppraisalValue = null);

public record CarExpenseRequest(
    string CarId, string? Category, string? Date, long Amount, string? Vendor, string? Notes);

public record CarSaleRequest(long SalePrice, string? SaleDate, string? SaleNotes);
