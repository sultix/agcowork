using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Api.Endpoints.Records;
using Agcowork.Application.Workspaces.Templates;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;
using Agcowork.Domain.Workspaces;
using ModelContextProtocol.Protocol;

namespace Agcowork.Api.Mcp;

/// <summary>
/// Closed sets in tool descriptions come from the registries, never from copied lists: attribute descriptions contain
/// <c>{{name}}</c> placeholders that the catalog fills here, in the tool description and in every parameter description.
/// </summary>
internal static class McpDescriptions
{
    public static IReadOnlyDictionary<string, string> Placeholders { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["propertyDefinition"] = PropertyDefinition(),
        ["viewTypes"] = string.Join(", ", ViewTypes.AllWireNames),
        ["activityTypes"] = string.Join(", ", ActivityTypes.All),
        ["roles"] = string.Join(", ", Enum.GetValues<WorkspaceRole>().Reverse().Select(r => r.ToString().ToLowerInvariant())),
        ["workspaceScopes"] = string.Join(", ", WorkspaceScope.All),
        ["templateIds"] = string.Join(", ", WorkspaceTemplates.Ids),
        ["defaultTemplate"] = WorkspaceTemplates.DefaultId,
        ["templates"] = string.Concat(WorkspaceTemplates.All.Select(t => $"'{t.Id}': {t.Description} ")).TrimEnd(),
        ["filterDsl"] = QueryEndpoints.DslDescription.TrimEnd(),
    };

    /// <summary>Replaces the placeholders in the tool's description and input schema; throws on an unknown one.</summary>
    public static void Apply(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        tool.Description = Fill(tool.Description);
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!;
        FillStrings(schema);
        tool.InputSchema = JsonSerializer.SerializeToElement(schema);
    }

    public static string? Fill(string? text)
    {
        if (text is null || !text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        foreach (var (name, value) in Placeholders)
        {
            text = text.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        }

        return text.Contains("{{", StringComparison.Ordinal)
            ? throw new InvalidOperationException($"Unknown placeholder in MCP description: {text}")
            : text;
    }

    private static void FillStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Fill(text);
                    }
                    else
                    {
                        FillStrings(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    FillStrings(child);
                }

                break;
        }
    }

    private static string PropertyDefinition()
    {
        var computed = PropertyTypes.All.Where(PropertyTypes.IsComputed).Select(PropertyTypes.ToWire);
        var writable = PropertyTypes.All.Where(t => !PropertyTypes.IsComputed(t)).Select(PropertyTypes.ToWire);
        return "Each property definition: 'key' (snake_case, ^[a-z][a-z0-9_]{0,62}$, immutable), 'name', 'type' (allowed: "
            + $"{string.Join(", ", writable)}; computed: {string.Join(", ", computed)}), optional 'description' (write it for agents), "
            + "'required', 'options' [ { 'value', 'label', 'color', 'group' } ] for select/multi_select/status (status options need "
            + $"'group', allowed: {string.Join(", ", PropertyOption.StatusGroups)}; colors allowed: {string.Join(", ", PropertyOption.Colors)}), "
            + "'targetCollectionId' and 'multiple' (relation, same workspace), 'default', 'unit' and 'precision' 0-10 (number).";
    }
}
