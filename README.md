# Watcher Preview

Watcher Preview is an early Windows desktop build for inspecting local agent
handoff decisions through an isolated synthetic demo and local test sink. It is
not a production service or an authority for unattended production workflows.
Preview behavior, formats, and compatibility may change.

## Judge Quick Start - No Rebuild

**Release:** [Watcher Preview v0.1.0-preview.2](https://github.com/Jukebox31b/watcher-preview/releases/tag/v0.1.0-preview.2)

**ZIP:** [WatcherPreview-win-x64.zip](https://github.com/Jukebox31b/watcher-preview/releases/download/v0.1.0-preview.2/WatcherPreview-win-x64.zip)

1. Download the self-contained Windows x64 Preview ZIP from the release above.
2. Verify the ZIP SHA-256 against
   `WatcherPreview-win-x64.release-summary.json` on the same release.
3. Extract the ZIP to a new local folder.
4. Verify the extracted file SHA-256 values against `manifest.json`.
5. Run `RUN-WATCHER-PREVIEW.cmd`. It launches only
   `DcsWatcherV2.exe --demo-ui`.
6. Run the bundled synthetic current-path fixture, replay it, and then run the
   synthetic sibling-branch fixture.
7. Confirm one local test-sink acceptance, a replay rejection with no second
   acceptance, and a sibling-branch rejection before delivery.

This judge path requires no rebuild, live account, API key, repository
connection, private data, or development toolchain.

## Verified Preview Status

| Area | Publication status |
|---|---|
| Package | Self-contained Windows x64 ZIP; unsigned Preview, not an installer |
| Tested OS | Windows 11 Pro x64 version 10.0.26200 build 26200 |
| Packaged Preview offline application suite | 203/203 passed, 0 failed, executed by the packaged `DcsWatcherV2.exe` |
| Command-cancellation subset | 17/17 passed, 0 failed; included within the 203 application checks, not additional |
| Stage 3 provenance and intake regression suite | 295/295 passed, 0 failed; separate runner exercised the packaged intake executable |
| Demo path | Isolated bundled synthetic fixtures and a non-actionable in-memory local test sink only |
| Live adapters | Experimental live adapters are unsupported, excluded from the judge path, and not part of the demo |
| Support | Use [GitHub Issues](https://github.com/Jukebox31b/watcher-preview/issues) once the public repository is available; no response-time commitment is made for this Preview |

The packaged suite result is recorded in
`WatcherPreview-win-x64.release-test.json`. The full regression result is
recorded in `WatcherPreview-win-x64.stage3-regression.json`, with fault results
in `WatcherPreview-win-x64.stage3-fault.json`; both assets are published beside
the ZIP. Both runs were made on the tested Windows 11 x64 host described above.
These passing synthetic and regression results do not establish live unattended
reliability.

> **Unsigned Preview warning:** This Preview is unsigned. Windows may show
> Microsoft Defender SmartScreen messages including **"Windows protected your
> PC"** and **"Unknown publisher"**. Verify the ZIP SHA-256 against the
> separately published release summary before extraction, then verify extracted
> files against `manifest.json`. Do not continue if a hash differs or
> organizational policy prohibits unsigned software. SHA-256 proves file
> equality only; it does not prove publisher identity or safety.

## Release Identity

`WatcherPreview-win-x64.release-summary.json` is the authoritative,
separately published release identity record. It binds the source commit and
source-tree SHA-256 to the ZIP, manifest, and packaged binary SHA-256 values;
the 203/203 packaged Preview offline application evidence; its included 17/17
command-cancellation subset; the separate 295/295 Stage 3 provenance and intake
regression evidence; the packaged intake executable SHA-256; and zero fault
counters.

The release summary is not one of the hashed inputs whose identities it
records. Publishing that record separately therefore does not recursively
change the commit, tree, ZIP, manifest, or binary identities it binds.

## Build From Source - Optional

Prerequisites are Windows 11 x64, PowerShell 5.1 or later, and the .NET 8 SDK.
From the reviewed public repository root:

```powershell
.\PUBLISH-WATCHER-PREVIEW.cmd
.\RUN-WATCHER-PREVIEW.cmd
```

The default output is `artifacts\WatcherPreview-win-x64`. The publish command
creates the extracted self-contained package, `manifest.json`, an adjacent ZIP,
and adjacent JSON release summary and packaged-test evidence. The run command
launches the isolated demo interface with `--demo-ui`.

**Public repository:** [github.com/Jukebox31b/watcher-preview](https://github.com/Jukebox31b/watcher-preview)

## Demo Safety Boundary

The Build Week demo uses only bundled synthetic fixtures and a non-actionable
local test sink. Demo composition rejects non-demo adapters. It does not contact
live ChatGPT or Codex sessions, mutate a repository, launch an external
workload, or use private data.

Normal operation is manual by default. Watcher does not provide or claim an API
key, subscription, authentication, authorization, usage-limit, safety-system,
or product-policy bypass. Public provider APIs remain more appropriate for
stable server-scale and multi-tenant integrations.

## How GPT-5.6, Codex, And Humans Built It

GPT-5.6 directed the product and safety architecture, requirement decomposition,
analysis of forensic lessons from operational incidents, and the independent
review process. Those directions became separate source, authorization,
delivery, and audit contracts plus fail-closed lineage, replay, cancellation,
and recovery requirements, so implementation work could not self-certify the
release.

Codex worker threads implemented and refactored the Windows application, built
the isolated demo, added regression and packaged-release tests, and produced the
sanitized source and Preview package. This accelerated the loop from reviewed
requirements and incident findings to bounded code changes, executable tests,
and release evidence; no time-saving metric is claimed.

The human set the goals and guardrails, reviewed operational incidents and their
forensic lessons, evaluated the resulting behavior, and authorized release
direction. GPT-5.6 and Codex were used in development and review; the isolated
demo does not call either model or a live model service at runtime.

## Preview Limitations

- Only the Windows 11 Pro x64 configuration above was tested.
- The offline synthetic demo does not establish live unattended reliability.
- Automatic modes are opt-in and require explicit local policy and trust setup.
- Experimental live adapters are unsupported and excluded from the demo.
- Desktop and browser surfaces can change and may require adapter maintenance.
- A file hash establishes equality with a manifest, not publisher identity or
  general safety.

The repository is licensed under the MIT License. Review
`docs/watcher-release/PRIVACY.md` and `docs/watcher-release/SECURITY.md` before
evaluating any non-demo feature.
