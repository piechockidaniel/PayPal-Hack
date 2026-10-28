# PayPal 360

An early local, sandbox-only prototype for a provider-independent commerce platform with an approval gate and audit trail.

## Run

Requires .NET 10. In PowerShell set environment variables for this session:

```powershell
$env:PAYPAL_CLIENT_ID = '<sandbox client ID>'
$env:PAYPAL_CLIENT_SECRET = '<sandbox client secret>'
$env:OPENAI_API_KEY = '<your OpenAI API key>'
$env:OPENAI_MODEL = '<a Responses API compatible model>'
$env:PAYPAL360_OPERATOR_TOKEN = '<a long random local demo token>'
dotnet run --urls http://127.0.0.1:5080
```

Open http://127.0.0.1:5080. The catalog and capability list work without credentials. Checkout requires PayPal sandbox credentials; AI review requires OpenAI credentials and a model. Human approval requires the operator token. The PayPal API URL is fixed to the sandbox.

Checkout creates a sandbox order on the server using product prices held on the server. The buyer approves in PayPal and the server captures the order. A model may suggest an internal follow-up on a paid order; the deterministic policy gate requires human approval before creating that follow-up. No customer messages, refunds or other real-world actions are sent.

The audit trail is written to `data/audit.ndjson`, and can be viewed in the interface. Orders and approval state are in memory and reset when the application stops. Audit events do not include credentials or full model prompts.

## Prototype limitations

This is a local single-operator demonstration, not a production-ready store. The operator token is a shared secret and the name entered is self-reported: it does not establish individual identity or roles. The audit file is append-only by application convention; it is neither tamper-proof nor a transactionally consistent ledger. There is no durable order/approval database, stock reservation, webhook verification, tax/shipping calculation, merchant onboarding, access control or multi-tenancy. Do not expose it publicly or use live accounts.

The connector registry labels unimplemented PayPal and Google Merchant features unverified; it does not probe account permissions. The existing Google Merchant workflow code is a design reference, not yet integrated.

