using Agcowork.Domain.Activity;

namespace Agcowork.Domain.Tests.Activity;

public sealed class ActivityEventTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static ActivityEvent Create(string targetType, Guid targetId, Guid? relatedRecordId = null) =>
        ActivityEvent.Create(Workspace, "x.y", Actor, targetType, targetId, null, null, null, TimeProvider.System, relatedRecordId);

    [Fact]
    public void Create_RecordEvent_RelatedRecordIsTarget()
    {
        var record = Guid.CreateVersion7();

        Assert.Equal(record, Create(ActivityTargets.Record, record).RelatedRecordId);
        Assert.Equal(record, Create(ActivityTargets.Record, record, record).RelatedRecordId);
        Assert.Throws<ArgumentException>(() => Create(ActivityTargets.Record, record, Guid.CreateVersion7()));
    }

    [Fact]
    public void Create_CommentEvent_NeedsRecordOfComment()
    {
        var record = Guid.CreateVersion7();

        Assert.Equal(record, Create(ActivityTargets.Comment, Guid.CreateVersion7(), record).RelatedRecordId);
        Assert.Throws<ArgumentException>(() => Create(ActivityTargets.Comment, Guid.CreateVersion7()));
        Assert.Throws<ArgumentException>(() => Create(ActivityTargets.Comment, Guid.CreateVersion7(), Guid.Empty));
    }

    [Fact]
    public void Create_OtherTargets_NoRelatedRecord()
    {
        Assert.Null(Create(ActivityTargets.Space, Guid.CreateVersion7()).RelatedRecordId);
        Assert.Null(Create(ActivityTargets.Member, Guid.CreateVersion7()).RelatedRecordId);
        Assert.Throws<ArgumentException>(() => Create(ActivityTargets.Collection, Guid.CreateVersion7(), Guid.CreateVersion7()));
    }
}
