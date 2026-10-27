using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

// Admin-level MCP tools for lane CRUD. Mirrors the REST
// surface in LaneEndpoints.cs (POST /boards/{boardId}/lanes, PATCH /lanes/{id},
// DELETE /lanes/{id}). All three gate via RequireAdminLevelAsync.
[McpServerToolType]
public class LaneTools(BoardDbContext db, McpAuthService auth, BoardEventBroadcaster broadcaster)
{
    [McpServerTool(Name = "create_lane", Destructive = false)]
    [Description("Create a lane (column) on a board. Requires Administrator or AgentAdministrator role. Position is the lane's ordering value; omit it to append the lane after the board's last lane. A position already taken by another lane on the board is a conflict, and int.MaxValue is reserved for the archive lane and is rejected.")]
    public async Task<string> CreateLaneAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board ID to create the lane on")] Guid boardId,
        [Description("The lane name")] string name,
        [Description("The lane's position (ordering value). Optional; omitted appends after the board's last lane.")] int? position = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct))
        {
            return "Error: Board not found.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: Name is required.";
        }

        var created = await LanePositionHelper.CreateAsync(db, boardId, name, position, ct);
        if (created.Lane is not Lane lane)
        {
            return $"Error: {created.Error}";
        }

        // lane.created — REST/MCP emit the identical event through the shared factory.
        await WebhookEventFactory.PublishLaneCreatedAsync(db, broadcaster, lane, user!, ct);
        return JsonSerializer.Serialize(lane, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "update_lane", Destructive = false)]
    [Description("Update a lane's name and/or position. Requires Administrator or AgentAdministrator role. Archive lanes cannot be modified. Position int.MaxValue is reserved; a position already taken by another lane on the board is a conflict, and nothing in the call is saved.")]
    public async Task<string> UpdateLaneAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the lane to update")] Guid laneId,
        [Description("The new lane name (optional)")] string? name = null,
        [Description("The new position (optional)")] int? position = null,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var lane = await db.Lanes.FindAsync([laneId], ct);
        if (lane is null)
        {
            return "Error: Lane not found.";
        }

        // Capture the pre-mutation values for the per-axis no-op guard.
        var oldName = lane.Name;
        var oldPosition = lane.Position;

        var (updateError, _) = await LaneUpdateHelper.UpdateAsync(db, lane, name, position, ct);
        if (updateError is not null)
        {
            return $"Error: {updateError}";
        }

        // Split by axis: name → lane.renamed; position → lane.reordered (board's full new
        // order). Co-fire through PublishCoalesced — one SSE bell, identical to the REST PATCH.
        var nameChanged = name is not null && name != oldName;
        var positionChanged = position is not null && position.Value != oldPosition;

        var events = await WebhookEventFactory.BuildLaneUpdateEventsAsync(db, lane, user!, nameChanged, positionChanged, ct);
        broadcaster.PublishCoalesced(lane.BoardId, events);
        return JsonSerializer.Serialize(lane, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "reorder_lanes", Destructive = false)]
    [Description("Reorder all of a board's non-archive lanes in one call. Requires Administrator or AgentAdministrator role. Pass orderedLaneIds as a CSV of lane GUIDs giving the complete desired left-to-right order — it must be exactly the board's current non-archive lane set (no missing, extra, duplicate, or unknown ids), else the call fails loud with no changes. The server assigns dense positions 0,1,2,… in that order; the archive lane is untouched. Returns the reordered lanes.")]
    public async Task<string> ReorderLanesAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The board ID whose lanes to reorder")] Guid boardId,
        [Description("CSV of lane GUIDs in the complete desired left-to-right order")] string orderedLaneIds,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct))
        {
            return "Error: Board not found.";
        }

        var parts = (orderedLaneIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new Guid[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Guid.TryParse(parts[i], out var parsed))
            {
                return $"Error: Invalid lane ID format: '{parts[i]}'. Expected a GUID.";
            }

            ids[i] = parsed;
        }

        var (lanes, validationError) = await LaneReorderHelper.ValidateAsync(db, boardId, ids, ct);
        if (validationError is not null)
        {
            return $"Error: {validationError}";
        }

        var ordered = await LaneReorderHelper.ReorderAsync(db, lanes!, ids, ct);

        // lane.reordered — ONE event carrying the board's full new order (never N), same single board
        // bell the reorder always rang, identical to the REST reorder.
        await WebhookEventFactory.PublishLaneReorderedAsync(db, broadcaster, boardId, user!, ct);
        return JsonSerializer.Serialize(ordered, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "delete_lane", Destructive = true)]
    [Description("Delete a lane. Requires Administrator or AgentAdministrator role. Archive lanes cannot be deleted, and a lane must be empty (no cards) before it can be deleted.")]
    public async Task<string> DeleteLaneAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the lane to delete")] Guid laneId,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var lane = await db.Lanes.FindAsync([laneId], ct);
        if (lane is null)
        {
            return "Error: Lane not found.";
        }

        if (lane.IsArchiveLane)
        {
            return "Error: Archive lanes cannot be deleted.";
        }

        if (await db.Cards.AnyAsync(c => c.LaneId == laneId, ct))
        {
            return "Error: Lane must be empty.";
        }

        var deleteOutcome = await GuardedDelete.LaneAsync(db, lane, ct);
        if (deleteOutcome is GuardedDeleteOutcome.InUse)
        {
            return "Error: Lane must be empty.";
        }

        if (deleteOutcome is GuardedDeleteOutcome.NotFound)
        {
            return "Error: Lane not found.";
        }

        // lane.deleted — published from the captured lane after the row is gone; REST/MCP identical
        // through the shared factory.
        await WebhookEventFactory.PublishLaneDeletedAsync(db, broadcaster, lane, user!, ct);
        return "Lane deleted.";
    }
}
