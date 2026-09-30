using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Collabot.Collattice.Api.Mcp;
using Collabot.Collattice.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Webhook coverage. Every write surface the running app exposes — each REST route with a mutating
// HTTP method or with no method restriction at all, and each MCP tool not annotated read-only — must
// appear in exactly one of the two lists below: EmittingSurfaces (the events it raises, each proven by
// driving the surface for real) or SilentSurfaces (deliberately raises nothing, with the reason). The
// catalog tests prove that the declared events are internally consistent; they cannot notice a write
// that declares nothing at all, which is how card deletion and the whole size family once shipped
// silent under green CI (https://github.com/MrBildo/collattice/issues/402). A new write surface that
// is neither wired to emit nor listed as silent fails here, by name, and so does an emitting surface
// that stops emitting.
// Sealed so the plain IDisposable shape below is complete; no derived class can add state to dispose.
public sealed class WebhookCoverageTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>, IDisposable
{
    private static readonly HashSet<string> _mutatingMethods = new(StringComparer.Ordinal) { "POST", "PUT", "PATCH", "DELETE" };

    private readonly WebhookTestFactory _factory = factory;
    private readonly List<IServiceScope> _scopes = [];

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }
    }

    // The known-emitting map: surface → every event it can raise. A multi-axis surface lists each axis,
    // and its driver exercises all of them in one request so a missing axis is caught too.
    private static readonly Dictionary<string, EmittingSurface> _emittingSurfaces = new(StringComparer.Ordinal)
    {
        // ── REST: boards (webhook-only — board CRUD rings no SSE bell) ──
        // A new board is seeded with its hidden archive lane and default sizes; those rows are part of
        // the board's creation and are deliberately not reported as lane.created / size.created.
        ["POST /api/v1/boards"] = new(["board.created"], static async scenario =>
        {
            scenario.StartCapture();
            await scenario.PostAsync("/api/v1/boards", new { name = CoverageScenario.UniqueName("Created") });
        }),
        ["PATCH /api/v1/boards/{id:guid}"] = new(["board.renamed"], static async scenario =>
        {
            var boardId = await scenario.BareBoardAsync();
            scenario.StartCapture();
            await scenario.PatchAsync($"/api/v1/boards/{boardId}", new { name = CoverageScenario.UniqueName("Renamed") });
        }),
        ["DELETE /api/v1/boards/{id:guid}"] = new(["board.deleted"], static async scenario =>
        {
            var boardId = await scenario.BareBoardAsync();
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/boards/{boardId}");
        }),

        // ── REST: cards ──
        ["POST /api/v1/boards/{boardId:guid}/cards"] = new(["card.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/cards", new { name = "Created", laneId = board.LaneA });
        }),
        ["PATCH /api/v1/cards/{id:guid}"] = new(["card.updated", "card.moved", "card.labeled", "card.unlabeled"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board, board.LabelA);
            scenario.StartCapture();
            await scenario.PatchAsync($"/api/v1/cards/{cardId}", new { name = "Every Axis", laneId = board.LaneB, labelIds = new[] { board.LabelB } });
        }),
        ["POST /api/v1/cards/{id:guid}/reorder"] = new(["card.moved"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/cards/{cardId}/reorder", new { laneId = board.LaneB, index = 0 });
        }),
        ["DELETE /api/v1/cards/{id:guid}"] = new(["card.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/cards/{cardId}");
        }),
        ["POST /api/v1/cards/{id:guid}/archive"] = new(["card.archived"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/cards/{cardId}/archive", new { });
        }),
        ["POST /api/v1/cards/{id:guid}/restore"] = new(["card.restored"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.ArchivedCardAsync(board);
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/cards/{cardId}/restore", new { laneId = board.LaneA });
        }),
        ["POST /api/v1/cards/{id:guid}/finalize"] = new(["card.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var draft = await scenario.PostAsync($"/api/v1/boards/{board.Id}/cards/temp", new { name = "Draft", laneId = board.LaneA });
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/cards/{CoverageScenario.Id(draft)}/finalize", new { });
        }),

        // ── REST: lanes ──
        ["POST /api/v1/boards/{boardId:guid}/lanes"] = new(["lane.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/lanes", new { name = "Third", position = 2 });
        }),
        ["POST /api/v1/boards/{boardId:guid}/lanes/reorder"] = new(["lane.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/lanes/reorder", new { laneIds = new[] { board.LaneB, board.LaneA } });
        }),
        ["PATCH /api/v1/lanes/{id:guid}"] = new(["lane.renamed", "lane.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.PatchAsync($"/api/v1/lanes/{board.LaneB}", new { name = "Renamed", position = 5 });
        }),
        ["DELETE /api/v1/lanes/{id:guid}"] = new(["lane.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/lanes/{board.LaneB}");
        }),

        // ── REST: sizes ──
        ["POST /api/v1/boards/{boardId:guid}/sizes"] = new(["size.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/sizes", new { name = "XXL" });
        }),
        ["POST /api/v1/boards/{boardId:guid}/sizes/reorder"] = new(["size.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var reversed = (await scenario.SizeIdsInOrderAsync(board)).AsEnumerable().Reverse().ToArray();
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/sizes/reorder", new { sizeIds = reversed });
        }),
        ["PATCH /api/v1/sizes/{id:guid}"] = new(["size.renamed", "size.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var sizeId = await scenario.SizeAsync(board);
            var ordinal = await scenario.NextFreeOrdinalAsync(board);
            scenario.StartCapture();
            await scenario.PatchAsync($"/api/v1/sizes/{sizeId}", new { name = "Renamed", ordinal });
        }),
        ["DELETE /api/v1/sizes/{id:guid}"] = new(["size.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var sizeId = await scenario.SizeAsync(board);
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/sizes/{sizeId}");
        }),

        // ── REST: labels (the board-scoped resource, and a card's label set) ──
        ["POST /api/v1/boards/{boardId:guid}/labels"] = new(["label.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/labels", new { name = "Created", color = "#112233" });
        }),
        ["PATCH /api/v1/boards/{boardId:guid}/labels/{id:guid}"] = new(["label.updated"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.PatchAsync($"/api/v1/boards/{board.Id}/labels/{board.LabelA}", new { name = "Renamed" });
        }),
        ["DELETE /api/v1/boards/{boardId:guid}/labels/{id:guid}"] = new(["label.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/boards/{board.Id}/labels/{board.LabelA}");
        }),
        ["POST /api/v1/cards/{id:guid}/labels"] = new(["card.labeled"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/cards/{cardId}/labels", new { labelId = board.LabelA });
        }),
        ["DELETE /api/v1/cards/{id:guid}/labels/{labelId:guid}"] = new(["card.unlabeled"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board, board.LabelA);
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/cards/{cardId}/labels/{board.LabelA}");
        }),

        // ── REST: comments ──
        ["POST /api/v1/cards/{id:guid}/comments"] = new(["comment.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Created" });
        }),
        ["PATCH /api/v1/comments/{id:guid}"] = new(["comment.updated"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var commentId = await scenario.CommentAsync(await scenario.CardAsync(board));
            scenario.StartCapture();
            await scenario.PatchAsync($"/api/v1/comments/{commentId}", new { contentMarkdown = "Edited" });
        }),
        ["DELETE /api/v1/comments/{id:guid}"] = new(["comment.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var commentId = await scenario.CommentAsync(await scenario.CardAsync(board));
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/comments/{commentId}");
        }),

        // ── REST: attachments ──
        ["POST /api/v1/cards/{id:guid}/attachments"] = new(["attachment.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            scenario.StartCapture();
            await scenario.AttachmentAsync(cardId);
        }),
        ["DELETE /api/v1/attachments/{id:guid}"] = new(["attachment.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var attachmentId = await scenario.AttachmentAsync(await scenario.CardAsync(board));
            scenario.StartCapture();
            await scenario.DeleteAsync($"/api/v1/attachments/{attachmentId}");
        }),

        // ── REST: prune — the archive action and the delete action are both reached ──
        ["POST /api/v1/boards/{boardId:guid}/prune"] = new(["card.archived", "card.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            await scenario.CardAsync(board, board.LabelA);
            await scenario.CardAsync(board, board.LabelB);
            scenario.StartCapture();
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/prune", new { labelIds = new[] { board.LabelA } });
            await scenario.PostAsync($"/api/v1/boards/{board.Id}/prune", new { labelIds = new[] { board.LabelB }, action = "delete" });
        }),

        // ── MCP: cards ──
        ["mcp create_card"] = new(["card.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<CardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.CreateCardAsync(scenario.AdminKey, "Created", board.LaneA));
        }),
        ["mcp duplicate_card"] = new(["card.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            var tools = scenario.Tool<DuplicateCardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.DuplicateCardAsync(scenario.AdminKey, cardId: cardId));
        }),
        ["mcp move_card"] = new(["card.moved"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            var tools = scenario.Tool<CardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.MoveCardAsync(scenario.AdminKey, board.LaneB, cardId: cardId));
        }),
        ["mcp update_card"] = new(["card.updated", "card.moved", "card.labeled", "card.unlabeled"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board, board.LabelA);
            var tools = scenario.Tool<CardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.UpdateCardAsync(scenario.AdminKey, cardId: cardId, name: "Every Axis", laneId: board.LaneB, labelIds: board.LabelB.ToString()));
        }),
        ["mcp archive_card"] = new(["card.archived"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            var tools = scenario.Tool<ArchiveTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.ArchiveCardAsync(scenario.AdminKey, cardId: cardId));
        }),
        ["mcp restore_card"] = new(["card.restored"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.ArchivedCardAsync(board);
            var tools = scenario.Tool<ArchiveTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.RestoreCardAsync(scenario.AdminKey, board.LaneA, cardId: cardId));
        }),
        ["mcp bulk_archive_cards"] = new(["card.archived"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            var tools = scenario.Tool<BulkCardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.BulkArchiveCardsAsync(scenario.AdminKey, cardIds: cardId.ToString()));
        }),
        ["mcp bulk_restore_cards"] = new(["card.restored"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.ArchivedCardAsync(board);
            var tools = scenario.Tool<BulkCardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.BulkRestoreCardsAsync(scenario.AdminKey, board.LaneA, cardIds: cardId.ToString()));
        }),
        ["mcp bulk_update_cards"] = new(["card.moved", "card.updated", "card.labeled", "card.unlabeled"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board, board.LabelA);
            var sizeId = await scenario.SizeAsync(board);
            var tools = scenario.Tool<BulkCardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok
            (
                await tools.BulkUpdateCardsAsync
                (
                    scenario.AdminKey,
                    cardIds: cardId.ToString(),
                    laneId: board.LaneB,
                    sizeId: sizeId,
                    labelIds: board.LabelB.ToString()
                )
            );
        }),
        ["mcp prune"] = new(["card.archived"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            await scenario.CardAsync(board, board.LabelA);
            var tools = scenario.Tool<PruneTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.PruneAsync(scenario.AdminKey, board.Id, labelIds: board.LabelA.ToString()));
        }),

        // ── MCP: comments ──
        ["mcp add_comment"] = new(["comment.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            var tools = scenario.Tool<CommentTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.AddCommentAsync(scenario.AdminKey, "Created", cardId: cardId));
        }),
        ["mcp update_comment"] = new(["comment.updated"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var commentId = await scenario.CommentAsync(await scenario.CardAsync(board));
            var tools = scenario.Tool<CommentTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.UpdateCommentAsync(scenario.AdminKey, commentId, "Edited"));
        }),
        ["mcp delete_comment"] = new(["comment.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var commentId = await scenario.CommentAsync(await scenario.CardAsync(board));
            var tools = scenario.Tool<CommentTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.DeleteCommentAsync(scenario.AdminKey, commentId));
        }),

        // ── MCP: attachments ──
        ["mcp upload_attachment"] = new(["attachment.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            var tools = scenario.Tool<AttachmentTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.UploadAttachmentAsync(scenario.AdminKey, "note.txt", Convert.ToBase64String("hello"u8.ToArray()), cardId: cardId, contentType: "text/plain"));
        }),
        ["mcp delete_attachment"] = new(["attachment.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var attachmentId = await scenario.AttachmentAsync(await scenario.CardAsync(board));
            var tools = scenario.Tool<AttachmentTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.DeleteAttachmentAsync(scenario.AdminKey, attachmentId));
        }),

        // ── MCP: labels ──
        ["mcp create_label"] = new(["label.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<LabelTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.CreateLabelAsync(scenario.AdminKey, board.Id, "Created", "#112233"));
        }),
        ["mcp update_label"] = new(["label.updated"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<LabelTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.UpdateLabelAsync(scenario.AdminKey, board.LabelA, name: "Renamed"));
        }),
        ["mcp delete_label"] = new(["label.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<LabelTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.DeleteLabelAsync(scenario.AdminKey, board.LabelA));
        }),
        ["mcp add_label_to_card"] = new(["card.labeled"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board);
            var tools = scenario.Tool<LabelTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.AddLabelToCardAsync(scenario.AdminKey, cardId: cardId, labelId: board.LabelA));
        }),
        ["mcp remove_label_from_card"] = new(["card.unlabeled"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var cardId = await scenario.CardAsync(board, board.LabelA);
            var tools = scenario.Tool<LabelTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.RemoveLabelFromCardAsync(scenario.AdminKey, cardId: cardId, labelId: board.LabelA));
        }),

        // ── MCP: lanes ──
        ["mcp create_lane"] = new(["lane.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<LaneTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.CreateLaneAsync(scenario.AdminKey, board.Id, "Third", 2));
        }),
        ["mcp update_lane"] = new(["lane.renamed", "lane.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<LaneTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.UpdateLaneAsync(scenario.AdminKey, board.LaneB, name: "Renamed", position: 5));
        }),
        ["mcp reorder_lanes"] = new(["lane.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<LaneTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.ReorderLanesAsync(scenario.AdminKey, board.Id, $"{board.LaneB},{board.LaneA}"));
        }),
        ["mcp delete_lane"] = new(["lane.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<LaneTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.DeleteLaneAsync(scenario.AdminKey, board.LaneB));
        }),

        // ── MCP: sizes ──
        ["mcp create_size"] = new(["size.created"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var tools = scenario.Tool<SizeTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.CreateSizeAsync(scenario.AdminKey, board.Id, "XXL"));
        }),
        ["mcp update_size"] = new(["size.renamed", "size.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var sizeId = await scenario.SizeAsync(board);
            var ordinal = await scenario.NextFreeOrdinalAsync(board);
            var tools = scenario.Tool<SizeTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.UpdateSizeAsync(scenario.AdminKey, sizeId, name: "Renamed", ordinal: ordinal));
        }),
        ["mcp reorder_sizes"] = new(["size.reordered"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var reversed = (await scenario.SizeIdsInOrderAsync(board)).AsEnumerable().Reverse();
            var tools = scenario.Tool<SizeTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.ReorderSizesAsync(scenario.AdminKey, board.Id, string.Join(',', reversed)));
        }),
        ["mcp delete_size"] = new(["size.deleted"], static async scenario =>
        {
            var board = await scenario.BoardAsync();
            var sizeId = await scenario.SizeAsync(board);
            var tools = scenario.Tool<SizeTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.DeleteSizeAsync(scenario.AdminKey, sizeId));
        }),

        // ── MCP: boards ──
        ["mcp create_board"] = new(["board.created"], static async scenario =>
        {
            var tools = scenario.Tool<BoardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.CreateBoardAsync(scenario.AdminKey, CoverageScenario.UniqueName("Created")));
        }),
        ["mcp update_board"] = new(["board.renamed"], static async scenario =>
        {
            var boardId = await scenario.BareBoardAsync();
            var tools = scenario.Tool<BoardTools>();
            scenario.StartCapture();
            CoverageScenario.Ok(await tools.UpdateBoardAsync(scenario.AdminKey, boardId, CoverageScenario.UniqueName("Renamed")));
        }),
    };

    // The intentionally-silent allowlist: surface → why it raises no webhook event. This is where a
    // deliberate-silence ruling lives in code; adding a write surface here is a claim that a subscriber
    // has nothing to learn from it.
    private static readonly Dictionary<string, string> _silentSurfaces = new(StringComparer.Ordinal)
    {
        ["POST /api/v1/boards/{boardId:guid}/cards/temp"] = "Starts an unsaved draft; card.created fires when the draft is finalized.",
        ["POST /api/v1/cards/{id:guid}/cancel"] = "Discards an unsaved draft no subscriber was ever told about.",
        ["POST /api/v1/boards/{boardId:guid}/prune/preview"] = "A dry run that changes nothing; POST only carries the filter body.",
        ["POST /api/v1/users"] = "User accounts are not board facts; user events are deliberately outside the catalog.",
        ["PATCH /api/v1/users/{id:guid}"] = "User accounts are not board facts; user events are deliberately outside the catalog.",
        ["PATCH /api/v1/users/{id:guid}/deactivate"] = "User accounts are not board facts; user events are deliberately outside the catalog.",
        ["POST /api/v1/webhooks/subscriptions"] = "Configures webhook delivery itself, not the board.",
        ["PATCH /api/v1/webhooks/subscriptions/{id:guid}"] = "Configures webhook delivery itself, not the board.",
        ["DELETE /api/v1/webhooks/subscriptions/{id:guid}"] = "Configures webhook delivery itself, not the board.",
        ["POST /api/v1/webhooks/subscriptions/{id:guid}/test"] = "Sends webhook.ping to the one subscription under test; no board fact changed.",
        ["mcp create_webhook"] = "Configures webhook delivery itself, not the board.",
        ["mcp update_webhook"] = "Configures webhook delivery itself, not the board.",
        ["mcp delete_webhook"] = "Configures webhook delivery itself, not the board.",
        ["mcp test_webhook"] = "Sends webhook.ping to the one subscription under test; no board fact changed.",
        ["POST /mcp/"] = "The MCP transport; every tool behind it is enumerated as its own surface.",
        ["DELETE /mcp/"] = "Ends an MCP transport session; no board fact changes.",
        ["ANY /health"] = "Health probe; it reads state and never changes it, whatever the method.",
        ["ANY /alive"] = "Liveness probe; it reads state and never changes it, whatever the method.",
    };

    // MA0005 misreads a collection expression that targets TheoryData as an empty-array allocation, and
    // the constructor form trips IDE0028 instead; the collection expression is the house form.
#pragma warning disable MA0005
    public static TheoryData<string> EmittingSurfaceKeys => [.. _emittingSurfaces.Keys.Order(StringComparer.Ordinal)];
#pragma warning restore MA0005

    [Fact]
    public void EveryWriteSurface_IsEitherEmittingOrDeliberatelySilent()
    {
        var surfaces = EnumerateWriteSurfaces();

        var unlisted = surfaces
            .Where(surface => !_emittingSurfaces.ContainsKey(surface) && !_silentSurfaces.ContainsKey(surface))
            .Order(StringComparer.Ordinal)
                .ToList();
        var stale = _emittingSurfaces.Keys
            .Concat(_silentSurfaces.Keys)
            .Where(surface => !surfaces.Contains(surface))
            .Order(StringComparer.Ordinal)
                .ToList();
        var listedTwice = _emittingSurfaces.Keys
            .Where(_silentSurfaces.ContainsKey)
            .Order(StringComparer.Ordinal)
                .ToList();

        unlisted.ShouldBeEmpty($"write surfaces that neither emit a webhook event nor are listed as deliberately silent: {string.Join("; ", unlisted)}");
        stale.ShouldBeEmpty($"listed surfaces the running app no longer exposes: {string.Join("; ", stale)}");
        listedTwice.ShouldBeEmpty($"surfaces listed as both emitting and silent: {string.Join("; ", listedTwice)}");
    }

    // The positive control for the enumeration above: a zero from it is only evidence if it demonstrably
    // sees write surfaces on both transports and leaves reads out.
    [Fact]
    public void WriteSurfaceEnumeration_SeesRestAndMcpWrites_AndNoReads()
    {
        var surfaces = EnumerateWriteSurfaces();

        surfaces.ShouldContain("DELETE /api/v1/cards/{id:guid}");
        surfaces.ShouldContain("mcp update_card");
        surfaces.ShouldNotContain(surface => surface.StartsWith("GET ", StringComparison.Ordinal));
        surfaces.ShouldNotContain("mcp get_card");
    }

    [Theory]
    [MemberData(nameof(EmittingSurfaceKeys))]
    public async Task EmittingSurface_RaisesEveryMappedEvent(string surface)
    {
        var expected = _emittingSurfaces[surface];
        var scenario = new CoverageScenario(_factory, _scopes);

        await expected.Drive(scenario);

        // Events raised while arranging would mask a missing emit, so every driver clears the capture
        // after its setup and immediately before it drives the surface.
        scenario.CaptureStarted.ShouldBeTrue($"{surface}'s driver never called StartCapture");

        var raised = _factory.Sink.Captured
            .Select(boardEvent => boardEvent.EventType)
                .ToHashSet(StringComparer.Ordinal);
        var missing = expected.Events
            .Where(eventType => !raised.Contains(eventType))
                .ToList();

        missing.ShouldBeEmpty($"{surface} did not raise {string.Join(", ", missing)} (it raised: {string.Join(", ", raised.Order(StringComparer.Ordinal))})");
    }

    private HashSet<string> EnumerateWriteSurfaces()
    {
        var restSurfaces = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
                .SelectMany(RestWriteSurfaces);

        var mcpSurfaces = _factory.Services.GetServices<McpServerTool>()
            .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint is not true)
                .Select(tool => $"mcp {tool.ProtocolTool.Name}");

        return restSurfaces
            .Concat(mcpSurfaces)
                .ToHashSet(StringComparer.Ordinal);
    }

    // A route that declares no HTTP method accepts every method, POST included, so it is a write
    // surface too; it is keyed ANY because no single verb names it.
    private static IEnumerable<string> RestWriteSurfaces(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

        return methods.Count == 0
            ? [$"ANY {endpoint.RoutePattern.RawText}"]
            : methods
                .Where(_mutatingMethods.Contains)
                    .Select(method => $"{method} {endpoint.RoutePattern.RawText}");
    }

    private sealed record EmittingSurface(string[] Events, Func<CoverageScenario, Task> Drive);

    private sealed record CoverageBoard(Guid Id, Guid LaneA, Guid LaneB, Guid LabelA, Guid LabelB);

    // Arranges the state a surface driver needs — each driver gets its own fresh board, so no row depends
    // on what another row left behind — and holds the seeded admin's HTTP client and MCP tool instances.
    // Sealed: a private helper of this one test class, not meant to be extended.
    private sealed class CoverageScenario
    {
        private readonly WebhookTestFactory _factory;
        private readonly List<IServiceScope> _scopes;
        private readonly HttpClient _client;

        public CoverageScenario(WebhookTestFactory factory, List<IServiceScope> scopes)
        {
            _factory = factory;
            _scopes = scopes;
            _client = factory.CreateClient();

            TestAuthHelper.SetAdminAuth(_client, factory);
        }

        public bool CaptureStarted { get; private set; }

        public string AdminKey => CollatticeApiFactory.TestAdminAuthKey;

        public static string UniqueName(string baseName) => $"{baseName} {Guid.NewGuid():N}";

        public static Guid Id(JsonElement json) => json.GetProperty("id").GetGuid();

        public static void Ok(string toolResult) => toolResult.ShouldNotContain("Error");

        public void StartCapture()
        {
            _factory.Sink.Clear();
            CaptureStarted = true;
        }

        public T Tool<T>() where T : class
        {
            var scope = _factory.Services.CreateScope();
            _scopes.Add(scope);

            return ActivatorUtilities.CreateInstance<T>(scope.ServiceProvider);
        }

        public async Task<JsonElement> PostAsync(string path, object body)
        {
            var response = await _client.PostAsJsonAsync(path, body);
            response.EnsureSuccessStatusCode();

            return await ReadAsync(response);
        }

        public async Task PatchAsync(string path, object body)
        {
            var response = await _client.PatchAsJsonAsync(path, body);
            response.EnsureSuccessStatusCode();
        }

        public async Task DeleteAsync(string path)
        {
            var response = await _client.DeleteAsync(path);
            response.EnsureSuccessStatusCode();
        }

        // A board with no lanes of its own — only its hidden archive lane — so it is deletable.
        public async Task<Guid> BareBoardAsync() =>
            Id(await PostAsync("/api/v1/boards", new { name = UniqueName("Coverage") }));

        public async Task<CoverageBoard> BoardAsync()
        {
            var boardId = await BareBoardAsync();

            var laneA = Id(await PostAsync($"/api/v1/boards/{boardId}/lanes", new { name = "A", position = 0 }));
            var laneB = Id(await PostAsync($"/api/v1/boards/{boardId}/lanes", new { name = "B", position = 1 }));

            var labelA = Id(await PostAsync($"/api/v1/boards/{boardId}/labels", new { name = "a", color = "#aa0000" }));
            var labelB = Id(await PostAsync($"/api/v1/boards/{boardId}/labels", new { name = "b", color = "#0000aa" }));

            return new CoverageBoard(boardId, laneA, laneB, labelA, labelB);
        }

        public async Task<Guid> CardAsync(CoverageBoard board, Guid? labelId = null)
        {
            Guid[] labelIds = labelId is null ? [] : [labelId.Value];

            return Id(await PostAsync($"/api/v1/boards/{board.Id}/cards", new { name = "Card", laneId = board.LaneA, labelIds }));
        }

        public async Task<Guid> ArchivedCardAsync(CoverageBoard board)
        {
            var cardId = await CardAsync(board);

            await PostAsync($"/api/v1/cards/{cardId}/archive", new { });

            return cardId;
        }

        public async Task<Guid> CommentAsync(Guid cardId) =>
            Id(await PostAsync($"/api/v1/cards/{cardId}/comments", new { contentMarkdown = "Comment" }));

        public async Task<Guid> AttachmentAsync(Guid cardId)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "file", "note.txt");

            var response = await _client.PostAsync($"/api/v1/cards/{cardId}/attachments", form);
            response.EnsureSuccessStatusCode();

            return Id(await ReadAsync(response));
        }

        // Omitting the ordinal lets the server place the new size above every existing one.
        public async Task<Guid> SizeAsync(CoverageBoard board) =>
            Id(await PostAsync($"/api/v1/boards/{board.Id}/sizes", new { name = UniqueName("Size") }));

        public async Task<List<Guid>> SizeIdsInOrderAsync(CoverageBoard board)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            return await db.CardSizes
                .Where(size => size.BoardId == board.Id)
                .OrderBy(size => size.Ordinal)
                    .Select(size => size.Id)
                        .ToListAsync();
        }

        // An ordinal past every current size; a PATCH to a taken ordinal is rejected, so a move targets this.
        public async Task<int> NextFreeOrdinalAsync(CoverageBoard board)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardDbContext>();

            var max = await db.CardSizes
                .Where(size => size.BoardId == board.Id)
                    .MaxAsync(size => (int?)size.Ordinal);

            return (max ?? -1) + 1;
        }

        private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();

            return string.IsNullOrWhiteSpace(body)
                ? default
                : JsonDocument.Parse(body).RootElement;
        }
    }
}
