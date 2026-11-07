using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

/// <remarks>
/// Tenant table: indexes start with <c>workspace_id</c>. Deliberate exception: the foreign-key index on
/// <c>actor_id</c> (EF creates it by convention), which serves the instance-level lookup "my workspaces".
/// </remarks>
internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("memberships");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Actor>().WithMany().HasForeignKey(m => m.ActorId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => new { m.WorkspaceId, m.ActorId }).IsUnique();

        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.PrimitiveCollection(m => m.Scopes).IsRequired();
        builder.Property(m => m.CreatedBy).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Ignore(m => m.IsOwner);
    }
}
