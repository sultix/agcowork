namespace Agcowork.Domain.Collections.Schema;

/// <summary>Group of a <see cref="PropertyType.Status"/> option. Boards and filters use it ("everything not done").</summary>
public enum StatusGroup
{
    Todo = 1,
    InProgress = 2,
    Done = 3,
}

/// <summary>One choice of a select, multi_select or status property.</summary>
/// <param name="Value">Stored value, compared case-sensitively. Unique within the property, immutable for records that use it.</param>
/// <param name="Label">Display text.</param>
/// <param name="Color">One of <see cref="Colors"/>, or null for the default.</param>
/// <param name="Group">Required for status options, not allowed otherwise.</param>
public sealed record PropertyOption(string Value, string Label, string? Color = null, StatusGroup? Group = null)
{
    public const int ValueMaxLength = 100;
    public const int LabelMaxLength = 200;

    /// <summary>Palette the UI can render.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["gray", "brown", "orange", "yellow", "green", "blue", "purple", "pink", "red"];

    /// <summary>Wire names of <see cref="StatusGroup"/>.</summary>
    public static IReadOnlyList<string> StatusGroups { get; } = ["todo", "in_progress", "done"];

    public static string ToWire(StatusGroup group) => group switch
    {
        StatusGroup.Todo => "todo",
        StatusGroup.InProgress => "in_progress",
        StatusGroup.Done => "done",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown status group."),
    };

    public static bool TryParseGroup(string? wire, out StatusGroup group)
    {
        StatusGroup? parsed = wire switch
        {
            "todo" => StatusGroup.Todo,
            "in_progress" => StatusGroup.InProgress,
            "done" => StatusGroup.Done,
            _ => null,
        };
        group = parsed ?? default;
        return parsed is not null;
    }
}
