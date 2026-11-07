using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

internal sealed class ActorConfiguration : IEntityTypeConfiguration<Actor>
{
    public void Configure(EntityTypeBuilder<Actor> builder)
    {
        builder.ToTable("actors");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(Actor.NameMaxLength).IsRequired();
        builder.Property(a => a.Role).HasMaxLength(Actor.RoleMaxLength);
        builder.Property(a => a.Email).HasMaxLength(Actor.EmailMaxLength);
        // Emails are stored normalized (lower case), so a plain unique index is case-insensitive in effect.
        builder.HasIndex(a => a.Email).IsUnique().HasFilter("email IS NOT NULL");
        builder.PrimitiveCollection(a => a.InstanceScopes).IsRequired();
        builder.Property(a => a.IsActive).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
    }
}
