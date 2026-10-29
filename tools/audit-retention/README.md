# Audit retention gate

The application can export its complete local hash-linked audit stream and can
create or verify an externally signed checkpoint. The following procedure is
deliberately a runbook, not a production-retention claim: an archive owner,
external storage location, retention period, encryption, access policy,
rotation, recovery and restore approval must be assigned before the gate is
closed.

## Evidence to collect

1. As an authenticated administrator, download `GET /api/audit/export` and
   save the NDJSON outside the application data directory.
2. Download `GET /api/audit/anchor` while
   `PAYPAL360_AUDIT_ANCHOR_SECRET` is held outside the database.
3. Store the export and checkpoint together in the owner-approved archive;
   never commit either artifact or the signing secret to this repository.
4. Verify the checkpoint with `POST /api/audit/anchor/verify` before and after
   restore.
5. Make a copy of the archived NDJSON, alter one event, and confirm checkpoint
   verification fails. Restore the untouched copy, append a new local audit
   event, export again, and verify the restored prefix plus the new tail.

## Gate status

The application-side export, hash-chain verification, checkpoint signing,
tamper detection and post-append behavior are covered by the `checks` and
`integration-checks` projects. Independent retention is still open until the
archive owner and external storage/recovery controls are recorded.
