# Unsigned Preview Notice

Watcher Preview is distributed as an unsigned Windows x64 ZIP. The ZIP and its
executables do not carry a trusted publisher signature. Windows may display
Microsoft Defender SmartScreen messages such as **Windows protected your PC**
or identify the executable as **Unknown Publisher**.

Before extraction, compare the ZIP SHA-256 with the value in the separately
published `WatcherPreview-win-x64.release-summary.json`. After extraction,
compare package files with `manifest.json`. Stop if a hash differs or if local
or organizational policy prohibits unsigned software.

A matching hash establishes that bytes match the separately published record.
It does not establish publisher identity, malware-free status, general safety,
or suitability for a live workflow. No malware-scan or independent final audit
claim is made by this notice. Review `SECURITY.md` and `SUPPORT_MATRIX.md`, and
use only the bundled demo/test-sink path described there.
