using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Collabot.Collattice.Api.Persistence;

// Per-entity EF model configuration for the webhooks-v2 registry: subscriptions and the
// delivery-attempt audit log. Applied by BoardDbContext via ApplyConfigurationsFromAssembly.

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    // The webhook subscription event-selection is a small List<string> stored as a JSON TEXT
    // column (no child table; the set is tiny and read whole per drain). A value comparer is
    // configured below so EF detects changes by value; the store also assigns a fresh list on update
    // (replace-only), so edits persist correctly either way. NOTE: a value-converted column defeats
    // SQL translation of relational predicates — the dispatcher loads enabled rows and matches the
    // selection in CLR memory, never Where(s => s.EventTypes.Contains(...)).
    private static readonly ValueConverter<IList<string>, string> _eventTypesConverter = new
    (
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()
    );

    // A value-converted mutable collection needs a comparer so EF compares by VALUE (not by
    // reference) for change detection — without it EF logs a model-validation warning and would
    // miss an in-place edit. The store treats the selection as replace-only anyway, but the comparer
    // makes the model correct regardless and silences the warning.
    private static readonly ValueComparer<IList<string>> _eventTypesComparer = new
    (
        (left, right) => (left == null && right == null)
            || (left != null && right != null && left.SequenceEqual(right, StringComparer.Ordinal)),
        value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
        value => value.ToList()
    );

    public void Configure(EntityTypeBuilder<WebhookSubscription> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Url).HasMaxLength(2048);   // conventional maximum-URL-length cap

        // The subscription event-selection persists as a JSON TEXT column via the converter,
        // compared by value for change detection.
        builder
            .Property(x => x.EventTypes)
            .HasConversion(_eventTypesConverter, _eventTypesComparer);
    }
}

// sealed: a leaf configuration type; no subtype hierarchy is intended.
internal sealed class WebhookDeliveryAttemptConfiguration : IEntityTypeConfiguration<WebhookDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryAttempt> builder)
    {
        // Webhook delivery-attempt log indexes: the "deliveries for this board,
        // newest first" read, and "all attempts for one event".
        builder.HasIndex(x => new { x.BoardId, x.AttemptedAtUtc });
        builder.HasIndex(x => x.EventId);

        // Serves the per-subscription "deliveries newest first" read and the on-read metrics
        // aggregation (success/failure counts + last-delivery per subscription).
        builder.HasIndex(x => new { x.SubscriptionId, x.AttemptedAtUtc });

        builder.Property(x => x.EventId).HasMaxLength(60);
        builder.Property(x => x.EventType).HasMaxLength(40);
        builder.Property(x => x.Error).HasMaxLength(500);

        builder.Property(x => x.AttemptedAtUtc).HasConversion(ValueConverters.SortableUtc);

        // The delivery-attempt log's nullable SubscriptionId FK. SetNull (not Cascade):
        // deleting a subscription must NOT delete its delivery history — the audit log outlives the
        // subscription (an admin removing a flaky webhook still wants to see why it failed). A
        // deliberate divergence from the board-scoped Cascade relationships.
        builder
            .HasOne<WebhookSubscription>().WithMany()
            .HasForeignKey(x => x.SubscriptionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
