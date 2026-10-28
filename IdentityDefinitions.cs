using System.Security.Cryptography;
using System.Text;

namespace PayPal360;

public record RoleDefinition(string Id, string Name, string Kind, IReadOnlyList<string> Permissions, string TenantId = "local");
public record AgentDefinition(string Id, string Name, string RoleId, string? ParentId, IReadOnlyList<string> EffectivePermissions, string TenantId = "local", IReadOnlyList<string>? ResourceScope = null);
public record AgentCredential(string AgentId, string TokenHash, DateTimeOffset CreatedAt, bool Revoked = false);
public record AgentCredentialResult(AgentDefinition Agent, string Token);

public sealed class IdentityDefinitions(CommerceDatabase database)
{
    private static readonly HashSet<string> AgentPermissions = ["read_orders", "read_catalog", "propose_followup", "capture_payment", "refund_payment", "fulfill_order", "publish_product"];
    private static readonly HashSet<string> HumanPermissions = ["read_orders", "read_catalog", "read_audit", "review_proposals", "approve_proposals", "manage_policies", "manage_users", "manage_definitions", "execute_actions"];

    public IReadOnlyList<RoleDefinition> ListRoles(string tenantId = "local") => database.Read(state => (IReadOnlyList<RoleDefinition>)state.Roles.Where(role => string.Equals(role.TenantId ?? "local", tenantId, StringComparison.Ordinal)).ToArray());
    public IReadOnlyList<AgentDefinition> ListAgents(string tenantId = "local") => database.Read(state => (IReadOnlyList<AgentDefinition>)state.Agents.Where(agent => string.Equals(agent.TenantId ?? "local", tenantId, StringComparison.Ordinal)).ToArray());

    public AgentDefinition? FindAgent(string? id) => string.IsNullOrWhiteSpace(id) ? null : database.Read(state => state.Agents.FirstOrDefault(agent => agent.Id == id));

    public bool AgentHasPermission(string? id, string permission, string tenantId = "local") => database.Read(state =>
    {
        var agent = state.Agents.FirstOrDefault(item => item.Id == id && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        return agent is not null && agent.EffectivePermissions.Contains(permission, StringComparer.Ordinal);
    });

    public bool AgentCanAccessResource(string? id, string resourceId, string tenantId = "local") => database.Read(state =>
    {
        var agent = state.Agents.FirstOrDefault(item => item.Id == id && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal));
        var scope = agent?.ResourceScope ?? [];
        return agent is not null && (scope.Count == 0 || scope.Contains(resourceId, StringComparer.Ordinal));
    });

    public RoleDefinition CreateRole(string name, string kind, IEnumerable<string> requested, string actor, string reason, string tenantId = "local")
    {
        name = ValidateName(name);
        if (kind is not ("human" or "agent"))
            throw new ArgumentException("Role type must be human or agent.");
        var allowed = kind == "agent" ? AgentPermissions : HumanPermissions;
        var permissions = requested.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (permissions.Length == 0 || permissions.Any(p => !allowed.Contains(p)))
            throw new ArgumentException("Select valid permissions for the role.");
        return database.Update(state =>
        {
            if (state.Roles.Count >= 100)
                throw new ArgumentException("Role limit reached.");
            if (state.Roles.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && string.Equals(r.TenantId ?? "local", tenantId, StringComparison.Ordinal)))
                throw new ArgumentException("A role with this name already exists.");
            var role = new RoleDefinition(Guid.NewGuid().ToString("N"), name, kind, permissions, tenantId);
            state.Roles.Add(role);
            CommerceDatabase.Record(state, "-", "role.created", actor, "Created " + role.Kind + " role " + role.Name + "; permissions: " + string.Join(", ", permissions) + ". Reason: " + reason, "local-definition", role.Id);
            return role;
        });
    }

    public AgentDefinition CreateAgent(string name, string roleId, string? parentId, string actor, string reason, string tenantId = "local", IReadOnlyList<string>? resourceScope = null)
    {
        name = ValidateName(name);
        return database.Update(state =>
        {
            if (state.Agents.Count >= 100)
                throw new ArgumentException("Agent limit reached.");
            if (state.Agents.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An agent with this name already exists.");
            var role = state.Roles.FirstOrDefault(r => r.Id == roleId && r.Kind == "agent" && string.Equals(r.TenantId ?? "local", tenantId, StringComparison.Ordinal)) ?? throw new ArgumentException("Choose an existing agent role.");
            AgentDefinition? parent = null;
            if (!string.IsNullOrEmpty(parentId))
                parent = state.Agents.FirstOrDefault(a => a.Id == parentId && string.Equals(a.TenantId ?? "local", tenantId, StringComparison.Ordinal)) ?? throw new ArgumentException("Parent agent not found.");
            var effective = role.Permissions.Where(p => parent is null || parent.EffectivePermissions.Contains(p, StringComparer.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var requestedScope = (resourceScope ?? []).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var inheritedScope = parent?.ResourceScope ?? [];
            var effectiveScope = inheritedScope.Count == 0
                ? requestedScope
                : requestedScope.Length == 0
                    ? inheritedScope.ToArray()
                    : requestedScope.Intersect(inheritedScope, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var agent = new AgentDefinition(Guid.NewGuid().ToString("N"), name, role.Id, parent?.Id, effective, tenantId, effectiveScope);
            state.Agents.Add(agent);
            CommerceDatabase.Record(state, "-", "agent.created", actor, "Created agent " + agent.Name + (agent.ParentId is null ? " as a root agent" : " with parent " + agent.ParentId) + "; role: " + agent.RoleId + "; inherited permissions: " + string.Join(", ", effective) + ". Reason: " + reason, "local-definition", agent.Id);
            return agent;
        });
    }

    public AgentCredentialResult IssueCredential(string agentId, string actor, string reason, string tenantId = "local")
    {
        return database.Update(state =>
        {
            var agent = state.Agents.FirstOrDefault(item => item.Id == agentId && string.Equals(item.TenantId ?? "local", tenantId, StringComparison.Ordinal))
                ?? throw new ArgumentException("Agent not found.");
            var token = "pp360_agent_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            state.AgentCredentials.RemoveAll(item => item.AgentId == agentId && !item.Revoked);
            state.AgentCredentials.Add(new AgentCredential(agentId, TokenHash(token), DateTimeOffset.UtcNow));
            CommerceDatabase.Record(state, "-", "agent.credential_issued", actor, "Issued a replacement credential for agent " + agent.Name + ". Reason: " + reason, "local-auth", agent.Id, null, tenantId);
            return new AgentCredentialResult(agent, token);
        });
    }

    public AgentDefinition? Authenticate(string token) => database.Read(state =>
    {
        var hash = TokenHash(token);
        var credential = state.AgentCredentials.LastOrDefault(item => !item.Revoked && item.TokenHash == hash);
        return credential is null ? null : state.Agents.FirstOrDefault(agent => agent.Id == credential.AgentId);
    });

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string ValidateName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is < 1 or > 80)
            throw new ArgumentException("Name must contain 1 to 80 characters.");
        return trimmed;
    }
}

