# Security

Watcher Preview is pre-release software. Use it first in demo mode, keep actions manual, and run it with the least-privileged Windows account that can support the test.

## Threat model summary

Watcher processes local files and may observe or drive explicitly configured desktop, browser, repository, or local application interfaces. Relevant threats include malicious handoff content, replayed or substituted records, unsafe destination selection, compromised local state, secret leakage through diagnostics, tampered binaries, dependency or build-host compromise, and unexpected behavior after a private interface changes.

The Preview includes validation and provenance mechanisms, but those controls are not a security boundary against a compromised Windows account, hostile administrator, modified executable, or malicious third-party application. Experimental adapters should be assumed brittle and privileged relative to the content they can read or submit.

## Operator guidance

- Verify `manifest.json` hashes before running a package.
- Start with `--demo-ui` and do not connect live accounts during initial review.
- Keep execution manual and inspect destinations and payloads.
- Store runtime state outside source distributions and restrict access to it.
- Never put credentials, cookies, private keys, or personal exports in source or release archives.
- Stop using an adapter when its target application changes unexpectedly.
- Follow account, service, and organizational security policies; Watcher is not an authorization bypass.

## Binary trust

Preview binaries are unsigned until a release explicitly documents and verifies a signing identity. A hash proves file equality with a manifest, not publisher identity or safety. Build from a reviewed source commit when publisher authenticity is required.

## Reporting

Report suspected vulnerabilities privately through
[GitHub private vulnerability reporting](https://github.com/Jukebox31b/watcher-preview/security/advisories/new).
Include the affected version, reproduction details, impact, and any suggested
mitigation that can be shared safely. Do not put exploit details, credentials,
private data, or reusable secrets in a public issue.

Use [GitHub Issues](https://github.com/Jukebox31b/watcher-preview/issues) for
non-sensitive installation questions, demo/test-sink problems, and other
ordinary support requests. This Preview has no acknowledgement, response, fix,
or support-time SLA. Reports are handled as maintainer availability permits.
