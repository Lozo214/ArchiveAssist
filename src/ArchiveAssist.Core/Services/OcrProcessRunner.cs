using System.Diagnostics;
using System.Text;

namespace ArchiveAssist.Core.Services;

public sealed class OcrProcessRunner : IOcrProcessRunner
{
    public async Task<OcrProcessResult> RunAsync(
        OcrProcessRequest request,
        CancellationToken cancellationToken = default,
        IProgress<OcrProcessOutput>? outputProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {request.FileName}.");

        var standardOutput = ReadLinesAsync(process.StandardOutput, isStandardError: false, outputProgress);
        var standardError = ReadLinesAsync(process.StandardError, isStandardError: true, outputProgress);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardOutput, standardError);
            return new(process.ExitCode, standardOutput.Result, standardError.Result);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                // Cancellation cleanup is best effort.
            }
            try { await Task.WhenAll(standardOutput, standardError); }
            catch
            {
                // Stream cleanup after terminating the process is best effort.
            }
            throw;
        }
    }

    private static async Task<string> ReadLinesAsync(
        StreamReader reader,
        bool isStandardError,
        IProgress<OcrProcessOutput>? outputProgress)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line))
                outputProgress?.Report(new(line, isStandardError));
        }
        return output.ToString();
    }
}
