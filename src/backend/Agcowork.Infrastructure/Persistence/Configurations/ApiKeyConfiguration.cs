using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.HasOne<Actor>().WithMany().HasForeignKey(k => k.ActorId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(k => k.Prefix).HasMaxLength(ApiKeyToken.PrefixLength).IsFixedLength().IsRequired();
        builder.HasIndex(k => k.Prefix).IsUnique();
        builder.Property(k => k.SecretHash).HasMaxLength(ApiKey.SecretHashLength).IsRequired();
        builder.PrimitiveCollection(k => k.Scopes).IsRequired();
        // Optional restriction to one workspace, evaluated by ResolveTenant. Never SetNull: that would widen the key.
        builder.HasOne<Workspace>().WithMany().HasForeignKey(k => k.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(k => k.ExpiresAt);
        builder.Property(k => k.LastUsedAt);
        builder.Property(k => k.RevokedAt);
        builder.Property(k => k.CreatedAt).IsRequired();
    }
}
