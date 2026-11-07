using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Application.Activity;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Workspaces;

/// <summary>Diffs of the member.* activity events: role and scopes, old and new (null when absent).</summary>
internal static class MemberActivity
{
    public static (string Role, IReadOnlyList<string> Scopes) State(Membership membership) =>
        (JsonNamingPolicy.SnakeCaseLower.ConvertName(membership.Role.ToString()), [.. membership.Scopes]);

    public static JsonObject Added(Membership membership) => Diff(null, State(membership));

    public static JsonObject Changed((string Role, IReadOnlyList<string> Scopes) before, Membership after) => Diff(before, State(after));

    public static JsonObject Removed(Membership membership) => Diff(State(membership), null);

    private static JsonObject Diff((string Role, IReadOnlyList<string> Scopes)? before, (string Role, IReadOnlyList<string> Scopes)? after) =>
        new ActivityDiff()
            .Change("role", before?.Role, after?.Role)
            .Change("scopes", before is { } b ? ActivityDiff.Strings(b.Scopes) : null, after is { } a ? ActivityDiff.Strings(a.Scopes) : null)
            .ToJson();
}
