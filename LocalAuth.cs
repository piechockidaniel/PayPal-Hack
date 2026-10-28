using Microsoft.AspNetCore.Identity;

namespace PayPal360;

public record LocalUser(
    string Id,
    string Name,
    string PasswordHash,
    string TenantId = "local",
    IReadOnlyList<string>? RoleIds = null,
    bool IsAdministrator = true,
    bool IsActive = true);
public record LocalUserView(string Id, string Name, string TenantId, IReadOnlyList<string> RoleIds, bool IsAdministrator, bool IsActive);
public record LocalSession(string Id, string UserId, DateTimeOffset ExpiresAt, bool Revoked);

public sealed class LocalAuth
{
    private readonly CommerceDatabase database;
    private readonly PasswordHasher<LocalUser> hasher = new();

    public LocalAuth(CommerceDatabase database, IConfiguration configuration)
    {
        this.database = database;
        var name = (configuration["PAYPAL360_ADMIN_USERNAME"] ?? "").Trim();
        var password = configuration["PAYPAL360_ADMIN_PASSWORD"] ?? "";
        if (name.Length is < 1 or > 80 || password.Length is < 12 or > 128)
            return;

        database.Update(state =>
        {
            if (state.Users.Count != 0)
                return false;
            var user = new LocalUser(Guid.NewGuid().ToString("N"), name, "");
            user = user with
            {
                PasswordHash = hasher.HashPassword(user, password)
            };
            state.Users.Add(user);
            CommerceDatabase.Record(state, "-", "user.bootstrapped", "system", "Initial local administrator created.", "local-auth", user.Id);
            return true;
        });
    }

    public LocalSession CreateSession(LocalUser user) => database.Update(state =>
    {
        state.Sessions.RemoveAll(session => session.ExpiresAt <= DateTimeOffset.UtcNow);
        var session = new LocalSession(Guid.NewGuid().ToString("N"), user.Id, DateTimeOffset.UtcNow.AddHours(4), false);
        state.Sessions.Add(session);
        CommerceDatabase.Record(state, "-", "session.login", "user:" + user.Id, "Local administrator signed in.", "local-auth", user.Id);
        return session;
    });

    public bool IsSessionValid(string? sessionId, string? userId) =>
        !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(userId) &&
        database.Read(state => state.Sessions.Any(session => session.Id == sessionId && session.UserId == userId && !session.Revoked && session.ExpiresAt > DateTimeOffset.UtcNow));

    public void RevokeSession(string? sessionId, string? userId) => database.Update(state =>
    {
        var index = state.Sessions.FindIndex(session => session.Id == sessionId && session.UserId == userId && !session.Revoked);
        if (index >= 0)
        {
            state.Sessions[index] = state.Sessions[index] with
            {
                Revoked = true
            };
            CommerceDatabase.Record(state, "-", "session.logout", "user:" + userId, "Local administrator signed out.", "local-auth", userId);
        }
        return true;
    });
    public bool IsConfigured() => database.Read(state => state.Users.Count > 0);

    public LocalUser? FindUser(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? null : database.Read(state => state.Users.FirstOrDefault(item => item.Id == userId));

    public IReadOnlyList<LocalUserView> ListUsers() => database.Read(state => state.Users
        .Select(user => new LocalUserView(user.Id, user.Name, user.TenantId ?? "local", user.RoleIds ?? [], user.IsAdministrator, user.IsActive))
        .ToArray());

    public LocalUserView CreateUser(string name, string password, string tenantId, IEnumerable<string> roleIds, string actor, string reason)
    {
        name = (name ?? "").Trim();
        tenantId = (tenantId ?? "").Trim();
        if (name.Length is < 1 or > 80 || password.Length is < 12 or > 128)
            throw new ArgumentException("User name must contain 1 to 80 characters and password must contain 12 to 128 characters.");
        if (tenantId.Length is < 1 or > 80)
            throw new ArgumentException("Tenant ID must contain 1 to 80 characters.");
        var requestedRoles = roleIds.Distinct(StringComparer.Ordinal).ToArray();
        return database.Update(state =>
        {
            if (state.Users.Any(user => user.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A user with this name already exists.");
            var roles = state.Roles.Where(role => requestedRoles.Contains(role.Id, StringComparer.Ordinal) && role.Kind == "human").Select(role => role.Id).ToArray();
            if (roles.Length != requestedRoles.Length)
                throw new ArgumentException("Every assigned role must be an existing human role.");
            var seed = new LocalUser(Guid.NewGuid().ToString("N"), name, "", tenantId, roles, false, true);
            var user = seed with
            {
                PasswordHash = hasher.HashPassword(seed, password)
            };
            state.Users.Add(user);
            CommerceDatabase.Record(state, "-", "user.created", actor, "Created a tenant-scoped human user. Reason: " + reason, "local-auth", user.Id, null, tenantId);
            return new LocalUserView(user.Id, user.Name, tenantId, roles, false, true);
        });
    }

    public bool HasPermission(string? userId, string permission, string tenantId = "local") => database.Read(state =>
    {
        var user = state.Users.FirstOrDefault(item => item.Id == userId);
        if (user is null || !user.IsActive || !string.Equals(user.TenantId ?? "local", tenantId, StringComparison.Ordinal))
            return false;
        if (user.IsAdministrator)
            return true;
        var roleIds = user.RoleIds ?? [];
        return state.Roles.Any(role => role.Kind == "human" && roleIds.Contains(role.Id, StringComparer.Ordinal) && role.Permissions.Contains(permission, StringComparer.Ordinal));
    });

    public LocalUser? Verify(string? name, string? password)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password) || name.Length > 80 || password.Length > 128)
            return null;
        var user = database.Read(state => state.Users.FirstOrDefault(item => item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) && item.IsActive));
        if (user is null)
            return null;
        return hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed ? null : user;
    }
}


