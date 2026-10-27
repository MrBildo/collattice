using Microsoft.EntityFrameworkCore;

namespace Collabot.Collattice.Api;

public class BoardDbContext(DbContextOptions<BoardDbContext> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardUser> Users => Set<BoardUser>();
    public DbSet<Lane> Lanes => Set<Lane>();
    public DbSet<CardSize> CardSizes => Set<CardSize>();
    public DbSet<CardItem> Cards => Set<CardItem>();
    public DbSet<CardFieldHistory> CardFieldHistories => Set<CardFieldHistory>();
    public DbSet<CardComment> Comments => Set<CardComment>();
    public DbSet<CardAttachment> Attachments => Set<CardAttachment>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<CardLabel> CardLabels => Set<CardLabel>();
    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    // Per-entity fluent configuration lives in IEntityTypeConfiguration<T> implementations under
    // Persistence/ (grouped by aggregate area). They are discovered and applied by assembly scan, so
    // adding a new entity's configuration there needs no change here.
    // Max-length constraints are configured in the typed config classes — not via [MaxLength]
    // attributes on the entity classes — single source of truth for all EF model configuration.
    protected override void OnModelCreating(ModelBuilder builder) =>
        builder.ApplyConfigurationsFromAssembly(typeof(BoardDbContext).Assembly);
}
