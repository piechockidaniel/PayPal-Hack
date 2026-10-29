# PayPal 360 webhook tunnel

The local application listens on HTTP port `64491`. The production OpenLiteSpeed
vhost forwards `paypal.connect-the-dots.biz` to VPS loopback port `16491`, and
the certificate-authenticated SSH reverse tunnel carries that port to the local
application.

Install the per-user supervisor from PowerShell. It creates a Startup-folder
launcher, so administrator rights are not required:

```powershell
pwsh -NoProfile -File .\tools\webhook-tunnel\Install-PayPalWebhookTunnelTask.ps1
```

Remove it with:

```powershell
pwsh -NoProfile -File .\tools\webhook-tunnel\Remove-PayPalWebhookTunnelTask.ps1
```

The supervisor waits for the local app, verifies the public webhook route through
the tunnel, starts at the next user logon, restarts `plink` after a disconnect,
and stops forwarding while the app is unavailable. Logs are written outside
the repository under `%LOCALAPPDATA%\PayPal360\tunnel`.

The task uses the current Windows user and the existing local certificate file;
it does not copy or store the private key. Verify the local key ACL before
installing the task. The public UI remains local-only; only the configured
PayPal webhook host is allowed through the application host guard.

Operational checks:

```powershell
icacls "$env:USERPROFILE\.ssh\connect-the-dots.ppk"
curl.exe --max-time 10 -k -i -X POST -H "Content-Type: application/json" --data "{}" https://paypal.connect-the-dots.biz/api/webhooks/paypal
Get-Content "$env:LOCALAPPDATA\PayPal360\tunnel\supervisor.log" -Tail 40
```

The expected unsigned probe response is HTTP `400`; it proves the public
route reached the application without claiming webhook authentication. To
roll back, run the remove script and stop the local app. On the VPS,
`paypal.connect-the-dots.biz` is owned by the OpenLiteSpeed/Certbot operator;
the certificate files are under `/etc/letsencrypt/live/paypal.connect-the-dots.biz/`
and renewal must be checked there before expiry.
