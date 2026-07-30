using System.IO;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App.ViewModels;

public sealed class FileDetailsViewModel
{
    public FileDetailsViewModel(ReportRow row, IPdfMetadataReader metadataReader)
    {
        Row = row;
        FileName = row.FileName;
        FullPath = row.FullPath;
        InformationTabLabel = row.Kind == ArchiveFileKind.Pdf ? "Document Metadata" : "File Information";
        InformationFields = BuildInformationFields(row, metadataReader);
        ScanFields = BuildScanFields(row);
    }

    public ReportRow Row { get; }
    public string FileName { get; }
    public string FullPath { get; }
    public string InformationTabLabel { get; }
    public IReadOnlyList<DetailField> InformationFields { get; }
    public IReadOnlyList<DetailField> ScanFields { get; }

    private static IReadOnlyList<DetailField> BuildInformationFields(
        ReportRow row,
        IPdfMetadataReader metadataReader)
    {
        if (row.Kind == ArchiveFileKind.Pdf)
        {
            try
            {
                return metadataReader.Read(row.FullPath).Fields;
            }
            catch (Exception exception)
            {
                return
                [
                    new("File name", row.FileName),
                    new("Full path", row.FullPath),
                    new("File size", row.FileSizeLabel),
                    new("Metadata error", exception.Message)
                ];
            }
        }

        var fields = new List<DetailField>
        {
            new("File name", row.FileName),
            new("Full path", row.FullPath),
            new("Type", row.TypeLabel),
            new("File size", $"{row.FileSizeBytes:N0} bytes")
        };
        try
        {
            var file = new FileInfo(row.FullPath);
            if (file.Exists) fields.Add(new("Last modified", file.LastWriteTime.ToString("G")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            fields.Add(new("File information error", exception.Message));
        }
        return fields;
    }

    private static IReadOnlyList<DetailField> BuildScanFields(ReportRow row) =>
    [
        new("Type", row.TypeLabel),
        new("Relative folder", row.RelativeFolder),
        new("Page count", row.PageCount?.ToString("N0") ?? string.Empty),
        new("Documents", row.Documents.ToString("N0")),
        new("Maps", row.Maps.ToString("N0")),
        new("Photos", row.Photos.ToString("N0")),
        new("Photo backs", row.PhotoBacks.ToString("N0")),
        new("Production total", row.Total.ToString("N0")),
        new("Largest page size", row.LargestPageSizeLabel),
        new("Over page limit", row.OverLimitLabel),
        new("QA mode", row.Kind == ArchiveFileKind.Pdf ? row.QaMode.Name : string.Empty),
        new("OCR check status", row.OcrCheckStatusLabel),
        new("Searchable text", row.SearchableTextLabel),
        new("Pages with text", row.Kind == ArchiveFileKind.Pdf && row.QaMode != PdfQaMode.FastCountOnly
            ? row.PagesWithText.ToString("N0") : string.Empty),
        new("Pages without text", row.Kind == ArchiveFileKind.Pdf && row.OcrCheckComplete
            ? row.PagesWithoutText.ToString("N0") : string.Empty),
        new("Non-OCR page numbers", row.NonOcrPageNumbersLabel),
        new("Warnings", row.Warning ?? string.Empty),
        new("Error", row.Error ?? string.Empty)
    ];
}
