using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Collabot.Collattice.Api.Mcp;

// Webhook subscription management over MCP. Five admin-level tools mirroring the REST CRUD +
// test surface. Every tool delegates to the SAME shared WebhookSubscriptionStore (and WebhookTester
// for the ping) the REST endpoints use — so the two surfaces produce identical results and it is
// structurally impossible to create a subscription via MCP that bypasses the REST SSRF validation
// (the precedent is CardSummaryBuilder / SearchHelper, NOT LabelTools' per-surface re-implementation).
// The auth gate + JSON serialization are the only per-surface concerns; the data op is shared. All
// five gate RequireAdminLevelAsync (Administrator OR AgentAdministrator) — and the SSRF floor is
// what makes "an agent manages webhooks" safe, so it is load-bearing FOR this surface, not optional.
// internal (not public like the sibling *Tools): the constructor takes the internal
// WebhookSubscriptionStore / WebhookTester (the foundation kept the store + its DTOs internal —
// their read projections are internal API). WithToolsFromAssembly discovers via GetTypes() (all
// types, not public-only) and instantiates through the public primary constructor, so an internal
// tool type is discovered and resolved the same as a public one.
[McpServerToolType]
internal sealed class WebhookTools(WebhookSubscriptionStore store, WebhookTester tester, McpAuthService auth)
{
    [McpServerTool(Name = "create_webhook", Destructive = false)]
    [Description("Create a webhook subscription. Requires Administrator or AgentAdministrator role. The URL must be http/https and (unless Webhooks:AllowPrivateNetworkTargets is set) must not resolve to a private/loopback/link-local address. events is a comma-separated list of event types or \"*\" for all. The optional secret is the HMAC signing key (write-only — never returned).")]
    public async Task<string> CreateWebhookAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The dial-out URL (http/https)")] string url,
        [Description("Comma-separated event types to receive (e.g. \"card.created,card.moved\"), or \"*\" for all current and future events")] string events,
        [Description("Optional HMAC signing secret (write-only)")] string? secret = null,
        [Description("Whether the subscription is enabled (default true)")] bool enabled = true,
        [Description("Optional operator label (e.g. \"n8n prod\")")] string? name = null,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var view = await store.CreateAsync
            (
                new WebhookSubscriptionInput(url, SplitEvents(events), secret, enabled, name),
                ct
            );

            return JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
        }
        catch (WebhookValidationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_webhooks", ReadOnly = true, Destructive = false)]
    [Description("List all webhook subscriptions (global — not board-scoped). Requires Administrator or AgentAdministrator role. Each carries delivery metrics and a `signed` boolean; the secret is never returned.")]
    public async Task<string> ListWebhooksAsync
    (
        [Description("Your auth key")] string authKey,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var views = await store.ListAsync(ct);
        return JsonSerializer.Serialize(views, JsonSerializerOptions.Web);
    }

    [McpServerTool(Name = "update_webhook", Destructive = false)]
    [Description("Update a webhook subscription. Requires Administrator or AgentAdministrator role. Any omitted field is left unchanged. events (if provided) replaces the selection. Secret set/keep/clear: omit secret to keep it, pass secret to replace it, pass clearSecret=true to remove it (go unsigned).")]
    public async Task<string> UpdateWebhookAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the subscription to update")] Guid webhookId,
        [Description("New dial-out URL (optional; re-validated for SSRF when provided)")] string? url = null,
        [Description("New comma-separated event types or \"*\" (optional; replaces the selection when provided)")] string? events = null,
        [Description("New HMAC secret (optional; replaces the existing secret)")] string? secret = null,
        [Description("Set true to clear the secret (go unsigned)")] bool clearSecret = false,
        [Description("New enabled state (optional)")] bool? enabled = null,
        [Description("New operator label (optional)")] string? name = null,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var view = await store.UpdateAsync
            (
                webhookId,
                new WebhookSubscriptionPatch(url, events is null ? null : SplitEvents(events), secret, clearSecret, enabled, name),
                ct
            );

            return view is null
                ? "Error: Webhook subscription not found."
                : JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
        }
        catch (WebhookValidationException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "delete_webhook", Destructive = true)]
    [Description("Delete a webhook subscription. Requires Administrator or AgentAdministrator role. The subscription's delivery-attempt history is preserved (the audit log outlives the subscription).")]
    public async Task<string> DeleteWebhookAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the subscription to delete")] Guid webhookId,
        CancellationToken ct = default
    )
    {
        var (_, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var deleted = await store.DeleteAsync(webhookId, ct);
        return deleted ? "Webhook subscription deleted." : "Error: Webhook subscription not found.";
    }

    [McpServerTool(Name = "test_webhook", Destructive = false)]
    [Description("Send a synchronous test delivery (webhook.ping) to one subscription through the same guarded pipe as a real event (SSRF + signing), and return the outcome. Requires Administrator or AgentAdministrator role. Records one delivery-attempt row.")]
    public async Task<string> TestWebhookAsync
    (
        [Description("Your auth key")] string authKey,
        [Description("The ID (guid) of the subscription to test")] Guid webhookId,
        CancellationToken ct = default
    )
    {
        var (user, error) = await auth.RequireAdminLevelAsync(authKey, ct);
        if (error is not null)
        {
            return error;
        }

        var result = await tester.TestAsync(webhookId, user!, ct);
        return result is null
            ? "Error: Webhook subscription not found."
            : JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
    }

    // The events param is a CSV (note McpGuidCsv is GUID-specific, not reusable here). Empties are
    // dropped; the shared store validates non-empty / known-or-wildcard / collapses the wildcard.
    private static string[] SplitEvents(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
