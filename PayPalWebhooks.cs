using System.Threading.Channels;

namespace PayPal360;

public sealed record PayPalWebhookHeaders(
    string TransmissionId,
    string TransmissionTime,
    string CertUrl,
    string AuthAlgorithm,
    string TransmissionSignature);

public sealed record PayPalWebhookInboxItem(
    string EventId,
    string RawPayload,
    ProviderEvent ProviderEvent,
    DateTimeOffset ReceivedAt,
    string Status = "PENDING",
    int Attempts = 0,
    string? LastError = null);

public sealed record PayPalWebhookEnqueueResult(PayPalWebhookInboxItem Item, bool IsDuplicate);

public sealed class PayPalWebhookQueue(CommerceDatabase database)
{
    private readonly Channel<PayPalWebhookInboxItem> channel = Channel.CreateUnbounded<PayPalWebhookInboxItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    public PayPalWebhookEnqueueResult Accept(ProviderEvent providerEvent, string rawPayload) => database.Update(state =>
    {
        var existing = state.WebhookInbox.FirstOrDefault(item => item.EventId == providerEvent.EventId);
        if (existing is not null)
            return new PayPalWebhookEnqueueResult(existing, true);

        var item = new PayPalWebhookInboxItem(providerEvent.EventId, rawPayload, providerEvent, DateTimeOffset.UtcNow);
        state.WebhookInbox.Add(item);
        CommerceDatabase.Record(state, "-", "provider.webhook_received", "provider:paypal", "Verified PayPal webhook payload was durably accepted for processing.", "PayPal sandbox", providerEvent.EventId, null, providerEvent.TenantId);
        return new PayPalWebhookEnqueueResult(item, false);
    });

    public ValueTask EnqueueAsync(PayPalWebhookInboxItem item, CancellationToken ct) => channel.Writer.WriteAsync(item, ct);

    public IAsyncEnumerable<PayPalWebhookInboxItem> ReadAllAsync(CancellationToken ct) => channel.Reader.ReadAllAsync(ct);

    public IReadOnlyList<PayPalWebhookInboxItem> Pending() => database.Read(state => state.WebhookInbox
        .Where(item => item.Status is "PENDING" or "PROCESSING")
        .OrderBy(item => item.ReceivedAt)
        .ToArray());

    public PayPalWebhookInboxItem? BeginAttempt(string eventId) => database.Update(state =>
    {
        var index = state.WebhookInbox.FindIndex(item => item.EventId == eventId);
        if (index < 0 || state.WebhookInbox[index].Status is "APPLIED" or "DEAD_LETTER")
            return null;
        var next = state.WebhookInbox[index] with
        {
            Status = "PROCESSING",
            Attempts = checked(state.WebhookInbox[index].Attempts + 1)
        };
        state.WebhookInbox[index] = next;
        return next;
    });

    public void MarkApplied(string eventId) => database.Update(state =>
    {
        var index = state.WebhookInbox.FindIndex(item => item.EventId == eventId);
        if (index >= 0)
            state.WebhookInbox[index] = state.WebhookInbox[index] with
            {
                Status = "APPLIED",
                LastError = null
            };
        return true;
    });

    public void MarkDeadLetter(string eventId, string error) => database.Update(state =>
    {
        var index = state.WebhookInbox.FindIndex(item => item.EventId == eventId);
        if (index >= 0)
            state.WebhookInbox[index] = state.WebhookInbox[index] with
            {
                Status = "DEAD_LETTER",
                LastError = error.Length <= 500 ? error : error[..500]
            };
        return true;
    });
}

public sealed class PayPalWebhookWorker(PayPalWebhookQueue queue, DemoStore store, ILogger<PayPalWebhookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var pending in queue.Pending())
            await queue.EnqueueAsync(pending, stoppingToken);

        await foreach (var item in queue.ReadAllAsync(stoppingToken))
        {
            var attempt = queue.BeginAttempt(item.EventId);
            if (attempt is null)
                continue;
            try
            {
                store.ApplyProviderEvent(attempt.ProviderEvent);
                queue.MarkApplied(attempt.EventId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (attempt.Attempts >= 3)
                {
                    queue.MarkDeadLetter(attempt.EventId, exception.Message);
                    // Do not log the signed payload or credentials.
                    logger.LogError(exception, "PayPal provider event {EventId} moved to the dead-letter state after retries.", attempt.EventId);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt.Attempts), stoppingToken);
                await queue.EnqueueAsync(attempt, stoppingToken);
            }
        }
    }
}
