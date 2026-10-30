# PayPal 360

A local, sandbox-only commerce prototype with PayPal checkout, AI follow-up proposals, a human approval gate and an audit trail.

## What, why and how

### What it is

PayPal 360 is a commerce-operations control plane for businesses that want to connect customer payments with the internal work that follows. It brings checkout, payment status, operational follow-ups, human approvals and an evidence trail into one workflow.

The prototype is designed for a merchant, marketplace or commerce team rather than only for a developer. A buyer can complete a PayPal Sandbox payment, while the business can see what happened, decide which follow-up actions are allowed, and review the history of those decisions.

### Why it matters

Payment completion is only the beginning of an operational process. Businesses also need to know whether an order is trustworthy, which actions may be automated, where a person must approve a decision, and what evidence exists when something needs to be explained later.

PayPal 360 addresses that gap by making automation policy-aware and auditable. It helps reduce manual coordination around routine orders without turning payment or operational decisions into an uncontrolled black box. The design also makes unfinished capabilities visible, so a team can distinguish a verified payment event from a future integration that still needs an owner, permission or provider contract.

### How the solution works

1. A buyer starts checkout with a server-validated product and price snapshot.
2. PayPal creates and captures the Sandbox payment using durable request and idempotency records.
3. PayPal sends a signed webhook to the public HTTPS endpoint; the event is verified, stored and applied idempotently.
4. The merchant dashboard shows the order state, provider reference and audit history.
5. AI or internal agents may propose follow-up work only within their assigned permissions and resource scope.
6. Business policy determines whether a follow-up runs automatically, waits for human approval, is handled manually or is blocked.
7. The system records the actor, decision, outcome and relevant provider reference so the workflow can be reviewed later.

### Business value demonstrated

- A single view from payment creation through capture and reconciliation.
- Safer automation through explicit roles, scopes, approval gates and fail-closed behavior.
- Fewer duplicate or ambiguous operations through request keys, provider idempotency and restart-safe reconciliation.
- Faster investigation through an audit trail that links buyer, agent, merchant and provider activity.
- A clear foundation for future connectors such as fulfillment and Google Merchant Center, with shared provider-action contract types for idempotency, provider references and reconciliation. The approved PayPal refund path is implemented with human approval, full/partial amounts and reconciliation; fulfillment and publication remain explicitly unavailable until their contracts and owners are approved.

This repository currently demonstrates the workflow with PayPal Sandbox and local prototype infrastructure. The configured Sandbox create, approval, capture, signed webhook delivery and reconciliation path was verified on 2026-10-29. It is a foundation for a production commerce platform, not a claim that production deployment, independent audit retention or every downstream provider action is already finished.

## Run

Requires the .NET 10 SDK. Configure the local administrator before the **first** launch:

Use .NET User Secrets for local development; no `.env` file is required or
loaded by the application. The project already has a User Secrets ID. Set
only the values you use, from the repository root, for example:

```powershell
dotnet user-secrets set "LocalAuth:PAYPAL360_ADMIN_USERNAME" "<local administrator username>"
dotnet user-secrets set "LocalAuth:PAYPAL360_ADMIN_PASSWORD" "<unique password of at least 12 characters>"
dotnet user-secrets set "PayPal:PAYPAL_CLIENT_ID" "<PayPal sandbox client ID>"
dotnet user-secrets set "PayPal:PAYPAL_CLIENT_SECRET" "<PayPal sandbox client secret>"
dotnet user-secrets set "PayPal:PAYPAL_WEBHOOK_ID" "<PayPal sandbox webhook ID>"
dotnet user-secrets set "PayPal:PAYPAL360_WEBHOOK_HOST" "<public webhook hostname>"
dotnet user-secrets set "AgentAI:OPENAI_API_KEY" "<OpenAI API key>"
dotnet user-secrets set "AgentAI:OPENAI_MODEL" "<Responses API compatible model>"
dotnet user-secrets set "GoogleMerchant:GMC_MERCHANT_ACCOUNT_ID" "<Merchant Center account ID>"
dotnet user-secrets set "GoogleMerchant:GMC_SERVICE_ACCOUNT_JSON" "<external JSON key path>"
dotnet user-secrets set "GoogleMerchant:GMC_TENANT_ID" "local"
dotnet user-secrets set "Audit:PAYPAL360_AUDIT_ANCHOR_SECRET" "<external secret, at least 32 characters>"
dotnet run --urls http://127.0.0.1:5080
```

`appsettings.json` documents the supported sections and keys with safe
`ChangeMe` placeholders. The application treats those placeholders as
unconfigured. In Development, matching User Secrets take priority over this
structure; never replace the placeholders with real credentials in
`appsettings.json`.

To create the optional local data-protection certificate and configure its
User Secrets automatically, run:

```powershell
.\tools\New-PayPal360DataProtectionCertificate.ps1
```

The script stores the password-protected certificate under the user's local
application-data directory, outside the repository. This certificate is for
local ASP.NET data-protection keys only; it is not a PayPal certificate or a
webhook/TLS certificate.

To make one real, billable OpenAI request using the configured User Secrets,
run the opt-in smoke test. It uses a synthetic paid order and does not call
PayPal or write application data:

```powershell
dotnet run --project checks -- --live-openai
```

Open http://127.0.0.1:5080 and sign in. PayPal and AI credentials are optional for startup, but required for their respective actions. The PayPal endpoint is fixed to the sandbox. The application rejects requests from non-loopback clients and non-local host names, except for `/api/webhooks/paypal` when `PAYPAL360_WEBHOOK_HOST` exactly matches the request host and the request still arrives over loopback from a trusted reverse proxy or tunnel.

The `5080` command is the minimal local-only mode. For the verified recording flow, use the HTTPS UI and the separate local webhook listener instead:

```powershell
dotnet run --no-build --urls "https://localhost:64490;http://localhost:64491"
```

Open `https://localhost:64490`; the reverse tunnel forwards only the PayPal webhook to `http://localhost:64491`. The complete recording sequence and preflight are in `DEMO-RECORDING.md`.

In Development, the application reads the grouped User Secrets above first. Flat environment names remain supported as a deployment fallback, and environment names using the `Section__Key` convention are also supported. The first valid administrator credentials initialize one account in the local database; the password is stored as a hash. Subsequent configuration changes do not replace that account. An administrator can create additional tenant-scoped human accounts through the authenticated `/api/users` endpoint. Local usernames are globally unique because login identifies an account by username without a separate tenant selector. Password rotation and recovery are still outside this sandbox.

For the verified Sandbox webhook path, set `PAYPAL360_WEBHOOK_HOST=paypal.connect-the-dots.biz`. PayPal sends to `https://paypal.connect-the-dots.biz/api/webhooks/paypal`; OpenLiteSpeed terminates HTTPS and forwards VPS loopback port `16491` through the certificate-authenticated reverse tunnel to the local HTTP listener on port `64491`. The local UI and merchant APIs remain loopback-only. The per-user supervisor and install/remove procedures are in `tools/webhook-tunnel/`; the VPS certificate renewal remains the operator's responsibility.

The optional GMC connector uses the Google Merchant API `accounts.products.list`
operation through a dedicated service-account JSON file held outside the
repository. It is read-only and exposed at `/api/connectors/gmc/products` for
authenticated catalog readers. GMC is a discovery channel only: local PayPal
360 products, prices, inventory, orders and tenant ownership remain the source
of truth, and no GMC write operation is registered. The connector is marked
`configured-not-verified` until an owned account is configured and tested.

## Data and audit

State is stored in `data/paypal360.db`, which Git ignores. Orders, proposals, internal tasks, roles, agent definitions, checkout attempts, users, sessions, webhook inbox records, action requests and audit events survive restart. Approving a proposal commits its decision, follow-up task and audit entries in one SQLite transaction. Existing `data/audit.ndjson` entries, if present, are imported once and the original file is retained.

PayPal requests are external operations and cannot share that database transaction. Checkout attempts preserve their tenant, request key, immutable product/price snapshot and provider status before contacting PayPal. An unfinished attempt is retried with the same key only within a conservative five-hour window; `POST /api/checkout/reconcile/{requestId}` can ask the provider for authoritative status and link the result after a response-loss or restart. Capture intent is also persisted before the provider call and repeated capture uses PayPal's stable idempotency key. `POST /api/webhooks/paypal` verifies the signed event with PayPal's verification API when `PAYPAL_WEBHOOK_ID` is configured, persists the raw payload in a durable inbox, and applies provider events idempotently with bounded retries and a dead-letter state. The configured public Sandbox route and one-time signed capture-event application were verified on 2026-10-29; independent audit retention and production operations remain gates. Buyer checkout and capture are bound to a short-lived HttpOnly checkout-session cookie; merchant APIs remain separately authenticated. The browser retains the request key in session storage until capture succeeds. Merchant or agent action requests are persisted through `POST /api/actions/{action}` and can require a human decision at `POST /api/actions/{id}/decision`; approved `refund_payment` requests execute full or partial PayPal capture refunds through a stable idempotency key, count completed full refunds against the remaining balance, and can be reconciled with `POST /api/actions/{id}/reconcile`. Reconciliation rationale is retained on the action request and audit event. Refunds cannot be switched to automatic policy mode; fulfillment and publication remain explicitly unavailable. PayPal checkout-approval webhooks are intentionally accepted and ignored because the capture/reconciliation path is authoritative for this prototype.

## Identity and agent permissions

Merchant APIs require an authenticated human session or an authenticated agent bearer credential. Passwords are verified against a hash; the session cookie is HttpOnly and SameSite Strict, browser mutations require an antiforgery token and same-site origin, and login is rate-limited. Logout revokes the stored session. Agent tokens are stored only as hashes and can be replaced by an administrator.

Human roles, tenant-scoped users, agent identities, parent delegation and optional order resource scopes are enforced server-side. AI order review requires the selected agent to have both `read_orders` and `propose_followup`; effective permissions and resource scopes are intersected with parent delegation. Authorization failures are recorded with the verified actor or `unauthenticated` identity and request method/path, without request bodies or credentials. The administrator can configure the internal-follow-up policy as automatic, approval required, human-only or blocked. A policy change invalidates older pending approvals. Human-only blocks agent execution and exposes a separate authenticated manual-task endpoint. The action gateway also enforces action permissions and human approval. PayPal refunds are human-only through the approved provider handler; fulfillment and publication, account lifecycle management and production identity providers remain unfinished.

## Prototype limitations

This is not a production-ready store. SQLite stores the prototype state as a single serialized record; it is not designed for high volume or production multi-tenant operation. Local data-protection keys are stored under `data/keys`; configure `DataProtection:PAYPAL360_DATA_PROTECTION_CERTIFICATE_PATH` and `DataProtection:PAYPAL360_DATA_PROTECTION_CERTIFICATE_PASSWORD` in User Secrets (or use their flat deployment fallback names) to encrypt them with an X.509 certificate, otherwise the local sandbox emits the framework warning about unencrypted key storage. Audit events have a locally verifiable SHA-256 hash chain and `/api/audit/verify`; an optional `Audit:PAYPAL360_AUDIT_ANCHOR_SECRET` checkpoint is available, but there is no independently managed archive, retention policy, secret rotation/recovery process, or tamper-proof production archive. There is no password rotation, stock reservation, tax or shipping calculation, merchant onboarding or production access. The webhook route is externally reachable only through the configured reverse tunnel; do not expose the UI or use live accounts.

The connector registry starts unverified until an administrator uses the in-app Verify control; verification results are persisted and shown in the GUI. PayPal verification performs OAuth without creating an order, while GMC verification performs its bounded read-only catalog probe. An administrator can download the complete audit chain from `GET /api/audit/export` as NDJSON. If the User Secret `Audit:PAYPAL360_AUDIT_ANCHOR_SECRET` (or its flat deployment fallback) is configured outside the database, `GET /api/audit/anchor` returns a signed checkpoint and `POST /api/audit/anchor/verify` verifies it; the export and returned artifact must be archived independently and the application never deletes audit history automatically. The PayPal Sandbox flow, connector verification controls and approved refund handler are covered by deterministic checks; the configured GMC connector remains read-only. See `DEMO-RECORDING.md`, `ROADMAP.md` and `NEXT-PHASE-PLAN.md` for the recording flow and remaining work.

## Checks and formatting

```powershell
dotnet build PayPal360.slnx
dotnet run --project checks/Checks.csproj
dotnet run --project integration-checks/IntegrationChecks.csproj
node checks/ui-check.cjs
.\Format-Solution.ps1 -Check
```

Formatting conventions are defined in `.editorconfig`. The formatter handles C# source in this repository and skips generated directories. Web files have editor conventions but are not automatically reformatted by the script.

The dashboard, order details and approval dialog follow the reference images in the `design` folder. The Workflows builder, production governance dashboard, and Settings screens shown in some mockups are reference-only and are not implemented in this prototype. Design caveats are recorded in `design/REVIEW.md`.
