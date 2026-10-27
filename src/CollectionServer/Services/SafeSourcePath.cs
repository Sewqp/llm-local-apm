namespace CollectionServer.Services;

public static class SafeSourcePath
{
    public static string? Resolve(string sourceDir, string rawPath)
    {
        try { return ResolveExisting(sourceDir, rawPath); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static string? ResolveExisting(string sourceDir, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || Path.IsPathRooted(rawPath) ||
            !Path.GetExtension(rawPath).Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            rawPath.IndexOfAny(['*', '?']) >= 0 || !Directory.Exists(sourceDir)) return null;
        var parts = rawPath.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".." or "." || p.Contains(':'))) return null;
        var root = Path.GetFullPath(sourceDir);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return null;
        var direct = Path.GetFullPath(Path.Combine(root, rawPath));
        if (Inside(root, direct) && File.Exists(direct) && NoLinks(root, direct)) return direct;
        if (parts.Length != 1 || Path.GetExtension(rawPath) != ".cs") return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        var matches = Directory.EnumerateFiles(root, rawPath, options).Where(p => Inside(root, p) && NoLinks(root, p)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool Inside(string root, string candidate) => candidate.StartsWith(
        root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool NoLinks(string root, string candidate)
    {
        for (var current = candidate; Inside(root, current); current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
