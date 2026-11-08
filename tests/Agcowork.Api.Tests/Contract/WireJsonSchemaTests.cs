using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Agcowork.Api.Http;
using Agcowork.Application.Records;
using Agcowork.Domain.Views;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Tests.Contract;

/// <summary>
/// The wire rule of <see cref="WireJson"/> agrees with the generated contract (<c>openapi/v1.json</c>, AP-033) for every component
/// schema, without a database: a member the schema lists as <c>required</c> is always written (also as null), an optional one is
/// omitted when null. A member with an explicit <see cref="JsonIgnoreAttribute"/> must not be required; that would make the schema
/// promise a member the wire leaves out.
/// </summary>
public sealed class WireJsonSchemaTests
{
    /// <summary>
    /// Shapes whose schema is maintained by hand (<see cref="ProblemDetailsSchemaTransformer"/>): the framework type carries
    /// <see cref="JsonIgnoreAttribute"/> on its members and the extensions (<c>code</c>, <c>errors</c>, …) are no .NET members.
    /// </summary>
    private static readonly Type[] HandMaintained = [typeof(ProblemDetails), typeof(ProblemFieldError)];

    private static readonly Assembly[] ContractAssemblies = [typeof(WireJson).Assembly, typeof(RecordDto).Assembly, typeof(ViewFilterNode).Assembly];

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        // As ConfigureHttpJsonOptions starts: web defaults, then the wire rule.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        WireJson.Configure(options);
        return options;
    }

    [Fact]
    public void ComponentSchemas_EveryMember_OptionalOnWireExactlyWhenNotRequired()
    {
        var schemas = LoadSchemas();
        var checkedMembers = 0;
        var mismatches = new List<string>();

        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue; // enums, JsonElement, JsonObject
            }

            var type = ResolveType(schema.Name, properties);
            var typeInfo = Options.GetTypeInfo(type);
            Assert.Equal(JsonTypeInfoKind.Object, typeInfo.Kind);
            var required = schema.Value.TryGetProperty("required", out var list)
                ? list.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];

            foreach (var property in typeInfo.Properties.Where(p => !p.IsExtensionData && !IsNeverWritten(p)))
            {
                var inSchema = properties.TryGetProperty(property.Name, out _);
                if (!inSchema)
                {
                    mismatches.Add($"{schema.Name}.{property.Name}: written on the wire but not in the schema");
                    continue;
                }

                checkedMembers++;
                var isRequired = required.Contains(property.Name);
                if (WireJson.IsOptional(property) == isRequired)
                {
                    mismatches.Add($"{schema.Name}.{property.Name}: schema required={isRequired}, wire optional={WireJson.IsOptional(property)}");
                }

                if (isRequired && HasJsonIgnore(property.AttributeProvider))
                {
                    mismatches.Add($"{schema.Name}.{property.Name}: required in the schema but [JsonIgnore] on the wire");
                }
            }

            if (!HandMaintained.Contains(type))
            {
                foreach (var name in properties.EnumerateObject().Select(p => p.Name))
                {
                    if (typeInfo.Properties.All(p => p.Name != name))
                    {
                        mismatches.Add($"{schema.Name}.{name}: in the schema but no member of {type.Name}");
                    }
                }
            }
        }

        Assert.Empty(mismatches);
        Assert.True(checkedMembers > 300, $"Only {checkedMembers} members checked; did the schema walk break?");
    }

    [Fact]
    public void ContractTypes_JsonIgnoreOnRequiredConstructorParameter_OnlyOnHandMaintainedShapes()
    {
        var offenders = new List<string>();
        foreach (var type in ContractAssemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsClass && !t.IsAbstract && !HandMaintained.Contains(t)))
        {
            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()).Where(p => !p.HasDefaultValue))
            {
                var property = type.GetProperty(parameter.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property?.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: not JsonIgnoreCondition.Always })
                {
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        // Such a member is required in a generated schema but missing on the wire when null: use a default (= null) instead.
        Assert.Empty(offenders);
    }

    /// <summary><c>[JsonIgnore]</c> without condition (e.g. <c>ViewFilterNode.IsEmpty</c>): part of the contract metadata, never on the wire.</summary>
    private static bool IsNeverWritten(JsonPropertyInfo property) =>
        property.Get is null
        || property.AttributeProvider is MemberInfo member && member.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always };

    private static bool HasJsonIgnore(ICustomAttributeProvider? provider) =>
        provider?.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) == true;

    /// <summary>The .NET type of a component schema: its name, or <c>XOfY</c> for <c>X&lt;Y&gt;</c>; ambiguous names by their members.</summary>
    private static Type ResolveType(string schemaName, JsonElement properties)
    {
        if (schemaName == nameof(ProblemDetails))
        {
            return typeof(ProblemDetails);
        }

        var candidates = CandidateTypes(schemaName).ToList();
        if (candidates.Count > 1)
        {
            var names = properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Where(t => Options.GetTypeInfo(t).Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names)).ToList();
        }

        return Assert.Single(candidates);
    }

    private static IEnumerable<Type> CandidateTypes(string schemaName)
    {
        var types = ContractAssemblies.SelectMany(a => a.GetTypes()).Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)));
        var of = schemaName.IndexOf("Of", StringComparison.Ordinal);
        if (of > 0 && char.IsUpper(schemaName[of + 2]))
        {
            var definitionName = schemaName[..of] + "`1";
            var argument = CandidateTypes(schemaName[(of + 2)..]).ToList();
            var generic = types.Where(t => t.IsGenericTypeDefinition && t.Name == definitionName).ToList();
            if (generic.Count == 1 && argument.Count == 1)
            {
                return [generic[0].MakeGenericType(argument[0])];
            }
        }

        return types.Where(t => !t.IsGenericTypeDefinition && t.Name == schemaName);
    }

    private static JsonElement LoadSchemas()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agcowork.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "src", "backend", "Agcowork.Api", "openapi", "v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("components").GetProperty("schemas").Clone();
    }
}
