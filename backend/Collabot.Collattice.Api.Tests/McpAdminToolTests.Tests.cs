using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// The 11 new admin-level MCP tools (lane/label/size CRUD +
// board create/update). Each tool gates via McpAuthService.RequireAdminLevelAsync,
// which admits Administrator and AgentAdministrator but rejects HumanUser and
// AgentUser. This file exercises:
//   - the role-gate matrix per tool (Administrator ok, AgentAdministrator ok,
//     HumanUser/AgentUser rejected),
//   - the behavioral guards each tool mirrors from its REST analog
//     (reserved positions, archive-lane protection, non-empty-lane / in-use-size
//      / duplicate-name conflicts, board seeding).
public class McpAdminToolTests(CollatticeApiFactory factory) : IClassFixture<CollatticeApiFactory>, IDisposable
{
    private readonly CollatticeApiFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];
    private const string _adminPrivilegeError = "Error: This operation requires administrator privileges.";

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private (BoardDbContext Db, LaneTools Lane, LabelTools Label, SizeTools Size, BoardTools Board) CreateTools()
    {
        var scope = _factory.Services.CreateScope();
        _scopes.Add(scope);
        var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();
        var auth = new McpAuthService(new UserResolver(db));
        var broadcaster = scope.ServiceProvider.GetRequiredService<BoardEventBroadcaster>();
        return (
            db,
            new LaneTools(db, auth, broadcaster),
            new LabelTools(db, auth, broadcaster),
            new SizeTools(db, auth, broadcaster),
            new BoardTools(db, auth, scope.ServiceProvider.GetRequiredService<IWebhookSink>()));
    }

    private async Task<string> AuthKeyForAsync(UserRole role)
    {
        if (role == UserRole.Administrator)
        {
            return _factory.AdminAuthKey;
        }

        using var setupClient = _factory.CreateClient();
        var user = await TestAuthHelper.CreateUserAsync
        (
            setupClient,
            _factory,
            $"admintool-{role}-{Guid.NewGuid():N}",
            role
        );
        return user.AuthKey;
    }

    private static int _nextLanePosition = 10_000;
    private static int NextLanePosition() => Interlocked.Increment(ref _nextLanePosition);

    // Card numbers are unique per board. A monotonic counter guarantees distinct values; a random
    // pick collided on the (BoardId, Number) index (birthday paradox), which read as an intermittent
    // CI failure on unchanged code.
    private static int _nextCardNumber = 500_000;
    private static int NextCardNumber() => Interlocked.Increment(ref _nextCardNumber);

    private async Task<CardItem> AddCardAsync(BoardDbContext db, Guid laneId, Guid sizeId)
    {
        var adminId = await db.Users.Where(u => u.Role == UserRole.Administrator).Select(u => u.Id).FirstAsync();
        var card = new CardItem
        {
            Id = Guid.NewGuid(),
            BoardId = _factory.DefaultBoardId,
            LaneId = laneId,
            SizeId = sizeId,
            Name = "Fixture Card",
            Number = NextCardNumber(),
            Position = 0,
            CreatedByUserId = adminId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LastUpdatedByUserId = adminId,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Cards.Add(card);
        return card;
    }

    // create_lane

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task CreateLane_AdminLevel_Succeeds(UserRole role)
    {
        var (db, lane, _, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var position = NextLanePosition();

        var result = await lane.CreateLaneAsync(authKey, _factory.DefaultBoardId, $"Lane-{role}-{position.ToString(CultureInfo.InvariantCulture)}", position);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var laneId = json.GetProperty("id").GetGuid();
        (await db.Lanes.FindAsync(laneId)).ShouldNotBeNull();
        json.GetProperty("position").GetInt32().ShouldBe(position);
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task CreateLane_NonAdmin_ReturnsError(UserRole role)
    {
        var (db, lane, _, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var before = await db.Lanes.CountAsync();

        var result = await lane.CreateLaneAsync(authKey, _factory.DefaultBoardId, "Should-Not-Exist", NextLanePosition());

        result.ShouldBe(_adminPrivilegeError);
        (await db.Lanes.CountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task CreateLane_ReservedPosition_ReturnsError()
    {
        var (_, lane, _, _, _) = CreateTools();

        var result = await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, "Reserved", int.MaxValue);

        result.ShouldBe("Error: Position value is reserved.");
    }

    [Fact]
    public async Task CreateLane_UnknownBoard_ReturnsError()
    {
        var (_, lane, _, _, _) = CreateTools();

        var result = await lane.CreateLaneAsync(_factory.AdminAuthKey, Guid.NewGuid(), "Orphan", NextLanePosition());

        result.ShouldBe("Error: Board not found.");
    }

    // update_lane

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task UpdateLane_AdminLevel_RenamesLane(UserRole role)
    {
        var (db, lane, _, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"Before-{role}", NextLanePosition()));
        var laneId = created.GetProperty("id").GetGuid();

        var result = await lane.UpdateLaneAsync(authKey, laneId, name: "After");

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("name").GetString().ShouldBe("After");
        db.ChangeTracker.Clear();
        (await db.Lanes.FindAsync(laneId))!.Name.ShouldBe("After");
    }

    [Fact]
    public async Task UpdateLane_PositionCollision_ReturnsError()
    {
        var (_, lane, _, _, _) = CreateTools();
        var posA = NextLanePosition();
        var posB = NextLanePosition();
        var laneA = JsonSerializer.Deserialize<JsonElement>(
            await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, "CollideA", posA));
        await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, "CollideB", posB);

        var result = await lane.UpdateLaneAsync(_factory.AdminAuthKey, laneA.GetProperty("id").GetGuid(), position: posB);

        result.ShouldBe("Error: Position already taken by another lane.");
    }

    [Fact]
    public async Task UpdateLane_ArchiveLane_ReturnsError()
    {
        var (db, lane, _, _, _) = CreateTools();
        var archiveLaneId = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .FirstAsync();

        var result = await lane.UpdateLaneAsync(_factory.AdminAuthKey, archiveLaneId, name: "Hacked");

        result.ShouldBe("Error: Archive lanes cannot be modified.");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task UpdateLane_NonAdmin_ReturnsError(UserRole role)
    {
        var (_, lane, _, _, _) = CreateTools();
        var created = JsonSerializer.Deserialize<JsonElement>(
            await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, "Guarded", NextLanePosition()));
        var authKey = await AuthKeyForAsync(role);

        var result = await lane.UpdateLaneAsync(authKey, created.GetProperty("id").GetGuid(), name: "Nope");

        result.ShouldBe(_adminPrivilegeError);
    }

    // delete_lane

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task DeleteLane_AdminLevel_EmptyLane_Succeeds(UserRole role)
    {
        var (db, lane, _, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"ToDelete-{role}", NextLanePosition()));
        var laneId = created.GetProperty("id").GetGuid();

        var result = await lane.DeleteLaneAsync(authKey, laneId);

        result.ShouldBe("Lane deleted.");
        db.ChangeTracker.Clear();
        (await db.Lanes.FindAsync(laneId)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteLane_NonEmpty_ReturnsError()
    {
        var (db, lane, _, _, _) = CreateTools();
        var created = JsonSerializer.Deserialize<JsonElement>(
            await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, "Occupied", NextLanePosition()));
        var laneId = created.GetProperty("id").GetGuid();
        var defaultSize = await db.CardSizes.Where(s => s.BoardId == _factory.DefaultBoardId).OrderBy(s => s.Ordinal).FirstAsync();
        await AddCardAsync(db, laneId, defaultSize.Id);
        await db.SaveChangesAsync();

        var result = await lane.DeleteLaneAsync(_factory.AdminAuthKey, laneId);

        result.ShouldBe("Error: Lane must be empty.");
    }

    [Fact]
    public async Task DeleteLane_ArchiveLane_ReturnsError()
    {
        var (db, lane, _, _, _) = CreateTools();
        var archiveLaneId = await db.Lanes
            .Where(l => l.BoardId == _factory.DefaultBoardId && l.IsArchiveLane)
                .Select(l => l.Id)
                    .FirstAsync();

        var result = await lane.DeleteLaneAsync(_factory.AdminAuthKey, archiveLaneId);

        result.ShouldBe("Error: Archive lanes cannot be deleted.");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteLane_NonAdmin_ReturnsError(UserRole role)
    {
        var (db, lane, _, _, _) = CreateTools();
        var created = JsonSerializer.Deserialize<JsonElement>(
            await lane.CreateLaneAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, "Protected", NextLanePosition()));
        var laneId = created.GetProperty("id").GetGuid();
        var authKey = await AuthKeyForAsync(role);

        var result = await lane.DeleteLaneAsync(authKey, laneId);

        result.ShouldBe(_adminPrivilegeError);
        db.ChangeTracker.Clear();
        (await db.Lanes.FindAsync(laneId)).ShouldNotBeNull();
    }

    // create_label / update_label / delete_label

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task CreateLabel_AdminLevel_Succeeds(UserRole role)
    {
        var (db, _, label, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var name = $"Label-{role}-{Guid.NewGuid():N}";

        var result = await label.CreateLabelAsync(authKey, _factory.DefaultBoardId, name, "#ff0000");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("name").GetString().ShouldBe(name);
        json.GetProperty("color").GetString().ShouldBe("#ff0000");
        (await db.Labels.FindAsync(json.GetProperty("id").GetGuid())).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task CreateLabel_NonAdmin_ReturnsError(UserRole role)
    {
        var (_, _, label, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);

        var result = await label.CreateLabelAsync(authKey, _factory.DefaultBoardId, $"Nope-{Guid.NewGuid():N}", null);

        result.ShouldBe(_adminPrivilegeError);
    }

    [Fact]
    public async Task CreateLabel_DuplicateName_ReturnsError()
    {
        var (_, _, label, _, _) = CreateTools();
        var name = $"Dup-{Guid.NewGuid():N}";
        await label.CreateLabelAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, name, null);

        var result = await label.CreateLabelAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, name, null);

        result.ShouldBe("Error: A label with that name already exists on this board.");
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task UpdateLabel_AdminLevel_ChangesNameAndColor(UserRole role)
    {
        var (db, _, label, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await label.CreateLabelAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"Orig-{role}-{Guid.NewGuid():N}", "#000000"));
        var labelId = created.GetProperty("id").GetGuid();
        var newName = $"Updated-{role}-{Guid.NewGuid():N}";

        var result = await label.UpdateLabelAsync(authKey, labelId, newName, "#ffffff");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("name").GetString().ShouldBe(newName);
        json.GetProperty("color").GetString().ShouldBe("#ffffff");
        db.ChangeTracker.Clear();
        var reloaded = await db.Labels.FindAsync(labelId);
        reloaded!.Name.ShouldBe(newName);
        reloaded.Color.ShouldBe("#ffffff");
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task DeleteLabel_AdminLevel_UnassignsFromCards(UserRole role)
    {
        var (db, _, label, _, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await label.CreateLabelAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"DelLabel-{role}-{Guid.NewGuid():N}", null));
        var labelId = created.GetProperty("id").GetGuid();

        // Assign the label to a card to confirm the CardLabel cleanup.
        var defaultLane = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane).FirstAsync();
        var defaultSize = await db.CardSizes.Where(s => s.BoardId == _factory.DefaultBoardId).OrderBy(s => s.Ordinal).FirstAsync();
        var card = await AddCardAsync(db, defaultLane.Id, defaultSize.Id);
        db.CardLabels.Add(new CardLabel { CardId = card.Id, LabelId = labelId });
        await db.SaveChangesAsync();

        var result = await label.DeleteLabelAsync(authKey, labelId);

        result.ShouldBe("Label deleted.");
        db.ChangeTracker.Clear();
        (await db.Labels.FindAsync(labelId)).ShouldBeNull();
        (await db.CardLabels.AnyAsync(cl => cl.LabelId == labelId)).ShouldBeFalse();
        (await db.Cards.FindAsync(card.Id)).ShouldNotBeNull("deleting a label must not delete the card");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteLabel_NonAdmin_ReturnsError(UserRole role)
    {
        var (db, _, label, _, _) = CreateTools();
        var created = JsonSerializer.Deserialize<JsonElement>(
            await label.CreateLabelAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"GuardedLabel-{Guid.NewGuid():N}", null));
        var labelId = created.GetProperty("id").GetGuid();
        var authKey = await AuthKeyForAsync(role);

        var result = await label.DeleteLabelAsync(authKey, labelId);

        result.ShouldBe(_adminPrivilegeError);
        db.ChangeTracker.Clear();
        (await db.Labels.FindAsync(labelId)).ShouldNotBeNull();
    }

    // create_size / update_size / delete_size

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task CreateSize_AdminLevel_AutoOrdinal_Succeeds(UserRole role)
    {
        var (db, _, _, size, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var maxBefore = await db.CardSizes.Where(s => s.BoardId == _factory.DefaultBoardId).MaxAsync(s => s.Ordinal);

        var result = await size.CreateSizeAsync(authKey, _factory.DefaultBoardId, $"Size-{role}-{Guid.NewGuid():N}", ordinal: null);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("ordinal").GetInt32().ShouldBe(maxBefore + 1);
        (await db.CardSizes.FindAsync(json.GetProperty("id").GetGuid())).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task CreateSize_NonAdmin_ReturnsError(UserRole role)
    {
        var (_, _, _, size, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);

        var result = await size.CreateSizeAsync(authKey, _factory.DefaultBoardId, "Nope", ordinal: null);

        result.ShouldBe(_adminPrivilegeError);
    }

    [Fact]
    public async Task UpdateSize_OrdinalCollision_ReturnsError()
    {
        var (db, _, _, size, _) = CreateTools();
        var existingOrdinal = await db.CardSizes.Where(s => s.BoardId == _factory.DefaultBoardId).MinAsync(s => s.Ordinal);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await size.CreateSizeAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"ResizeMe-{Guid.NewGuid():N}", ordinal: null));

        var result = await size.UpdateSizeAsync(_factory.AdminAuthKey, created.GetProperty("id").GetGuid(), ordinal: existingOrdinal);

        result.ShouldBe("Error: Ordinal already taken by another size.");
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task UpdateSize_AdminLevel_RenamesSize(UserRole role)
    {
        var (db, _, _, size, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await size.CreateSizeAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"RenameOrig-{role}-{Guid.NewGuid():N}", ordinal: null));
        var sizeId = created.GetProperty("id").GetGuid();
        var newName = $"RenameNew-{role}-{Guid.NewGuid():N}";

        var result = await size.UpdateSizeAsync(authKey, sizeId, name: newName);

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("name").GetString().ShouldBe(newName);
        db.ChangeTracker.Clear();
        (await db.CardSizes.FindAsync(sizeId))!.Name.ShouldBe(newName);
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task DeleteSize_AdminLevel_Unused_Succeeds(UserRole role)
    {
        var (db, _, _, size, _) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await size.CreateSizeAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"DelSize-{role}-{Guid.NewGuid():N}", ordinal: null));
        var sizeId = created.GetProperty("id").GetGuid();

        var result = await size.DeleteSizeAsync(authKey, sizeId);

        result.ShouldBe("Size deleted.");
        db.ChangeTracker.Clear();
        (await db.CardSizes.FindAsync(sizeId)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteSize_InUse_ReturnsError()
    {
        var (db, _, _, size, _) = CreateTools();
        var created = JsonSerializer.Deserialize<JsonElement>(
            await size.CreateSizeAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"UsedSize-{Guid.NewGuid():N}", ordinal: null));
        var sizeId = created.GetProperty("id").GetGuid();
        var defaultLane = await db.Lanes.Where(l => l.BoardId == _factory.DefaultBoardId && !l.IsArchiveLane).FirstAsync();
        await AddCardAsync(db, defaultLane.Id, sizeId);
        await db.SaveChangesAsync();

        var result = await size.DeleteSizeAsync(_factory.AdminAuthKey, sizeId);

        result.ShouldBe("Error: Size is in use by cards.");
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task DeleteSize_NonAdmin_ReturnsError(UserRole role)
    {
        var (db, _, _, size, _) = CreateTools();
        var created = JsonSerializer.Deserialize<JsonElement>(
            await size.CreateSizeAsync(_factory.AdminAuthKey, _factory.DefaultBoardId, $"GuardedSize-{Guid.NewGuid():N}", ordinal: null));
        var sizeId = created.GetProperty("id").GetGuid();
        var authKey = await AuthKeyForAsync(role);

        var result = await size.DeleteSizeAsync(authKey, sizeId);

        result.ShouldBe(_adminPrivilegeError);
        db.ChangeTracker.Clear();
        (await db.CardSizes.FindAsync(sizeId)).ShouldNotBeNull();
    }

    // create_board / update_board

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task CreateBoard_AdminLevel_SeedsArchiveLaneAndDefaultSizes(UserRole role)
    {
        var (db, _, _, _, board) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var name = $"Board {role} {Guid.NewGuid():N}";

        var result = await board.CreateBoardAsync(authKey, name);

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        var boardId = json.GetProperty("id").GetGuid();
        json.GetProperty("slug").GetString().ShouldBe(Board.GenerateSlug(name));
        (await db.Lanes.CountAsync(l => l.BoardId == boardId && l.IsArchiveLane)).ShouldBe(1);
        var sizeNames = await db.CardSizes.Where(s => s.BoardId == boardId).OrderBy(s => s.Ordinal).Select(s => s.Name).ToListAsync();
        sizeNames.ShouldBe(["S", "M", "L", "XL"]);
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task CreateBoard_NonAdmin_ReturnsError(UserRole role)
    {
        var (db, _, _, _, board) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var before = await db.Boards.CountAsync();

        var result = await board.CreateBoardAsync(authKey, $"Forbidden {Guid.NewGuid():N}");

        result.ShouldBe(_adminPrivilegeError);
        (await db.Boards.CountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task CreateBoard_DuplicateSlug_ReturnsError()
    {
        var (_, _, _, _, board) = CreateTools();
        var name = $"Slug Collide {Guid.NewGuid():N}";
        await board.CreateBoardAsync(_factory.AdminAuthKey, name);

        var result = await board.CreateBoardAsync(_factory.AdminAuthKey, name);

        result.ShouldBe("Error: A board with that slug already exists.");
    }

    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.AgentAdministrator)]
    public async Task UpdateBoard_AdminLevel_RenamesButKeepsSlug(UserRole role)
    {
        var (db, _, _, _, board) = CreateTools();
        var authKey = await AuthKeyForAsync(role);
        var created = JsonSerializer.Deserialize<JsonElement>(
            await board.CreateBoardAsync(_factory.AdminAuthKey, $"Rename Board {role} {Guid.NewGuid():N}"));
        var boardId = created.GetProperty("id").GetGuid();
        var originalSlug = created.GetProperty("slug").GetString();

        var result = await board.UpdateBoardAsync(authKey, boardId, "Totally Different Name");

        var json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("name").GetString().ShouldBe("Totally Different Name");
        json.GetProperty("slug").GetString().ShouldBe(originalSlug);
        db.ChangeTracker.Clear();
        (await db.Boards.FindAsync(boardId))!.Slug.ShouldBe(originalSlug);
    }

    [Theory]
    [InlineData(UserRole.HumanUser)]
    [InlineData(UserRole.AgentUser)]
    public async Task UpdateBoard_NonAdmin_ReturnsError(UserRole role)
    {
        var (_, _, _, _, board) = CreateTools();
        var authKey = await AuthKeyForAsync(role);

        var result = await board.UpdateBoardAsync(authKey, _factory.DefaultBoardId, "Hijacked");

        result.ShouldBe(_adminPrivilegeError);
    }
}
