using Collabot.Collattice.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Collabot.Collattice.Api.Persistence;

// Per-entity EF model configuration for the card aggregate and its children (comments, attachments,
// label join). Applied by BoardDbContext via ApplyConfigurationsFromAssembly.

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class CardItemConfiguration : IEntityTypeConfiguration<CardItem>
{
    public void Configure(EntityTypeBuilder<CardItem> builder)
    {
        builder.HasIndex(x => new { x.BoardId, x.Number }).IsUnique().HasFilter("\"Number\" > 0");
        builder.HasIndex(x => new { x.LaneId, x.Position });
        builder.HasIndex(x => x.CreatedByUserId);
        builder.HasIndex(x => x.LastUpdatedByUserId);

        builder.Property(x => x.Name).HasMaxLength(120);

        // Every tracked update or delete of a card saves only if the card is still the draft or
        // non-draft it was when loaded. A finalize and a cancel each check "still a draft?" when they
        // load the card, so two of them for one draft at once could both pass; this makes the second
        // save fail instead of announcing the card twice, renumbering it, or deleting a card that was
        // just finalized. Only a finalize ever changes the flag, and only from draft to card, so a save
        // of any card loaded as a non-draft always passes.
        builder.Property(x => x.IsTemp).IsConcurrencyToken();

        builder.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.SortableUtc);
        builder.Property(x => x.LastUpdatedAtUtc).HasConversion(ValueConverters.SortableUtc);

        builder
            .HasOne<Lane>().WithMany()
            .HasForeignKey(x => x.LaneId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<CardSize>().WithMany()
            .HasForeignKey(x => x.SizeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne<BoardUser>().WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne<BoardUser>().WithMany()
            .HasForeignKey(x => x.LastUpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class CardFieldHistoryConfiguration : IEntityTypeConfiguration<CardFieldHistory>
{
    public void Configure(EntityTypeBuilder<CardFieldHistory> builder)
    {
        // Unique, not merely indexed: the revision ordinal is the trail's addressing scheme (the
        // from/to pair read and the consecutive-diff chain both index by it), so two rows sharing a
        // revision would silently corrupt the audit trail. Two edits racing the max+1 allocation
        // fail loudly here instead. Also serves the trail read, which is always ordered by revision
        // within one card and field.
        builder.HasIndex(x => new { x.CardId, x.Field, x.Revision }).IsUnique();

        // Field names are short lowercase identifiers ("description"); 40 leaves room for the
        // compound names a later field increment might want without inviting free-form text.
        builder.Property(x => x.Field).HasMaxLength(40);

        builder.Property(x => x.EditedAtUtc).HasConversion(ValueConverters.SortableUtc);

        builder
            .HasOne<CardItem>().WithMany()
            .HasForeignKey(x => x.CardId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict matches every other user reference in the model: users are deactivated, never
        // deleted, so history keeps a resolvable editor. Null (the oldest row of a trail, whose
        // author predates recording) is exempt from the constraint.
        builder
            .HasOne<BoardUser>().WithMany()
            .HasForeignKey(x => x.EditedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class CardCommentConfiguration : IEntityTypeConfiguration<CardComment>
{
    public void Configure(EntityTypeBuilder<CardComment> builder)
    {
        builder.HasIndex(x => new { x.CardId, x.LastUpdatedAtUtc });
        builder.HasIndex(x => x.UserId);

        builder.Property(x => x.CreatedAtUtc).HasConversion(ValueConverters.SortableUtc);
        builder.Property(x => x.LastUpdatedAtUtc).HasConversion(ValueConverters.SortableUtc);

        builder
            .HasOne<CardItem>().WithMany()
            .HasForeignKey(x => x.CardId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<BoardUser>().WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class CardAttachmentConfiguration : IEntityTypeConfiguration<CardAttachment>
{
    public void Configure(EntityTypeBuilder<CardAttachment> builder)
    {
        builder.HasIndex(x => x.CardId);
        builder.HasIndex(x => x.AddedByUserId);

        builder.Property(x => x.FileName).HasMaxLength(240);
        builder.Property(x => x.ContentType).HasMaxLength(100);

        builder.Property(x => x.AddedAtUtc).HasConversion(ValueConverters.SortableUtc);

        builder
            .HasOne<CardItem>().WithMany()
            .HasForeignKey(x => x.CardId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<BoardUser>().WithMany()
            .HasForeignKey(x => x.AddedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class CardLabelConfiguration : IEntityTypeConfiguration<CardLabel>
{
    public void Configure(EntityTypeBuilder<CardLabel> builder)
    {
        builder.HasKey(x => new { x.CardId, x.LabelId });

        builder
            .HasOne<CardItem>().WithMany()
            .HasForeignKey(x => x.CardId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne<Label>().WithMany()
            .HasForeignKey(x => x.LabelId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
