using Agcowork.Application.Comments;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Records;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Comments;
using Agcowork.Domain.Identity;

namespace Agcowork.Application.Tests.Comments;

/// <summary>Comment use cases on the seeded workspaces: <see cref="RecordTestBase.ArrangeAsync"/> plus one record in Alpha.</summary>
public abstract class CommentTestBase(PostgresFixture postgres) : RecordTestBase(postgres)
{
    internal sealed record Discussion(Arranged A, RecordDto Record);

    internal async Task<Discussion> ArrangeDiscussionAsync()
    {
        var a = await ArrangeAsync();
        return new Discussion(a, await CreateRecordAsync(a, "Datenbank wählen"));
    }

    /// <summary>The tenant of <paramref name="actor"/> in Alpha (also used by the activity recorder).</summary>
    internal TenantContext As(Discussion d, Actor actor, params string[] keyScopes) => Enter(d.A.Seed.Alpha, actor, keyScopes);

    internal async Task<Result<CommentDto>> TryAddAsync(TenantContext tenant, AddCommentRequest request)
    {
        Db.ChangeTracker.Clear();
        Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await new AddComment(Db, TestActor.Agent(tenant.Membership.ActorId), tenant, Activity, Clock).HandleAsync(request, Ct);
        Db.ChangeTracker.Clear();
        return result;
    }

    internal async Task<CommentDto> AddAsync(Discussion d, string markdown, Guid? parent = null, Actor? actor = null)
    {
        var result = await TryAddAsync(As(d, actor ?? d.A.Seed.AlphaOwner), new AddCommentRequest(d.Record.Id, markdown, parent));
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    internal async Task<Result<CommentDto>> TryUpdateAsync(TenantContext tenant, UpdateCommentRequest request)
    {
        Db.ChangeTracker.Clear();
        Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await new UpdateComment(Db, TestActor.Agent(tenant.Membership.ActorId), tenant, Activity, Clock).HandleAsync(request, Ct);
        Db.ChangeTracker.Clear();
        return result;
    }

    internal async Task<Result> TryDeleteAsync(TenantContext tenant, Guid commentId)
    {
        Db.ChangeTracker.Clear();
        Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await new DeleteComment(Db, TestActor.Agent(tenant.Membership.ActorId), tenant, Activity, Clock).HandleAsync(new DeleteCommentRequest(commentId), Ct);
        Db.ChangeTracker.Clear();
        return result;
    }

    internal async Task<CommentThreadsDto> ListAsync(TenantContext tenant, Guid recordId, bool includeResolved = true)
    {
        var result = await new ListComments(Db, tenant).HandleAsync(new ListCommentsRequest(recordId, includeResolved), Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    internal async Task<Result<MentionPage>> TryMentionsAsync(TenantContext tenant, ListMentionsRequest request) =>
        await new ListMentions(Db, TestActor.Agent(tenant.Membership.ActorId), tenant).HandleAsync(request, Ct);

    internal async Task<MentionPage> MentionsAsync(TenantContext tenant, ListMentionsRequest? request = null)
    {
        var result = await TryMentionsAsync(tenant, request ?? new ListMentionsRequest());
        Assert.True(result.IsSuccess, Describe(result.Error));
        return result.Value;
    }

    internal async Task<Result<MarkMentionsReadResult>> TryMarkReadAsync(TenantContext tenant, MarkMentionsReadRequest request)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        return await new MarkMentionsRead(Db, TestActor.Agent(tenant.Membership.ActorId), tenant, Clock).HandleAsync(request, Ct);
    }

    /// <summary>A new agent, editor in Alpha.</summary>
    internal async Task<Actor> AddMemberAsync(Discussion d, string name)
    {
        var actor = Actor.CreateAgent(name, null, null, [], Clock);
        Db.Actors.Add(actor);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();
        var result = await new AddMember(Db, TestActor.Agent(d.A.Seed.AlphaOwner.Id), As(d, d.A.Seed.AlphaOwner), Clock, Activity).HandleAsync(new AddMemberRequest(actor.Id, "editor"), Ct);
        Assert.True(result.IsSuccess, Describe(result.Error));
        Db.ChangeTracker.Clear();
        return actor;
    }

    internal static string Mention(Actor actor) => CommentMarkdown.MentionLink(actor.Name, actor.Id);

    internal static FieldError FieldOf<T>(Result<T> result, string field) => Assert.Single(Invalid(result).Errors, e => e.Field == field);
}
