# Privacy

Watcher Preview is designed as a local desktop application. The project does not operate a hosted Watcher service, but an enabled adapter may interact with third-party applications or services selected by the operator.

## Local data

Depending on configuration, Watcher may store settings, workflow state, event history, validation records, diagnostic logs, and limited content needed to complete a handoff. That material can contain repository paths, account or conversation references, prompts, report metadata, and operational timestamps.

Local data remains the operator's responsibility. Use a dedicated test workspace, apply restrictive file permissions, avoid shared or cloud-synchronized folders, and remove local state before transferring a machine or source tree. Do not place passwords, session cookies, private keys, bearer credentials, or personal conversation exports in a profile.

## Network behavior

Demo mode is intended for local evaluation. Outside demo mode, network behavior depends on the adapters and repositories the operator explicitly configures. Third-party privacy terms apply to those services. Watcher does not make third-party collection local merely because its own state is stored on the machine.

## Diagnostics and sharing

Review every log, report, screenshot, crash artifact, manifest, and configuration file before sharing it. Treat identifiers and filesystem paths as potentially personal. Public source staging must pass the blocking sanitization scanner; passing that scan reduces known disclosure risks but is not a guarantee that content is anonymous.

There is no automatic telemetry commitment for this Preview. Verify the behavior of the exact source commit and package manifest you are evaluating.
