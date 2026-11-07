namespace Agcowork.Domain.Identity;

/// <summary>
/// Optional restriction an API key can carry on top of the actor's membership rights
/// (architecture §7). Stored in AP-004, evaluated from AP-005 on.
/// </summary>
public static class KeyScope
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Schema = "schema";
    public const string Admin = "admin";

    public static IReadOnlyList<string> All { get; } = [Read, Write, Schema, Admin];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);
}
