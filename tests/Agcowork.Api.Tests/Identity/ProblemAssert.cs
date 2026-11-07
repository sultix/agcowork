using System.Net;
using System.Text.Json;

namespace Agcowork.Api.Tests.Identity;

/// <summary>Assertions on the ProblemDetails wire format (see ProblemDetailsMapping in the Api).</summary>
internal static class ProblemAssert
{
    public static async Task<JsonElement> IsProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var root = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("title").GetString()));
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        return root;
    }

    public static JsonElement Field(JsonElement problem, string field) =>
        problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == field);

    public static HashSet<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
}
