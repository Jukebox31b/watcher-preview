# Watcher Preview Support Matrix

This matrix describes the narrow configuration evaluated for the Preview. It
does not imply support for nearby Windows versions, other editions, or live
adapter combinations.

| Area | Preview status |
|---|---|
| Operating system | Tested only on Windows 11 Pro x64, build 26200 |
| Other Windows builds, editions, and architectures | Not tested; unsupported for this Preview |
| Distribution | Self-contained Windows x64 ZIP; no installer |
| Code signing | Unsigned; Windows may show SmartScreen and Unknown Publisher warnings |
| Demo mode | Supported evaluation path using bundled synthetic fixtures |
| Delivery | Local non-actionable demo/test sink only |
| Packaged release test | Supported as an offline release-evidence gate |
| Live ChatGPT, Codex, GitHub, desktop, browser, repository, or DCS adapters | Experimental and unsupported |
| Automatic or unattended live operation | Unsupported |

"Supported" here means the release is intended to reproduce the documented
demo/test-sink workflow on the tested configuration. It is not a production
readiness, compatibility, security-certification, or response-time commitment.

Use [GitHub Issues](https://github.com/Jukebox31b/watcher-preview/issues) for
non-sensitive support. Use the private reporting link in `SECURITY.md` for a
suspected vulnerability.
