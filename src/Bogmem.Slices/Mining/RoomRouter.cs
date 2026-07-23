namespace Bogmem.Slices.Mining;

public sealed class RoomRouter(ProjectConfig configuration)
{
    public string Route(string sourceRoot, string sourceFile, string content)
    {
        var relative = Path.GetRelativePath(sourceRoot, sourceFile)
            .Replace(Path.DirectorySeparatorChar, '/');
        var folder = Path.GetDirectoryName(relative)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        var filename = Path.GetFileNameWithoutExtension(relative);

        foreach (var room in configuration.Rooms)
        {
            if (Terms(room).Any(term => PathContainsToken(folder, term)))
                return room.Name;
        }

        foreach (var room in configuration.Rooms)
        {
            if (Terms(room).Any(term => PathContainsToken(filename, term)))
                return room.Name;
        }

        var sample = content[..Math.Min(content.Length, 2000)];
        var scored = configuration.Rooms
            .Select((room, index) => new
            {
                Room = room,
                Index = index,
                Score = Terms(room).Sum(term => CountOccurrences(sample, term)),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .FirstOrDefault();
        if (scored is not null) return scored.Room.Name;

        return configuration.Rooms
            .FirstOrDefault(room => string.Equals(room.Name, "general", StringComparison.OrdinalIgnoreCase))
            ?.Name ?? "general";
    }

    private static IEnumerable<string> Terms(ProjectRoom room) =>
        room.Keywords
            .Prepend(room.Name)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool PathContainsToken(string value, string term)
    {
        if (term.Length == 0) return false;
        var index = 0;
        while ((index = value.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || IsBoundary(value[index - 1]);
            var end = index + term.Length;
            var after = end == value.Length || IsBoundary(value[end]);
            if (before && after) return true;
            index++;
        }
        return false;
    }

    private static bool IsBoundary(char value) => value is '-' or '_' or '.' or '/' or '\\';

    private static int CountOccurrences(string value, string term)
    {
        if (term.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }
}
