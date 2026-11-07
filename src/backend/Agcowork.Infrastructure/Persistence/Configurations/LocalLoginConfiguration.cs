using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agcowork.Infrastructure.Persistence.Configurations;

internal sealed class LocalLoginConfiguration : IEntityTypeConfiguration<LocalLogin>
{
    public void Configure(EntityTypeBuilder<LocalLogin> builder)
    {
        builder.ToTable("local_logins");
        builder.HasKey(l => l.ActorId);
        builder.Property(l => l.ActorId).ValueGeneratedNever();
        builder.HasOne<Actor>().WithOne().HasForeignKey<LocalLogin>(l => l.ActorId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(l => l.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();
    }
}
