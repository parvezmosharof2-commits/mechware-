using System.Collections.Concurrent;
using System.Text.Json;
using ShopVerse.Application.Payments;

namespace ShopVerse.Infrastructure.Payments;

/// <summary>
/// A gateway that behaves like the real ones without leaving the process, for tests and local development. A session is
/// "paid" only when <see cref="SimulatePayment"/> says so, exactly like a customer finishing on a hosted page; the
/// server-to-server <see cref="VerifyAsync"/> reports that state, and the webhook body carries ids only.
/// </summary>
public sealed class MockPaymentProvider(string webhookSecret = MockPaymentProvider.DefaultSecret) : IPaymentProvider
{
    public const string DefaultSecret = "whsec_mock_provider_secret_0001";

    private readonly ConcurrentDictionary<string, (long Amount, string Currency, bool Paid, long PaidAmount)> _sessions = new();

    public string Name => "mock";
    public string WebhookSecret => webhookSecret;

    public Task<ProviderSession> CreateSessionAsync(Guid orderId, string orderNumber, long amountMinor, string currency, CancellationToken ct = default)
    {
        var providerRef = "mock_" + Guid.NewGuid().ToString("N");
        _sessions[providerRef] = (amountMinor, currency, false, 0);
        return Task.FromResult(new ProviderSession(providerRef, $"https://pay.mock.test/{providerRef}"));
    }

    /// <summary>The customer finished on the hosted page. <paramref name="amountMinor"/> defaults to the amount asked for.</summary>
    public void SimulatePayment(string providerRef, long? amountMinor = null)
    {
        var s = _sessions[providerRef];
        _sessions[providerRef] = (s.Amount, s.Currency, true, amountMinor ?? s.Amount);
    }

    public Task<ProviderPayment> VerifyAsync(string providerRef, CancellationToken ct = default) =>
        Task.FromResult(_sessions.TryGetValue(providerRef, out var s)
            ? new ProviderPayment(s.Paid, s.Paid ? s.PaidAmount : 0, s.Currency)
            : new ProviderPayment(false, 0, "BDT"));

    public ParsedWebhook? Parse(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString();
            var reference = root.GetProperty("providerRef").GetString();
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(reference) ? null : new ParsedWebhook(id, reference);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
