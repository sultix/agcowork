using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Agcowork.Api.Http;

/// <summary>
/// JSON on the wire (REST, MCP structured content, SignalR payloads), one rule set for all adapters so they agree with the
/// OpenAPI contract (architecture §4.1, §4.3):
/// <list type="bullet">
/// <item>Enums travel as snake_case strings (<c>"human"</c>, <c>"agent"</c>).</item>
/// <item>
/// Members the contract lists as <c>required</c> (constructor parameters without a default) are always written, also when
/// null: <c>"readAt": null</c>, <c>"parentCommentId": null</c>, <c>"nextCursor": null</c>. Clients can rely on
/// <c>=== null</c>.
/// </item>
/// <item>
/// Optional members (constructor parameters with a default such as <c>replies</c>, <c>history</c>, <c>count</c>; settable or
/// init-only properties such as the filter DSL nodes) are omitted when null; the schema does not list them as required.
/// </item>
/// <item>
/// To make a member optional, give its constructor parameter a default (<c>IReadOnlyList&lt;X&gt;? Replies = null</c>): the
/// schema then omits it from <c>required</c> and the wire omits it when null. Do not use <see cref="JsonIgnoreAttribute"/>
/// for that: the schema would still list the member as <c>required</c> while the wire leaves it out. An explicit
/// <see cref="JsonIgnoreAttribute"/> still wins, but only for shapes whose schema is maintained by hand (ProblemDetails and
/// its <c>errors[].allowed</c>, see <see cref="ProblemDetailsSchemaTransformer"/>).
/// </item>
/// </list>
/// Persistence (<c>JsonColumns</c>) and activity diffs use their own options and are not affected.
/// </summary>
internal static class WireJson
{
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(OmitOptionalNulls);
    }

    /// <summary>True when the contract treats <paramref name="property"/> as optional (not <c>required</c> in OpenAPI).</summary>
    internal static bool IsOptional(JsonPropertyInfo property) =>
        !property.IsRequired
        && property.AssociatedParameter is not { HasDefaultValue: false, IsMemberInitializer: false };

    private static void OmitOptionalNulls(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is null
                || property.ShouldSerialize is not null
                || property.IsExtensionData
                || !CanBeNull(property.PropertyType)
                || property.AttributeProvider?.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) == true
                || !IsOptional(property))
            {
                continue;
            }

            property.ShouldSerialize = static (_, value) => value is not null;
        }
    }

    private static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
}
