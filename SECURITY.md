# Security reports

Please use the repository's **Security → Report a vulnerability** feature for
security issues when private vulnerability reporting is enabled. Do not open a
public issue containing exploit details, credentials or private session logs.
If the private reporting button is unavailable, open an issue asking the
maintainers to enable it, without disclosing the vulnerability.

Include the application version, Windows version, impact, and a minimal synthetic
reproduction. Never attach `auth.json`, access tokens, real conversations or an
entire Codex data directory.

Only the latest release is maintained. The application has no automatic updater;
download a new release to receive fixes, including updates to its bundled .NET
runtime. Checksums verify file integrity, not publisher identity. Initial builds
are not Authenticode-signed.

Maintainer publication checklist: enable private vulnerability reporting, secret
scanning/push protection where available, and require the Windows checks workflow
before merging. Repository settings cannot be configured before a remote exists.
