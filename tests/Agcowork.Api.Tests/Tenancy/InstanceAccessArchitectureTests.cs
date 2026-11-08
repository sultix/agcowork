using System.Reflection;
using System.Text.RegularExpressions;
using Agcowork.Application.Realtime;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces;
using Agcowork.Infrastructure.Persistence;

namespace Agcowork.Api.Tests.Tenancy;

/// <summary>
/// The instance area (<see cref="InstanceAccess"/>, AP-036) lifts row-level security; who may use it is a fixed list
/// (architecture §2a, step 5). Adding a user means changing this test, which a review sees. The EF bypass
/// (<c>IgnoreQueryFilters</c>) and the instance area go together: one never without the other.
/// </summary>
public sealed partial class InstanceAccessArchitectureTests
{
    /// <summary>The only classes that enter the instance area, with the reason.</summary>
    private static readonly Dictionary<Type, string> Users = new()
    {
        [typeof(ResolveTenant)] = "the caller's own membership, before the tenant exists",
        [typeof(ListMyWorkspaces)] = "the caller's own memberships, outside any tenant",
        [typeof(InstanceActivityReader)] = "realtime dispatcher reads the activity log of all workspaces",
    };

    /// <summary>Source files that may mention the type in code: its definition, its DI registration, its reader, its users.</summary>
    private static readonly string[] AllowedFiles =
    [
        "Agcowork.Application/Tenancy/InstanceAccess.cs",
        "Agcowork.Application/DependencyInjection.cs",
        "Agcowork.Infrastructure/Persistence/AgcoworkDbContext.cs",
        "Agcowork.Application/Tenancy/ResolveTenant.cs",
        "Agcowork.Application/Workspaces/ListMyWorkspaces.cs",
        "Agcowork.Application/Realtime/InstanceActivityReader.cs",
    ];

    [Fact]
    public void Constructors_OnlyTheListedUsersAndTheDbContextTakeInstanceAccess()
    {
        Assembly[] assemblies = [typeof(ResolveTenant).Assembly, typeof(AgcoworkDbContext).Assembly, typeof(Program).Assembly];

        var takers = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.DeclaringType != typeof(InstanceAccess))
            .Where(t => t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(InstanceAccess))))
            .ToHashSet();

        Assert.Equal(Users.Keys.Append(typeof(AgcoworkDbContext)).OrderBy(t => t.FullName), takers.OrderBy(t => t.FullName));
    }

    [Fact]
    public void Sources_InstanceAccessAppearsOnlyInTheListedFiles()
    {
        var mentions = SourceFiles()
            .Where(f => CodeLines(f.Text).Any(line => InstanceAccessToken().IsMatch(line)))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(AllowedFiles.Order(StringComparer.Ordinal), mentions);
    }

    [Fact]
    public void Sources_IgnoreQueryFiltersExactlyWhereTheInstanceAreaIsEntered()
    {
        var files = SourceFiles().ToList();
        var bypassing = files.Where(f => CodeLines(f.Text).Any(line => line.Contains(".IgnoreQueryFilters(", StringComparison.Ordinal))).Select(f => f.Path);
        var entering = files.Where(f => CodeLines(f.Text).Any(line => EnterCall().IsMatch(line))).Select(f => f.Path);
        string[] expected =
        [
            "Agcowork.Application/Realtime/InstanceActivityReader.cs",
            "Agcowork.Application/Tenancy/ResolveTenant.cs",
            "Agcowork.Application/Workspaces/ListMyWorkspaces.cs",
        ];

        Assert.Equal(expected, bypassing.Order(StringComparer.Ordinal));
        Assert.Equal(expected, entering.Order(StringComparer.Ordinal));
        foreach (var (type, reason) in Users)
        {
            var file = Assert.Single(files, f => f.Path.EndsWith($"/{type.Name}.cs", StringComparison.Ordinal));
            Assert.Contains($"\"{reason}\"", file.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Only the two row-level security classes may switch roles or name the scope setting: anything else could leave the
    /// application role or enter the instance area past <see cref="InstanceAccess"/>.
    /// </summary>
    [Fact]
    public void Sources_RoleSwitchesAndScopeSettingOnlyInTheRowLevelSecurityClasses()
    {
        string[] allowed =
        [
            "Agcowork.Infrastructure/Persistence/DatabaseSessionInterceptor.cs",
            "Agcowork.Infrastructure/Persistence/RowLevelSecurity.cs",
        ];

        var offenders = SourceFiles()
            .Where(f => !allowed.Contains(f.Path))
            .SelectMany(f => CodeLines(f.Text).Where(line => SessionSwitch().IsMatch(line)).Select(line => $"{f.Path}: {line}"))
            .ToList();

        Assert.Empty(offenders);
        Assert.Contains(SourceFiles(), f => f.Path == allowed[1] && SessionSwitch().IsMatch(f.Text));
    }

    [Theory]
    [InlineData("SELECT set_config('role', 'postgres', false)")]
    [InlineData("reset  role")]
    [InlineData("SET ROLE postgres")]
    [InlineData("SET SESSION AUTHORIZATION DEFAULT")]
    [InlineData("current_setting('agcowork.scope')")]
    [InlineData("instanceAccess?.Enter(\"x\")")]
    public void Patterns_RecognizeForbiddenSql(string line) =>
        Assert.True(SessionSwitch().IsMatch(line) || EnterCall().IsMatch(line), line);

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "agcowork.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var backend = Path.Combine(root.FullName, "src", "backend");
        return Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(backend, path).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj"))
            .Select(path => (path, File.ReadAllText(Path.Combine(backend, path))));
    }

    /// <summary>Lines without comments (doc comments mention the type by name).</summary>
    private static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => !line.StartsWith("//", StringComparison.Ordinal));

    [GeneratedRegex(@"\bInstanceAccess\b")]
    private static partial Regex InstanceAccessToken();

    // instanceAccess.Enter("…") or instanceAccess?.Enter("…"), the only way into the area.
    [GeneratedRegex(@"\b[iI]nstanceAccess\??\.Enter\(")]
    private static partial Regex EnterCall();

    // Role switches and the scope setting, in any letter case and spacing.
    [GeneratedRegex(@"agcowork\.scope|\bRESET\s+ROLE\b|\bSET\s+ROLE\b|\bSET\s+SESSION\s+AUTHORIZATION\b|set_config\s*\(\s*'role'", RegexOptions.IgnoreCase)]
    private static partial Regex SessionSwitch();
}
