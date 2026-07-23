using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bogmem.Slices.Mining;

public sealed record ProjectRoom(
    string Name,
    string? Description,
    IReadOnlyList<string> Keywords);

public sealed record ProjectConfig(
    string Wing,
    IReadOnlyList<ProjectRoom> Rooms,
    string? ConfigurationPath)
{
    private static readonly string[] ConfigurationFileNames =
    [
        "mempalace.yaml",
        "mempalace.yml",
        "mempal.yaml",
        "mempal.yml",
    ];

    public static ProjectConfig Load(string sourceDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var configurationPath = ConfigurationFileNames
            .Select(name => Path.Combine(source, name))
            .FirstOrDefault(File.Exists);
        if (configurationPath is null)
            return new ProjectConfig(DefaultWing(source), [GeneralRoom()], null);

        ProjectConfigDocument document;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            document = deserializer.Deserialize<ProjectConfigDocument>(
                File.ReadAllText(configurationPath)) ?? new ProjectConfigDocument();
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Invalid project configuration '{configurationPath}': {ex.Message}", ex);
        }

        var wing = string.IsNullOrWhiteSpace(document.Wing)
            ? DefaultWing(source)
            : ValidName(document.Wing, "wing", configurationPath);
        var rooms = (document.Rooms ?? [])
            .Select((room, index) => ToRoom(room, index, configurationPath))
            .ToList();
        var duplicate = rooms
            .GroupBy(room => room.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException(
                $"Invalid project configuration '{configurationPath}': duplicate room '{duplicate.Key}'.");
        if (rooms.All(room => !string.Equals(room.Name, "general", StringComparison.OrdinalIgnoreCase)))
            rooms.Add(GeneralRoom());

        return new ProjectConfig(wing, rooms, configurationPath);
    }

    private static ProjectRoom ToRoom(ProjectRoomDocument? room, int index, string configurationPath)
    {
        if (room is null)
            throw new InvalidDataException(
                $"Invalid project configuration '{configurationPath}': room {index + 1} is empty.");
        var name = ValidName(room.Name, $"rooms[{index}].name", configurationPath);
        var keywords = (room.Keywords ?? [])
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProjectRoom(name, room.Description?.Trim(), keywords);
    }

    private static string ValidName(string? value, string field, string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(
                $"Invalid project configuration '{configurationPath}': {field} is required.");
        var name = value.Trim();
        if (name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidDataException(
                $"Invalid project configuration '{configurationPath}': {field} cannot contain path traversal.");
        return name;
    }

    private static string DefaultWing(string source)
    {
        var name = new DirectoryInfo(source).Name;
        var normalized = name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_').Trim('_');
        return normalized.Length == 0 ? "project" : normalized;
    }

    private static ProjectRoom GeneralRoom() => new("general", "Unclassified project memory", []);

    private sealed class ProjectConfigDocument
    {
        public string? Wing { get; set; }
        public List<ProjectRoomDocument?>? Rooms { get; set; }
    }

    private sealed class ProjectRoomDocument
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Keywords { get; set; }
    }
}
