using System.IO;

namespace ArchiveAssist.App.Services;

public static class FileSystemRenameService
{
    private static readonly HashSet<string> ReservedWindowsNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

    public static string Rename(string sourcePath, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ValidateName(newName);

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var isFile = File.Exists(fullSourcePath);
        var isFolder = Directory.Exists(fullSourcePath);
        if (!isFile && !isFolder)
        {
            throw new FileNotFoundException(
                "The selected file or folder is no longer available.",
                fullSourcePath);
        }

        var parentPath = Path.GetDirectoryName(
            fullSourcePath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            throw new InvalidOperationException("A drive root cannot be renamed here.");
        }

        var targetPath = Path.Combine(parentPath, newName);
        if (string.Equals(fullSourcePath, targetPath, StringComparison.Ordinal))
        {
            return fullSourcePath;
        }

        var isCaseOnlyRename = string.Equals(
            fullSourcePath,
            targetPath,
            StringComparison.OrdinalIgnoreCase);
        if (!isCaseOnlyRename &&
            (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            throw new IOException(
                $"A file or folder named \"{newName}\" already exists in this location.");
        }

        if (isCaseOnlyRename)
        {
            return RenameWithTemporaryPath(fullSourcePath, targetPath, isFolder);
        }

        if (isFolder)
        {
            Directory.Move(fullSourcePath, targetPath);
        }
        else
        {
            File.Move(fullSourcePath, targetPath);
        }

        return targetPath;
    }

    public static void ValidateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Enter a file or folder name.", nameof(newName));
        }

        if (!string.Equals(newName, newName.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Names cannot begin or end with spaces.",
                nameof(newName));
        }

        if (newName.EndsWith(".", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Names cannot end with a period.",
                nameof(newName));
        }

        if (newName is "." or ".." ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            newName.Contains(Path.DirectorySeparatorChar) ||
            newName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "The name contains characters that Windows does not allow.",
                nameof(newName));
        }

        if (newName.Length > 255)
        {
            throw new ArgumentException(
                "The name cannot be longer than 255 characters.",
                nameof(newName));
        }

        var baseName = Path.GetFileNameWithoutExtension(newName);
        if (ReservedWindowsNames.Contains(baseName))
        {
            throw new ArgumentException(
                $"\"{baseName}\" is reserved by Windows. Choose another name.",
                nameof(newName));
        }
    }

    private static string RenameWithTemporaryPath(
        string sourcePath,
        string targetPath,
        bool isFolder)
    {
        var parentPath = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("The selected item has no parent folder.");
        var temporaryPath = Path.Combine(
            parentPath,
            $".archiveassist-rename-{Guid.NewGuid():N}.tmp");
        try
        {
            Move(sourcePath, temporaryPath, isFolder);
            Move(temporaryPath, targetPath, isFolder);
            return targetPath;
        }
        catch
        {
            if (!File.Exists(sourcePath) &&
                !Directory.Exists(sourcePath) &&
                (File.Exists(temporaryPath) || Directory.Exists(temporaryPath)))
            {
                Move(temporaryPath, sourcePath, isFolder);
            }

            throw;
        }
    }

    private static void Move(string sourcePath, string targetPath, bool isFolder)
    {
        if (isFolder)
        {
            Directory.Move(sourcePath, targetPath);
        }
        else
        {
            File.Move(sourcePath, targetPath);
        }
    }
}
