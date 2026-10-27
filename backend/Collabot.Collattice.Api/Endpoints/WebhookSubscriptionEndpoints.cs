namespace Collabot.Collattice.Api.Endpoints;

// Webhook subscription management (the registry CRUD + the test-delivery affordance). Every
// route gates admin-level (Administrator OR AgentAdministrator) — a write here reveals and
// redirects where the server dials and what it signs with (the SSRF channel), the opposite security
// shape from the intentionally-open SSE stream. All operations delegate to the shared
// WebhookSubscriptionStore (validation, SSRF registration check, secret set/keep/clear, secret-free
// projection) and WebhookTester (the ping), so the REST and MCP surfaces are identical by
// construction and the SSRF check is un-bypassable. The store throws WebhookValidationException for
// caller-fixable input (bad URL, empty/unknown events, an SSRF-blocked target) → mapped to 400.
internal static class WebhookSubscriptionEndpoints
{
    public static RouteGroupBuilder MapWebhookSubscriptionEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/webhooks/subscriptions", async (WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var views = await store.ListAsync(ct);
            return Results.Ok(views);
        }).RequireAdminOrAgentAdmin();

        group.MapGet("/webhooks/subscriptions/{id:guid}", async (WebhookSubscriptionStore store, Guid id, CancellationToken ct) =>
        {
            var view = await store.GetAsync(id, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        }).RequireAdminOrAgentAdmin();

        group.MapPost("/webhooks/subscriptions", async (WebhookSubscriptionStore store, CreateWebhookSubscriptionRequest request, CancellationToken ct) =>
        {
            try
            {
                var view = await store.CreateAsync
                (
                    new WebhookSubscriptionInput(request.Url, request.Events ?? [], request.Secret, request.Enabled, request.Name),
                    ct
                );

                return Results.Created($"/api/v1/webhooks/subscriptions/{view.Id}", view);
            }
            catch (WebhookValidationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).RequireAdminOrAgentAdmin();

        group.MapPatch("/webhooks/subscriptions/{id:guid}", async (WebhookSubscriptionStore store, Guid id, UpdateWebhookSubscriptionRequest request, CancellationToken ct) =>
        {
            try
            {
                var view = await store.UpdateAsync
                (
                    id,
                    new WebhookSubscriptionPatch(request.Url, request.Events, request.Secret, request.ClearSecret ?? false, request.Enabled, request.Name),
                    ct
                );

                return view is null ? Results.NotFound() : Results.Ok(view);
            }
            catch (WebhookValidationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).RequireAdminOrAgentAdmin();

        group.MapDelete("/webhooks/subscriptions/{id:guid}", async (WebhookSubscriptionStore store, Guid id, CancellationToken ct) =>
        {
            var deleted = await store.DeleteAsync(id, ct);
            return deleted ? Results.Ok(new { deleted = true }) : Results.NotFound();
        }).RequireAdminOrAgentAdmin();

        // Send a synchronous test delivery (webhook.ping) to one subscription through the same
        // guarded pipe as a real event (SSRF + signing) — the operator/agent wants the outcome now.
        group.MapPost("/webhooks/subscriptions/{id:guid}/test", async (HttpContext http, WebhookTester tester, Guid id, CancellationToken ct) =>
        {
            var result = await tester.TestAsync(id, http.CurrentUser(), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAdminOrAgentAdmin();

        return group;
    }
}
