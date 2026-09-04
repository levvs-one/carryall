using System.Text;

namespace Handinpack.Core;

internal static class PackagePaths
{
    internal static bool TryNormalize(string? path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = "Нужно относительное имя файла без выхода за пределы комплекта.";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32000 || Path.IsPathRooted(path))
            return false;

        string candidate = path.Replace('\\', '/');
        foreach (string segment in candidate.Split('/'))
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
                return false;

            string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                 stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
            {
                error = "Имя зарезервировано Windows.";
                return false;
            }
        }

        try { _ = candidate.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return false; }
        normalized = candidate;
        error = string.Empty;
        return true;
    }

    internal static string Key(string path) => path.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    internal static bool IsWithin(string path, string directory)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(Path.EndsInDirectorySeparator(fullDirectory)
                ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Unsupported(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 |
            (FileAttributes)0x400000)) != 0;

    internal static void CheckAncestors(string path)
    {
        var ancestors = new Stack<string>();
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            ancestors.Push(current);
            current = Directory.GetParent(current)?.FullName;
        }
        while (ancestors.TryPop(out string? ancestor))
        {
            try
            {
                if (Unsupported(File.GetAttributes(ancestor)))
                    throw new IOException($"Ссылки и облачные заполнители не поддерживаются: {ancestor}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static bool IsIoError(Exception exception) => exception is IOException or InvalidDataException or
        UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
