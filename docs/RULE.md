# Sensitive Information Protection Rules

## Core Principle

Expose the minimum information needed to complete the task. Preserve technical usefulness while removing raw secrets, personal data, and unnecessary internal detail.

## Never Disclose

- API keys, access tokens, refresh tokens, OAuth codes, bearer tokens, SAS tokens, JWTs, PATs, session IDs, cookies, CSRF tokens, or license keys.
- Passwords, passphrases, PINs, recovery codes, one-time codes, password hashes, salts, or MFA backup codes.
- Private keys, SSH keys, certificates with private material, `.pfx` passwords, signing secrets, webhook secrets, and encryption keys.
- Full database, storage, queue, email, SFTP, LDAP, VPN, or cloud connection strings.
- Full credit card numbers, bank account numbers, national identifiers, passport numbers, tax identifiers, or insurance identifiers.
- Personal contact data, customer records, employee records, health data, financial records, or precise location data unless explicitly required and safely minimized.

## Redaction Standards

- Replace sensitive values with clear placeholders:
  - `<SECRET_REDACTED>`
  - `<TOKEN_REDACTED>`
  - `<PASSWORD_REDACTED>`
  - `<PRIVATE_KEY_REDACTED>`
  - `<EMAIL_REDACTED>`
  - `<CUSTOMER_ID_001>`
- Preserve shape when useful:
  - Keep field names, file names, column names, HTTP methods, status codes, error classes, and non-sensitive stack frames.
  - Keep last 4 characters only when identity matching is needed, such as `...A1b2`.
  - Keep domains only when public or necessary; otherwise use `example.com` or `<HOST_REDACTED>`.
- Do not use real-looking fake secrets. Use obvious placeholders instead.

## Code And Config Best Practices

- Load secrets from environment variables, secret managers, managed identity, or local ignored config.
- Keep `.env`, `appsettings.*.local.json`, user secrets, generated key files, and credential caches out of source control.
- Add or verify ignore patterns for secret-bearing local files.
- Prefer least-privilege credentials and short-lived tokens.
- Avoid logging request headers, cookies, authorization values, full payloads, database URLs, or personal data.
- Mask sensitive fields in exceptions, telemetry, traces, and debug output.
- Do not commit production data as fixtures. Use synthetic data.
- Do not store secrets in comments, README examples, screenshots, notebooks, prompt files, or test snapshots.

## Review Checklist

Before sharing or saving output, check for:

- `password`, `passwd`, `pwd`, `secret`, `token`, `apikey`, `api_key`, `authorization`, `bearer`, `cookie`, `connectionString`, `client_secret`, `private_key`.
- PEM blocks such as `BEGIN PRIVATE KEY`, `BEGIN RSA PRIVATE KEY`, or `BEGIN OPENSSH PRIVATE KEY`.
- URLs containing usernames, passwords, tokens, signatures, or query-string credentials.
- JWT-like strings with three base64url segments.
- Cloud keys and tokens from AWS, Azure, Google Cloud, GitHub, GitLab, npm, Docker, Slack, Stripe, Twilio, SendGrid, OpenAI, or database vendors.
- CSV, JSON, XML, Excel, log, and Markdown files with raw customer or employee records.

## Safe Output Pattern

When sensitive data is present:

1. State that sensitive values were found and redacted.
2. Provide the redacted artifact or summary.
3. Explain any local-only steps the user must perform with their real secret.
4. Avoid repeating the original sensitive value in the explanation.

## Default Decision

When sensitivity is uncertain, treat the value as sensitive, redact it, and preserve enough context for the user to continue.
