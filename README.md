# PayPal 360

A local, sandbox-only commerce prototype with PayPal checkout, AI follow-up proposals, a human approval gate and an audit trail.

## Run

Requires the .NET 10 SDK. Configure the local administrator before the **first** launch:

```powershell
$env:PAYPAL360_ADMIN_USERNAME = '<local administrator username>'
$env:PAYPAL360_ADMIN_PASSWORD = '<unique password of at least 12 characters>'
$env:PAYPAL_CLIENT_ID = '<PayPal sandbox client ID>'
$env:PAYPAL_CLIENT_SECRET = '<PayPal sandbox client secret>'
$env:OPENAI_API_KEY = '<OpenAI API key>'
$env:OPENAI_MODEL = '<Responses API compatible model>'
$env:PAYPAL360_AUDIT_ANCHOR_SECRET = '<external secret, at least 32 characters>'
dotnet run --urls http://127.0.0.1:5080
```

Open http://127.0.0.1:5080 and sign in. PayPal and AI credentials are optional for startup, but required for their respective actions. The PayPal endpoint is fixed to the sandbox. The application rejects requests from non-loopback clients or non-local host names.

The first valid administrator credentials initialize one account in the local database; the password is stored as a hash. Subsequent environment changes do not replace that account. An administrator can create additional tenant-scoped human accounts through the authenticated `/api/users` endpoint. Local usernames are globally unique because login identifies an account by username without a separate tenant selector. Password rotation and recovery are still outside this sandbox. The application reads environment variables; it does not automatically load an `.env` file.

## Data and audit

State is stored in `data/paypal360.db`, which Git ignores. Orders, proposals, internal tasks, roles, agent definitions, checkout attempts, users, sessions, webhook inbox records, action requests and audit events survive restart. Approving a proposal commits its decision, follow-up task and audit entries in one SQLite transaction. Existing `data/audit.ndjson` entries, if present, are imported once and the original file is retained.

PayPal requests are external operations and cannot share that database transaction. Checkout attempts preserve their tenant, request key, immutable product/price snapshot and provider status before contacting PayPal. An unfinished attempt is retried with the same key only within a conservative five-hour window; `POST /api/checkout/reconcile/{requestId}` can ask the provider for authoritative status and link the result after a response-loss or restart. Capture intent is also persisted before the provider call and repeated capture uses PayPal's stable idempotency key. `POST /api/webhooks/paypal` verifies the signed event with PayPal's verification API when `PAYPAL_WEBHOOK_ID` is configured, persists the raw payload in a durable inbox, and applies provider events idempotently with bounded retries and a dead-letter state. Public HTTPS delivery, durable retention/operations policy and live sandbox proof remain deployment gates. Buyer checkout and capture are bound to a short-lived HttpOnly checkout-session cookie; merchant APIs remain separately authenticated. The browser retains the request key in session storage until capture succeeds. Merchant or agent action requests are persisted through `POST /api/actions/{action}` and can require a human decision at `POST /api/actions/{id}/decision`; `capture_payment` executes through the provider handler, while unsupported refund, fulfillment and publication actions return an explicit unavailable result.

## Identity and agent permissions

Merchant APIs require an authenticated human session or an authenticated agent bearer credential. Passwords are verified against a hash; the session cookie is HttpOnly and SameSite Strict, browser mutations require an antiforgery token and same-site origin, and login is rate-limited. Logout revokes the stored session. Agent tokens are stored only as hashes and can be replaced by an administrator.

Human roles, tenant-scoped users, agent identities, parent delegation and optional order resource scopes are enforced server-side. AI order review requires the selected agent to have both `read_orders` and `propose_followup`; effective permissions and resource scopes are intersected with parent delegation. Authorization failures are recorded with the verified actor or `unauthenticated` identity and request method/path, without request bodies or credentials. The administrator can configure the internal-follow-up policy as automatic, approval required, human-only or blocked. A policy change invalidates older pending approvals. Human-only blocks agent execution and exposes a separate authenticated manual-task endpoint. The action gateway also enforces action permissions and human approval, but provider handlers for refund, fulfillment and publication, account lifecycle management and production identity providers remain unfinished.

## Prototype limitations

This is not a production-ready store. SQLite stores the prototype state as a single serialized record; it is not designed for high volume or production multi-tenant operation. Local data-protection keys are stored under `data/keys`; configure `PAYPAL360_DATA_PROTECTION_CERTIFICATE_PATH` and its local secret password to encrypt them with an X.509 certificate, otherwise the local sandbox emits the framework warning about unencrypted key storage. Audit events have a locally verifiable SHA-256 hash chain and `/api/audit/verify`; an optional external-secret checkpoint is available, but there is no independently managed archive, retention policy, secret rotation/recovery process, or tamper-proof production archive. There is no password rotation, stock reservation, webhook delivery proof, tax or shipping calculation, merchant onboarding or production access. Do not expose it publicly or use live accounts.

The connector registry labels unimplemented PayPal and Google Merchant capabilities unverified; it does not probe account permissions. An administrator can download the complete audit chain from `GET /api/audit/export` as NDJSON. If `PAYPAL360_AUDIT_ANCHOR_SECRET` is configured outside the database, `GET /api/audit/anchor` returns a signed checkpoint and `POST /api/audit/anchor/verify` verifies it; the export and returned artifact must be archived independently and the application never deletes audit history automatically. Real PayPal and AI service calls have not been verified in this environment. See `ROADMAP.md` for remaining work.

## Checks and formatting

```powershell
dotnet build PayPal360.slnx
dotnet run --project checks/Checks.csproj
dotnet run --project integration-checks/IntegrationChecks.csproj
node checks/ui-check.cjs
.\Format-Solution.ps1 -Check
```

Formatting conventions are defined in `.editorconfig`. The formatter handles C# source in this repository and skips generated directories. Web files have editor conventions but are not automatically reformatted by the script.

The dashboard, order details and approval dialog follow the reference images in the `design` folder. Design caveats are recorded in `design/REVIEW.md`.
