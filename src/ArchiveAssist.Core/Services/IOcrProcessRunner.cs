namespace ArchiveAssist.Core.Services;

public sealed record OcrProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

public sealed record OcrProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record OcrProcessOutput(
    string Line,
    bool IsStandardError);

public interface IOcrProcessRunner
{
    Task<OcrProcessResult> RunAsync(
        OcrProcessRequest request,
        CancellationToken cancellationToken = default,
        IProgress<OcrProcessOutput>? outputProgress = null);
}
