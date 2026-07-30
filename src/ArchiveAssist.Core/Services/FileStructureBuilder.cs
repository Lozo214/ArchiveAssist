using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public static class FileStructureBuilder
{
    public static FileStructureNode Build(string rootPath, IEnumerable<ReportRow> reportRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(reportRows);

        var root = Path.GetFullPath(rootPath);
        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName)) rootName = root;

        var rootNode = new FileStructureNode(rootName, root, "Folder", isFolder: true);
        var folderNodes = new Dictionary<string, FileStructureNode>(StringComparer.OrdinalIgnoreCase)
        {
            [root] = rootNode
        };

        foreach (var row in reportRows.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = Path.GetDirectoryName(row.FullPath) ?? root;
            var parent = EnsureFolder(root, parentPath, rootNode, folderNodes);
            parent.Children.Add(new FileStructureNode(
                row.FileName,
                row.FullPath,
                row.TypeLabel,
                isFolder: false,
                row,
                parent.Depth + 1));

            var current = parent;
            while (true)
            {
                current.AddRollup(row);
                if (ReferenceEquals(current, rootNode)) break;
                var currentParentPath = Path.GetDirectoryName(current.FullPath);
                if (currentParentPath is null || !folderNodes.TryGetValue(currentParentPath, out current!))
                {
                    rootNode.AddRollup(row);
                    break;
                }
            }
        }

        SortChildren(rootNode);
        return rootNode;
    }

    private static FileStructureNode EnsureFolder(
        string root,
        string requestedPath,
        FileStructureNode rootNode,
        IDictionary<string, FileStructureNode> folderNodes)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal)) return rootNode;

        var currentPath = root;
        var currentNode = rootNode;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!folderNodes.TryGetValue(currentPath, out var child))
            {
                child = new FileStructureNode(
                    segment,
                    currentPath,
                    "Folder",
                    isFolder: true,
                    depth: currentNode.Depth + 1);
                folderNodes[currentPath] = child;
                currentNode.Children.Add(child);
            }
            currentNode = child;
        }

        return currentNode;
    }

    private static void SortChildren(FileStructureNode node)
    {
        var sorted = node.Children
            .OrderByDescending(child => child.IsFolder)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            node.Children.Add(child);
            SortChildren(child);
        }
    }
}
