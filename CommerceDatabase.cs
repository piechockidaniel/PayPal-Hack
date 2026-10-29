using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PayPal360.Contracts;

namespace PayPal360;

public sealed class CommerceState
{
    public bool LegacyAuditImported
    {
        get; set;
    }
    public List<LocalUser> Users { get; set; } = [];
    public List<LocalSession> Sessions { get; set; } = [];
    public List<Order> Orders { get; set; } = [];
    public List<Proposal> Proposals { get; set; } = [];
    public List<FollowUpTask> Tasks { get; set; } = [];
    public List<AuditEvent> Audit { get; set; } = [];
    public List<PolicyRule> Policies { get; set; } = [];
    public List<RoleDefinition> Roles { get; set; } = [];
    public List<AgentDefinition> Agents { get; set; } = [];
    public List<AgentCredential> AgentCredentials { get; set; } = [];
    public List<CheckoutAttempt> CheckoutAttempts { get; set; } = [];
    public List<ProviderEvent> ProviderEvents { get; set; } = [];
    public List<PayPalWebhookInboxItem> WebhookInbox { get; set; } = [];
    public List<ActionRequest> ActionRequests { get; set; } = [];
    public List<ConnectorVerification> ConnectorVerifications { get; set; } = [];
}

public sealed class CommerceDatabase
{
    private readonly string connectionString;

    public CommerceDatabase(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "paypal360.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS commerce_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                payload TEXT NOT NULL
            );
            INSERT OR IGNORE INTO commerce_state(id, payload) VALUES (1, '{}');
            """;
        command.ExecuteNonQuery();
        ImportLegacyAudit(dataDirectory);
    }

    private void ImportLegacyAudit(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "audit.ndjson");
        if (!File.Exists(path))
            return;
        Update(state =>
        {
            if (state.LegacyAuditImported)
                return false;
            foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                var entry = JsonSerializer.Deserialize<AuditEvent>(line)
                    ?? throw new InvalidDataException("The legacy audit file contains an invalid entry.");
                state.Audit.Add(entry);
            }
            state.LegacyAuditImported = true;
            return true;
        });
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static CommerceState Load(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM commerce_state WHERE id = 1";
        return JsonSerializer.Deserialize<CommerceState>((string)command.ExecuteScalar()!)
            ?? throw new InvalidDataException("The commerce state is invalid.");
    }

    public T Read<T>(Func<CommerceState, T> read)
    {
        using var connection = Open();
        return read(Load(connection, null));
    }

    public T Update<T>(Func<CommerceState, T> update)
    {
        using var connection = Open();
        // Acquire SQLite's write lock before loading the serialized state. This
        // prevents two application instances from both accepting the same
        // checkout key based on the same stale snapshot.
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var state = Load(connection, transaction);
        var result = update(state);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE commerce_state SET payload = $payload WHERE id = 1";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
        transaction.Commit();
        return result;
    }

    public static void Record(CommerceState state, string orderId, string type, string actor, string reason, string source, string? reference, string? policyVersion = null)
    {
        Record(state, orderId, type, actor, reason, source, reference, policyVersion, "local");
    }

    public static void Record(CommerceState state, string orderId, string type, string actor, string reason, string source, string? reference, string? policyVersion, string tenantId)
    {
        var previousHash = state.Audit.LastOrDefault()?.Hash;
        var entry = new AuditEvent(
            DateTimeOffset.UtcNow,
            orderId,
            type,
            actor,
            reason,
            source,
            reference,
            Guid.NewGuid().ToString("N"),
            policyVersion,
            previousHash,
            null,
            string.IsNullOrWhiteSpace(tenantId) ? "local" : tenantId);
        state.Audit.Add(entry with
        {
            Hash = Hash(entry)
        });
    }

    public static AuditVerification VerifyAudit(IEnumerable<AuditEvent> events)
    {
        string? previousHash = null;
        var checkedEvents = 0;
        var hasLegacy = false;
        // The serialized list order is the append order and is authoritative;
        // timestamps can legitimately share the same tick.
        foreach (var entry in events)
        {
            if (string.IsNullOrWhiteSpace(entry.EventId) || string.IsNullOrWhiteSpace(entry.Hash))
            {
                hasLegacy = true;
                continue;
            }

            if (!string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal) ||
                !string.Equals(entry.Hash, Hash(entry with
                {
                    Hash = null
                }), StringComparison.Ordinal))
                return new AuditVerification(false, checkedEvents, hasLegacy, entry.EventId, "Audit hash chain verification failed.");

            previousHash = entry.Hash;
            checkedEvents++;
        }
        return new AuditVerification(!hasLegacy, checkedEvents, hasLegacy, hasLegacy ? "legacy" : null,
            hasLegacy ? "One or more imported legacy events lack integrity metadata." : null);
    }

    private static string Hash(AuditEvent entry)
    {
        var canonical = string.Join("\n", [
            entry.EventId ?? "",
            entry.PreviousHash ?? "",
            entry.At.ToUniversalTime().ToString("O"),
            entry.TenantId,
            entry.OrderId,
            entry.Event,
            entry.Actor,
            entry.Reason,
            entry.Source,
            entry.Reference ?? "",
            entry.PolicyVersion ?? ""
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public record AuditVerification(bool IsValid, int CheckedEvents, bool HasLegacyEvents, string? FailedEventId, string? Failure);






