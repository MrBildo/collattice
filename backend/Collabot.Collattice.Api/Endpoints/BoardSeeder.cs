namespace Collabot.Collattice.Api.Endpoints;

// Shared board-seed logic for the REST BoardEndpoints (POST /boards), the MCP
// BoardTools (create_board), AND the install-time first-run seed in Program.cs.
// Every board — however created — is seeded with one hidden archive lane
// (Position = int.MaxValue, IsArchiveLane = true), the four default card sizes
// (S/M/L/XL, ordinals 0-3), and the starter label set (Feature/Bug/Chore). One
// domain fact belongs in one place: REST/MCP/install seed drift is the top bug
// class on this codebase (see PruneFilter), so the shared scaffold lives here and
// the parity tests (BoardCreateParityTests) gate it.
//
// What is NOT here, by design: the three default visible lanes (Backlog / In
// Progress / Done) and the welcome sample card are install-only first-run
// onboarding, added by Program.cs on top of this shared scaffold. An API/MCP
// board-create yields a blank canvas the creator shapes (Program.cs is the only
// "brand-new human's first screen" target) — auto-littering every programmatic
// board with a sample card to delete is the opposite of onboarding. The starter
// labels ARE shared: they make any fresh board's first card labelable and surface
// the create-card Labels section, which is board-shape value, not first-run-only.
//
// Seed does NOT call SaveChanges — the caller owns the transaction (it also adds
// the Board itself, plus any install-only extras, and persists everything in one
// save).
internal static class BoardSeeder
{
    // Starter labels seeded on every new board. Three is the minimal
    // set that makes the first card creatable with a label and the create-card
    // Labels section appear; names + colors mirror the team's conventional-commit
    // label convention (the `collattice` skill). Single source of truth — both
    // BoardSeeder callers and the parity tests reference this.
    public static readonly (string Name, string Color)[] StarterLabels =
    [
        ("Feature", "#22c55e"),
        ("Bug", "#ef4444"),
        ("Chore", "#6b7280"),
    ];

    public static void Seed(BoardDbContext db, Board board)
    {
        db.Lanes.Add(new Lane
        {
            Id = Guid.NewGuid(),
            BoardId = board.Id,
            Name = "Archive",
            Position = int.MaxValue,
            IsArchiveLane = true,
        });

        db.CardSizes.AddRange
        (
            new CardSize { Id = Guid.NewGuid(), BoardId = board.Id, Name = "S", Ordinal = 0 },
            new CardSize { Id = Guid.NewGuid(), BoardId = board.Id, Name = "M", Ordinal = 1 },
            new CardSize { Id = Guid.NewGuid(), BoardId = board.Id, Name = "L", Ordinal = 2 },
            new CardSize { Id = Guid.NewGuid(), BoardId = board.Id, Name = "XL", Ordinal = 3 }
        );

        foreach (var (name, color) in StarterLabels)
        {
            db.Labels.Add(new Label
            {
                Id = Guid.NewGuid(),
                BoardId = board.Id,
                Name = name,
                Color = color,
            });
        }
    }
}
