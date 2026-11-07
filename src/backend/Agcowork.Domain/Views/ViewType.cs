namespace Agcowork.Domain.Views;

/// <summary>
/// How a view presents its collection (architecture §3). Wire and storage names are lowercase (<c>table</c>, <c>board</c>).
/// <c>timeline</c> and <c>calendar</c> arrive in phase 3 and are rejected until then.
/// </summary>
public enum ViewType
{
    Table = 1,
    Board = 2,
    Gallery = 3,
    List = 4,
    Document = 5,
    Raw = 6,
}

/// <summary>Facts about <see cref="ViewType"/>s.</summary>
public static class ViewTypes
{
    /// <summary>Wire names of all allowed types, in declaration order.</summary>
    public static IReadOnlyList<string> AllWireNames { get; } = [.. Enum.GetValues<ViewType>().Select(ToWire)];

    public static string ToWire(ViewType type) => type.ToString().ToLowerInvariant();

    /// <summary>Parses a wire name (exact, lowercase); false for unknown names such as <c>timeline</c>.</summary>
    public static bool TryParse(string? wire, out ViewType type)
    {
        foreach (var candidate in Enum.GetValues<ViewType>())
        {
            if (string.Equals(ToWire(candidate), wire, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }
}
