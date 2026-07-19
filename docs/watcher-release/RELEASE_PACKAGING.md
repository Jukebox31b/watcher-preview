# Release Packaging

## Authoritative command

From the source root, run `PUBLISH-WATCHER-PREVIEW.cmd`. The command delegates
to `PUBLISH-WATCHER-PREVIEW.ps1`, the authoritative Windows x64 publisher. The
PowerShell entry point accepts `-OutputPath` for local verification.

The publisher requires a clean Git worktree or explicit no-Git source
provenance. It hashes the stable release inputs, deletes and recreates only the
resolved package directory, publishes the Watcher and intake projects, removes
symbols, copies public package documentation, and writes `manifest.json`.

The build uses compressed self-contained single-file app hosts without trimming
or NativeAOT. Native runtime libraries are bundled for extraction. Watcher
application assemblies remain explicit DLL sidecars because runtime attestation
requires stable assembly paths. The single-file analyzer is disabled for an
offline .NET SDK installation that lacks the optional ILLink task package.

## Required packaged gates

The publisher invokes the packaged main executable with offline
`--release-test <json-path>`. It then builds the existing
`source/dcs-watcher-v2-stage3-regression` project in a temporary directory and
runs that regression executable against the packaged
`DcsWatcherV2.Stage3Intake.exe`.

These are two separate gates. The **Packaged Preview offline application
suite** runs from the packaged `DcsWatcherV2.exe` and currently requires
203/203 checks. Its 17/17 `command-cancellation` subsuite is included within
that 203 total. The **Stage 3 provenance and intake regression suite** is a
separate 295/295 fixture run against the packaged intake executable. The 17
subsuite checks must never be added to either top-level total.

Both commands are bounded. The packaged release test and regression build each
have a 15-minute limit; the full Stage3 regression/fault run has a 30-minute
limit. A timeout terminates the process tree and fails publication.

The packaged application gate requires the reviewed 203-test candidate
baseline, including its exact 17-test command-cancellation subsuite, to pass
with zero failures. The Stage3 gate requires the authoritative 295-test baseline to report 295
passed and zero failed. If the suite intentionally gains or removes tests, the
publisher's expected total must be reviewed and changed to the new exact total;
publication does not accept an arbitrary nonzero count. Fault evidence must
report zero duplicate acceptances, unauthorized deliveries, silent recoveries,
and live outputs. Missing, malformed, inconsistent, or wrong-schema JSON and
nonzero process exits all fail publication.

External evidence is first written and validated in a temporary workspace.
Stale external artifacts are removed before building. Only after every gate and
the stable-input recheck pass does the publisher emit these files beside the
package directory:

- `<package-name>.release-test.json`
- `<package-name>.stage3-regression.json`
- `<package-name>.stage3-fault.json`
- `<package-name>.zip`
- `<package-name>.release-summary.json`

The summary is emitted last. It cryptographically binds each evidence file by
name, byte size, and SHA-256 and records the validated totals and all four fault
counters. It also binds the ZIP, manifest, main executable, main application
DLL, and packaged intake executable. The regression result records the SHA-256
of the packaged intake executable it tested. Failure removes any partially
emitted external files, so a failed run cannot leave a final ZIP or summary.

The evidence JSON and summary remain outside the ZIP and manifest inventory.
The ZIP contains the package directory as its top-level entry.

Run `RUN-WATCHER-PREVIEW.cmd` for the supported demo/test-sink interface. The
launcher always passes `--demo-ui`; it does not start normal operation. See
`SUPPORT_MATRIX.md` and `UNSIGNED_PREVIEW_NOTICE.md` for the tested boundary and
Windows trust warnings.

## Reproducible public-source stage

Create a candidate public source tree with:

```powershell
.\tools\watcher-release\New-PublicWatcherStage.ps1 -Destination <outside-repository-directory>
```

The destination must be outside and must not contain the source repository.
Existing destination content is removed only after those boundaries are
validated. The staged source contains the Watcher, Stage3 intake, and Stage3
regression projects; the publisher and launch scripts; both public-release
tools; the MIT license; root README; and source provenance. Build outputs,
runtime state, `.git`, private history, and private evidence artifacts are not
selected.

The staged documentation set is deliberate: privacy, release packaging,
security, support matrix, and unsigned notice. Internal product and Build Week
scope, sanitization and acceptance checklists, submission working drafts, shot
lists, and recording scripts are not public source-release inputs. Every
referenced document in this packaging guide is included in the public stage and
packaged documentation where applicable.

## Sanitization gate

Public staging invokes `Test-PublicSanitization.ps1` automatically. Any match
blocks the command, reports only a stage-relative file and line plus rule name,
and retains the disposable stage for local remediation. A retained failed stage
is not a public-source candidate.

The scanner checks filesystem identities, local account and host identifiers,
real conversation or thread identifiers, unapproved repository identities,
secrets and private-key material, incident labels, private runtime names,
internal project terms, forbidden evidence directories, unexpected files, and
reparse points. The intended public repository identity
`Jukebox31b/watcher-preview` is allowed; other concrete repository identities
remain blocked. The scanner source is the only content exemption because it
encodes the signatures it enforces. Additional project-specific terms can be
supplied with `-AdditionalBlockedTerm` during a direct scan.

Release tooling does not rewrite or conceal blocked source or documentation.
The owning source or documentation worker must remove a finding, then recreate
and rescan the stage.

## Manifest and remaining review

The manifest records the source commit, repository-wide dirty state,
deterministic hashes for all three source projects and the release inputs, .NET
SDK and bundled runtime-pack versions, target framework, runtime identifier,
single-file properties, authoritative publisher and generated sidecar-target
hashes, file sizes, and SHA-256 values. The inventory excludes `manifest.json`
itself to avoid a recursive self-hash.

In a Git checkout, any tracked or non-ignored untracked change blocks
publication. A sanitized no-Git stage uses `SOURCE_PROVENANCE.json`; the
publisher does not invent `source.dirty=false` when that provenance omits a
cleanliness result.

Before distribution, the exact clean candidate still requires a passing public
sanitization scan, package execution on the tested support-matrix host, malware
review, and an independent ZIP, manifest, evidence, and claim audit. This guide
does not assert that those final gates have occurred.

The Preview is unsigned. Publish `UNSIGNED_PREVIEW_NOTICE.md` with the download
and keep the warning visible. SHA-256 establishes byte equality only; it does
not prove publisher identity, malware-free status, or general safety.
