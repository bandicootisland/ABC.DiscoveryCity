using System.Text;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders.OpenXml.Xlsx;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders.Xls;
using Telerik.Windows.Documents.Spreadsheet.Model;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Extracts text from Excel (xlsx/xls) and CSV files using Telerik SpreadProcessing.
/// Returns sentences organized by sheet and row for the ingestion pipeline.
/// </summary>
public static class SpreadsheetLoader
{
    /// <summary>
    /// Load a spreadsheet file and extract text as a list of sentence strings.
    /// Each row becomes a sentence, prefixed with sheet and row info.
    /// CSV files are read directly as text (no Telerik needed).
    /// </summary>
    public static SpreadsheetResult Load(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".csv" => LoadCsv(filePath),
            ".xlsx" => LoadExcel(filePath, new XlsxFormatProvider()),
            ".xls" => LoadExcel(filePath, new XlsFormatProvider()),
            _ => new SpreadsheetResult(Path.GetFileName(filePath))
        };
    }

    private static SpreadsheetResult LoadExcel(string filePath, IWorkbookFormatProvider provider)
    {
        var result = new SpreadsheetResult(Path.GetFileName(filePath));

        Workbook workbook;

        using (var stream = File.OpenRead(filePath))
        {
            workbook = provider.Import(stream);
        }

        foreach (var sheet in workbook.Sheets)
        {
            if (sheet is not Worksheet worksheet) continue;

            var usedRange = worksheet.UsedCellRange;
            if (usedRange == null) continue;

            int rowCount = usedRange.RowCount;
            int colCount = usedRange.ColumnCount;
            string sheetName = worksheet.Name;

            for (int row = 0; row < rowCount; row++)
            {
                var sb = new StringBuilder();
                bool hasContent = false;

                for (int col = 0; col < colCount; col++)
                {
                    var cell = worksheet.Cells[usedRange.FromIndex.RowIndex + row,
                                               usedRange.FromIndex.ColumnIndex + col];
                    var value = cell.GetValue().Value;
                    CellValueFormat format = cell.GetFormat().Value;
                    string cellText = value?.GetResultValueAsString(format) ?? "";

                    if (!string.IsNullOrWhiteSpace(cellText))
                    {
                        if (hasContent) sb.Append(" | ");
                        sb.Append(cellText.Trim());
                        hasContent = true;
                    }
                }

                if (hasContent)
                {
                    result.Rows.Add(new SpreadsheetRow(sheetName, row + 1, sb.ToString()));
                }
            }
        }

        result.SheetCount = workbook.Sheets.Count;
        result.Workbook = workbook;
        return result;
    }

    private static SpreadsheetResult LoadCsv(string filePath)
    {
        var result = new SpreadsheetResult(Path.GetFileName(filePath));
        var lines = File.ReadAllLines(filePath);
        int rowNum = 0;

        foreach (var line in lines)
        {
            rowNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Simple CSV: replace commas with pipe separator for readability
            var cleaned = line.Trim();
            result.Rows.Add(new SpreadsheetRow("Sheet1", rowNum, cleaned));
        }

        result.SheetCount = 1;
        return result;
    }

    /// <summary>
    /// Convert spreadsheet rows to the sentence string format used by the ingestion pipeline.
    /// </summary>
    public static List<string> ToSentences(SpreadsheetResult result)
    {
        var sentences = new List<string>(result.Rows.Count);
        int ordinal = 1;

        foreach (var row in result.Rows)
        {
            string prefix = result.SheetCount > 1
                ? $"[{row.SheetName}:R{row.RowNumber}]"
                : $"[R{row.RowNumber}]";

            sentences.Add($"\n[{ordinal}] {prefix} {row.Text}");
            ordinal++;
        }

        return sentences;
    }
}

public class SpreadsheetResult
{
    public string FileName { get; }
    public int SheetCount { get; set; }
    public List<SpreadsheetRow> Rows { get; } = new();
    public Workbook? Workbook { get; set; }

    public SpreadsheetResult(string fileName) => FileName = fileName;
}

public readonly record struct SpreadsheetRow(string SheetName, int RowNumber, string Text);
