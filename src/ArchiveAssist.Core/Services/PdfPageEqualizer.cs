using System.Text;
using ArchiveAssist.Core.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ArchiveAssist.Core.Services;

public sealed class PdfPageEqualizer : IPdfPageEqualizer
{
    private const string GeneratedFolderName = "Equalized PDFs";
    private const string ManifestFileName = "equalization_manifest.csv";

    public Task<EqualizationPreview> PreviewAsync(
        string rootFolder,
        int maxPagesPerPdf,
        IProgress<EqualizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRootAndLimit(rootFolder, maxPagesPerPdf);
        return Task.Run(
            () => BuildPreview(Path.GetFullPath(rootFolder), maxPagesPerPdf, progress, cancellationToken),
            cancellationToken);
    }

    public Task<EqualizationResult> EqualizeAsync(
        string rootFolder,
        string outputRoot,
        int maxPagesPerPdf,
        IProgress<EqualizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRootAndLimit(rootFolder, maxPagesPerPdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        return Task.Run(
            () => Equalize(
                Path.GetFullPath(rootFolder),
                Path.GetFullPath(outputRoot),
                maxPagesPerPdf,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private static EqualizationPreview BuildPreview(
        string rootFolder,
        int maxPagesPerPdf,
        IProgress<EqualizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pdfPaths = DiscoverSourcePdfs(rootFolder, cancellationToken);
        var pageCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < pdfPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pdfPaths[index];
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            pageCounts[path] = document.PageCount;
            progress?.Report(new(
                "Checking",
                Path.GetFileName(path),
                index + 1,
                pdfPaths.Count));
        }

        var folders = pdfPaths
            .GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var paths = group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
                var pages = paths.Sum(path => pageCounts[path]);
                var filesOverLimit = paths.Count(path => pageCounts[path] > maxPagesPerPdf);
                var relativeFolder = Path.GetRelativePath(rootFolder, group.Key);
                return new EqualizationFolderPlan(
                    group.Key,
                    string.IsNullOrWhiteSpace(relativeFolder) ? "." : relativeFolder,
                    paths.Count,
                    pages,
                    filesOverLimit,
                    filesOverLimit == 0 ? 0 : DivideRoundUp(pages, maxPagesPerPdf),
                    paths);
            })
            .OrderBy(folder => folder.SourceFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(rootFolder, maxPagesPerPdf, folders);
    }

    private static EqualizationResult Equalize(
        string rootFolder,
        string outputRoot,
        int maxPagesPerPdf,
        IProgress<EqualizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException($"The output path already exists: {outputRoot}");
        }

        var preview = BuildPreview(rootFolder, maxPagesPerPdf, progress, cancellationToken);
        if (!preview.HasWork)
        {
            return new(outputRoot, [], null);
        }

        var outputPaths = new List<string>();
        var manifestRows = new List<ManifestRow>();
        var completedFiles = 0;
        var createdOutputRoot = false;

        try
        {
            Directory.CreateDirectory(outputRoot);
            createdOutputRoot = true;

            foreach (var folder in preview.Folders.Where(folder => folder.NeedsWork))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = folder.RelativeFolder == "."
                    ? outputRoot
                    : Path.Combine(outputRoot, folder.RelativeFolder);
                Directory.CreateDirectory(destination);

                var writer = new PdfDocument();
                var outputNumber = 1;
                var pendingSources = new List<SourcePageReference>();
                try
                {
                    foreach (var sourcePath in folder.SourcePdfPaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new(
                            "Equalizing",
                            Path.GetFileName(sourcePath),
                            completedFiles,
                            preview.SourceFiles));

                        using var source = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                        for (var pageIndex = 0; pageIndex < source.PageCount; pageIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            writer.AddPage(source.Pages[pageIndex]);
                            pendingSources.Add(new(sourcePath, pageIndex + 1));

                            if (writer.PageCount == maxPagesPerPdf)
                            {
                                FlushWriter(ref writer, destination, outputNumber++, pendingSources, outputPaths, manifestRows);
                            }
                        }

                        completedFiles++;
                        progress?.Report(new(
                            "Equalizing",
                            Path.GetFileName(sourcePath),
                            completedFiles,
                            preview.SourceFiles));
                    }

                    FlushWriter(ref writer, destination, outputNumber, pendingSources, outputPaths, manifestRows);
                }
                finally
                {
                    writer.Dispose();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(outputRoot, ManifestFileName);
            WriteManifest(manifestPath, manifestRows);
            progress?.Report(new("Complete", string.Empty, preview.SourceFiles, preview.SourceFiles));
            return new(outputRoot, outputPaths, manifestPath);
        }
        catch
        {
            if (createdOutputRoot)
            {
                TryDeleteCreatedOutput(outputRoot);
            }
            throw;
        }
    }

    private static void FlushWriter(
        ref PdfDocument writer,
        string destination,
        int outputNumber,
        List<SourcePageReference> pendingSources,
        List<string> outputPaths,
        List<ManifestRow> manifestRows)
    {
        if (writer.PageCount == 0)
        {
            return;
        }

        var finalPath = Path.Combine(destination, $"Equalized_{outputNumber:000}.pdf");
        var temporaryPath = finalPath + ".part";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writer.Save(stream, closeStream: false);
            }
            File.Move(temporaryPath, finalPath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }

        outputPaths.Add(finalPath);
        manifestRows.AddRange(pendingSources.Select(source =>
            new ManifestRow(finalPath, source.SourcePdf, source.SourcePage)));
        pendingSources.Clear();
        writer.Dispose();
        writer = new PdfDocument();
    }

    private static void WriteManifest(string manifestPath, IReadOnlyList<ManifestRow> rows)
    {
        var temporaryPath = manifestPath + ".part";
        try
        {
            using (var writer = new StreamWriter(
                       new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None),
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.WriteLine("Output PDF,Source PDF,Source Page");
                foreach (var row in rows)
                {
                    writer.Write(CsvEscape(row.OutputPdf));
                    writer.Write(',');
                    writer.Write(CsvEscape(row.SourcePdf));
                    writer.Write(',');
                    writer.WriteLine(row.SourcePage);
                }
            }
            File.Move(temporaryPath, manifestPath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static List<string> DiscoverSourcePdfs(string rootFolder, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootFolder);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            paths.AddRange(Directory.GetFiles(current, "*.pdf", SearchOption.TopDirectoryOnly));

            foreach (var directory in Directory.GetDirectories(current))
            {
                if (string.Equals(Path.GetFileName(directory), GeneratedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(directory);
                }
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    private static void ValidateRootAndLimit(string rootFolder, int maxPagesPerPdf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolder);
        if (maxPagesPerPdf < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPagesPerPdf),
                "The maximum page count must be at least 1.");
        }

        if (!Directory.Exists(rootFolder))
        {
            throw new DirectoryNotFoundException($"Folder not found: {Path.GetFullPath(rootFolder)}");
        }
    }

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static string CsvEscape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";

    private static void TryDeleteCreatedOutput(string outputRoot)
    {
        try
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
        catch
        {
            // Preserve the original failure. The UI reports the output path so any
            // locked remnant can be inspected and removed deliberately.
        }
    }

    private sealed record SourcePageReference(string SourcePdf, int SourcePage);
    private sealed record ManifestRow(string OutputPdf, string SourcePdf, int SourcePage);
}
