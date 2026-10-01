using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Collabot.Collattice.Api.Persistence;

// Per-entity EF model configuration for the board, its structural children (lanes, sizes, labels),
// and the user identity. Applied by BoardDbContext via ApplyConfigurationsFromAssembly.

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class BoardConfiguration : IEntityTypeConfiguration<Board>
{
    public void Configure(EntityTypeBuilder<Board> builder)
    {
        builder.HasIndex(x => x.Slug).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(80);
        builder.Property(x => x.Slug).HasMaxLength(80);

        builder.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.SortableUtc);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class BoardUserConfiguration : IEntityTypeConfiguration<BoardUser>
{
    public void Configure(EntityTypeBuilder<BoardUser> builder)
    {
        builder.HasIndex(x => x.AuthKey).IsUnique();

        builder.Property(x => x.AuthKey).HasMaxLength(26);   // ULID is 26 chars
        builder.Property(x => x.Name).HasMaxLength(80);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class LaneConfiguration : IEntityTypeConfiguration<Lane>
{
    public void Configure(EntityTypeBuilder<Lane> builder)
    {
        builder.HasIndex(x => new { x.BoardId, x.Position }).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(40);

        builder
            .HasOne<Board>().WithMany()
            .HasForeignKey(x => x.BoardId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class CardSizeConfiguration : IEntityTypeConfiguration<CardSize>
{
    public void Configure(EntityTypeBuilder<CardSize> builder)
    {
        builder.HasIndex(x => new { x.BoardId, x.Ordinal }).IsUnique();
        builder.HasIndex(x => new { x.BoardId, x.Name }).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(20);

        builder
            .HasOne<Board>().WithMany()
            .HasForeignKey(x => x.BoardId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class LabelConfiguration : IEntityTypeConfiguration<Label>
{
    public void Configure(EntityTypeBuilder<Label> builder)
    {
        builder.HasIndex(x => new { x.BoardId, x.Name }).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(30);
        builder.Property(x => x.Color).HasMaxLength(20);

        builder
            .HasOne<Board>().WithMany()
            .HasForeignKey(x => x.BoardId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
