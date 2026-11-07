using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agcowork.Domain.Views;

/// <summary>
/// One node of the filter DSL (architecture §3), shared by REST, MCP and storage: either a group
/// (<c>{ "and": [ … ] }</c> / <c>{ "or": [ … ] }</c>) or a condition (<c>{ "prop", "op", "value" }</c>).
/// Exactly one of the three shapes is set; <see cref="ViewFilterOperators"/> lists the operators.
/// The structure is checked by the application layer; translation to SQL is a separate concern.
/// </summary>
public sealed record ViewFilterNode
{
    /// <summary>All children must match.</summary>
    public IReadOnlyList<ViewFilterNode>? And { get; init; }

    /// <summary>At least one child must match.</summary>
    public IReadOnlyList<ViewFilterNode>? Or { get; init; }

    /// <summary>Condition: key of a schema property.</summary>
    public string? Prop { get; init; }

    /// <summary>Condition: eq, neq, in, nin, contains, starts_with, gt, gte, lt, lte, is_empty or is_not_empty.</summary>
    public string? Op { get; init; }

    /// <summary>Condition: comparison value; a list for in/nin, omitted for is_empty/is_not_empty.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>Members that are not part of the DSL; kept only so the validator can reject them (they are never stored).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }

    /// <summary>Keys of the DSL: the only members a node may have.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["and", "or", "prop", "op", "value"];

    /// <summary>True when no member is set, i.e. "no filter" (<c>{}</c>).</summary>
    [JsonIgnore]
    public bool IsEmpty => And is null && Or is null && Prop is null && Op is null && Value is null && Unknown is not { Count: > 0 };
}

/// <summary>Operators of filter conditions.</summary>
public static class ViewFilterOperators
{
    public const string Eq = "eq";
    public const string Neq = "neq";
    public const string In = "in";
    public const string Nin = "nin";
    public const string Contains = "contains";
    public const string StartsWith = "starts_with";
    public const string Gt = "gt";
    public const string Gte = "gte";
    public const string Lt = "lt";
    public const string Lte = "lte";
    public const string IsEmpty = "is_empty";
    public const string IsNotEmpty = "is_not_empty";

    public static IReadOnlyList<string> All { get; } =
        [Eq, Neq, In, Nin, Contains, StartsWith, Gt, Gte, Lt, Lte, IsEmpty, IsNotEmpty];

    /// <summary>Operators that take a list value.</summary>
    public static bool TakesList(string op) => op is In or Nin;

    /// <summary>Operators that take no value.</summary>
    public static bool TakesNoValue(string op) => op is IsEmpty or IsNotEmpty;
}

/// <summary>One sort criterion of a view.</summary>
/// <param name="Prop">Key of a schema property.</param>
/// <param name="Dir"><c>asc</c> or <c>desc</c>.</param>
public sealed record ViewSort(string Prop, string Dir)
{
    public const string Asc = "asc";
    public const string Desc = "desc";

    public static IReadOnlyList<string> Directions { get; } = [Asc, Desc];
}
