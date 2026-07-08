using ClosedXML.Excel;

public static class ExportModule
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapPost("/api/export/excel", ExportExcel);
    }

    static IResult ExportExcel(ExportRequest req)
    {
        if (req.Headers == null || req.Headers.Count == 0) return Results.BadRequest("缺少欄位");

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SanitizeSheetName(req.SheetName ?? "Sheet1"));

        for (int c = 0; c < req.Headers.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = req.Headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F0F4FF");
        }

        var rows = req.Rows ?? [];
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                var cell = ws.Cell(r + 2, c + 1);
                var raw = (row[c] ?? "").Trim();
                if (decimal.TryParse(raw.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var num))
                    cell.Value = num;
                else
                    cell.Value = raw;
            }
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        ws.RangeUsed()?.SetAutoFilter();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var fileName = string.IsNullOrWhiteSpace(req.FileName) ? "export.xlsx" : req.FileName;
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) fileName += ".xlsx";

        return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    static string SanitizeSheetName(string name)
    {
        foreach (var ch in "[]*?/\\:") name = name.Replace(ch, '-');
        return name.Length > 31 ? name[..31] : name;
    }
}

public record ExportRequest(string? FileName, string? SheetName, List<string> Headers, List<List<string>> Rows);
