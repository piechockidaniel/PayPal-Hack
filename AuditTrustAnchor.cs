using System.Security.Cryptography;
using System.Text;

namespace PayPal360;

public sealed record AuditAnchorDocument(
    string Algorithm,
    string Scope,
    int EventCount,
    string? LastEventId,
    string? LastHash,
    DateTimeOffset CreatedAt,
    string Signature);

public sealed record AuditAnchorVerification(
    bool IsConfigured,
    bool IsValid,
    int CheckedEvents,
    string? Failure);

public sealed class AuditTrustAnchor(CommerceDatabase database, IConfiguration configuration)
{
    private const string Algorithm = "HMAC-SHA256";
    private const string Scope = "commerce-audit-global";
    private const int MinimumSecretLength = 32;

    public bool IsConfigured => ReadSecret() is not null;

    public AuditAnchorDocument Create()
    {
        var secret = ReadSecret() ?? throw new InvalidOperationException(
            "Audit anchor signing is not configured. Set PAYPAL360_AUDIT_ANCHOR_SECRET outside the database.");
        var snapshot = Snapshot();
        var createdAt = DateTimeOffset.UtcNow;
        return new AuditAnchorDocument(
            Algorithm,
            Scope,
            snapshot.EventCount,
            snapshot.LastEventId,
            snapshot.LastHash,
            createdAt,
            Sign(secret, snapshot.EventCount, snapshot.LastEventId, snapshot.LastHash, createdAt));
    }

    public AuditAnchorVerification Verify(AuditAnchorDocument? document)
    {
        var secret = ReadSecret();
        if (secret is null)
            return new AuditAnchorVerification(false, false, 0, "Audit anchor signing is not configured.");
        if (document is null || document.Algorithm != Algorithm || document.Scope != Scope ||
            document.EventCount < 0 || string.IsNullOrWhiteSpace(document.Signature))
            return new AuditAnchorVerification(true, false, 0, "The audit anchor document is malformed.");

        var expectedSignature = Sign(secret, document.EventCount, document.LastEventId, document.LastHash, document.CreatedAt);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedSignature),
                Encoding.UTF8.GetBytes(document.Signature)))
            return new AuditAnchorVerification(true, false, 0, "The audit anchor signature is invalid.");

        var result = database.Read(state =>
        {
            if (document.EventCount > state.Audit.Count)
                return new AuditAnchorVerification(true, false, 0, "The anchor references more events than are present.");

            var prefix = state.Audit.Take(document.EventCount).ToArray();
            var prefixVerification = CommerceDatabase.VerifyAudit(prefix);
            if (!prefixVerification.IsValid)
                return new AuditAnchorVerification(true, false, prefixVerification.CheckedEvents, "The anchored audit prefix no longer verifies.");

            var last = prefix.LastOrDefault();
            if (!string.Equals(last?.EventId, document.LastEventId, StringComparison.Ordinal) ||
                !string.Equals(last?.Hash, document.LastHash, StringComparison.Ordinal))
                return new AuditAnchorVerification(true, false, prefixVerification.CheckedEvents, "The anchored audit prefix has changed.");

            return new AuditAnchorVerification(true, true, prefixVerification.CheckedEvents, null);
        });
        return result;
    }

    private AuditSnapshot Snapshot() => database.Read(state =>
    {
        var verification = CommerceDatabase.VerifyAudit(state.Audit);
        if (!verification.IsValid)
            throw new InvalidOperationException("Cannot create an audit checkpoint while the local audit chain is invalid.");
        var last = state.Audit.LastOrDefault();
        return new AuditSnapshot(state.Audit.Count, last?.EventId, last?.Hash);
    });

    private string? ReadSecret()
    {
        var secret = configuration["PAYPAL360_AUDIT_ANCHOR_SECRET"]?.Trim();
        return secret is { Length: >= MinimumSecretLength } ? secret : null;
    }

    private static string Sign(string secret, int eventCount, string? lastEventId, string? lastHash, DateTimeOffset createdAt)
    {
        var canonical = string.Join("\n", [
            Algorithm,
            Scope,
            eventCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lastEventId ?? "",
            lastHash ?? "",
            createdAt.ToUniversalTime().ToString("O")
        ]);
        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record AuditSnapshot(int EventCount, string? LastEventId, string? LastHash);
}
