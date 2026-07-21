using System.Text;
using System.Windows;
using System.Windows.Controls;
using WavyFi.Models;

namespace WavyFi.Ui;

/// <summary>Turns a DataGrid's visible columns (in display order) into
/// tab-separated rows or CSV — shared by the copy actions and file export.</summary>
public static class DataGridCsv
{
    public static List<DataGridColumn> VisibleColumns(DataGrid grid) =>
        grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

    public static string CellText(DataGridColumn column, object item) =>
        column.OnCopyingCellClipboardContent(item)?.ToString() ?? "";

    public static string TabSeparatedRows(DataGrid grid, IEnumerable<object> items)
    {
        var columns = VisibleColumns(grid);
        return string.Join(Environment.NewLine, items.Select(item =>
            string.Join("\t", columns.Select(c => CellText(c, item)))));
    }

    public static string Build(DataGrid grid, System.Collections.IEnumerable items)
    {
        var columns = VisibleColumns(grid);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => CsvFormat.Escape(c.Header?.ToString() ?? ""))));
        foreach (var item in items)
            sb.AppendLine(string.Join(",", columns.Select(c => CsvFormat.Escape(CellText(c, item)))));
        return sb.ToString();
    }
}
