using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArchiveAssist.Core.Models;

public sealed class FileStructureNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public FileStructureNode(
        string name,
        string fullPath,
        string typeLabel,
        bool isFolder,
        ReportRow? reportRow = null,
        int depth = 0)
    {
        Name = name;
        FullPath = fullPath;
        TypeLabel = typeLabel;
        IsFolder = isFolder;
        ReportRow = reportRow;
        Depth = Math.Max(0, depth);
        if (reportRow is null) return;

        Documents = reportRow.Documents;
        Maps = reportRow.Maps;
        Photos = reportRow.Photos;
        WarningCount = reportRow.HasWarning ? 1 : 0;
    }

    public string Name { get; }
    public string FullPath { get; }
    public string TypeLabel { get; }
    public bool IsFolder { get; }
    public ReportRow? ReportRow { get; }
    public int Depth { get; }
    public double HierarchyIndent => Depth * 19d;
    public double HierarchyCompensation => Depth * -19d;
    public ObservableCollection<FileStructureNode> Children { get; } = [];
    public int Documents { get; private set; }
    public int Maps { get; private set; }
    public int Photos { get; private set; }
    public int WarningCount { get; private set; }
    public bool HasWarning => WarningCount > 0;

    public string WarningsLabel => IsFolder
        ? WarningCount switch
        {
            0 => string.Empty,
            1 => "1 warning file",
            _ => $"{WarningCount:N0} warning files"
        }
        : ReportRow?.IssuesLabel ?? string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    internal void AddRollup(ReportRow row)
    {
        Documents += row.Documents;
        Maps += row.Maps;
        Photos += row.Photos;
        if (row.HasWarning) WarningCount++;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
