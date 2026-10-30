# PayPal 360 VPS deployment notes

The hackathon deployment is a Sandbox-only VPS service behind the existing
OpenLiteSpeed HTTPS vhost.

## Runtime shape

- application service: `paypal360.service`
- release root: `/opt/paypal360/releases/`
- active release: `/opt/paypal360/current`
- persistent data: `/var/lib/paypal360/data`
- systemd environment file: `/etc/paypal360/paypal360.env`
- data-protection certificate: `/etc/paypal360/paypal360-data-protection.pfx`
- loopback listener: `127.0.0.1:16492`
- public URL: `https://paypal.connect-the-dots.biz`

The service user is `paypal360`. Secrets belong only in the root-owned,
group-readable systemd environment file or an equivalent secret manager; never
commit them or put them in the published archive. The deployment environment
must set `ASPNETCORE_ENVIRONMENT=Production`,
`ASPNETCORE_URLS=http://127.0.0.1:16492`,
`PayPal__PAYPAL360_WEBHOOK_HOST=paypal.connect-the-dots.biz`, and
`Web__PAYPAL360_PUBLIC_HOST=paypal.connect-the-dots.biz`.

## Release shape

1. Run the solution build, deterministic checks, integration checks and UI
   check locally.
2. Publish a framework-dependent `linux-x64` archive and record its SHA-256.
3. Upload the archive to a temporary VPS path and verify the remote SHA-256
   matches before extraction.
4. Extract into a new timestamped release, point `current` at it, and keep the
   persistent data directory outside the release.
5. Restart `paypal360.service`, verify its loopback `/api/status`, then restart
   OpenLiteSpeed only after the service is healthy.
6. Verify the public `/api/status`, public UI, public login cookie flags, and
   unsigned webhook probe (`400 Required PayPal webhook headers are missing.`).

## Rollback

Keep the previous release symlink target and the dated OpenLiteSpeed vhost
backup. Restore the previous vhost upstream and release symlink, restart the
service and OpenLiteSpeed, then repeat the public status and webhook checks.
Do not delete the persistent database during a normal rollback.
