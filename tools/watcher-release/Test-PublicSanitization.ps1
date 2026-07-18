[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [string[]]$AdditionalBlockedTerm = @()
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scanRoot = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $scanRoot -PathType Container)) {
    Write-Error "Sanitization scan root does not exist: $scanRoot"
    exit 2
}

$privateStateName = [regex]::Escape("." + "dcs-watcher-v2")
$oldPublishName = [regex]::Escape("app" + "-v2")
$projectWord = [regex]::Escape("Belli" + "fex")
$projectRepo = [regex]::Escape("smart" + "-commander-branch-chatgpt-codex-bridge-app")
$publicRepository = [regex]::Escape("Jukebox31b/watcher-preview")

$rules = @(
    [pscustomobject]@{ Name = "absolute-windows-path"; Pattern = '(?i)(?:[A-Z]:\\|\\\\[A-Za-z0-9._-]+\\)' },
    [pscustomobject]@{ Name = "absolute-user-or-host-path"; Pattern = '(?i)(?:^|[\s"''=])/(?:Users|home|mnt|tmp)/(?:[^/\s"'']+/)' },
    [pscustomobject]@{ Name = "private-runtime-state-name"; Pattern = "(?i)$privateStateName" },
    [pscustomobject]@{ Name = "legacy-publish-directory"; Pattern = "(?i)(?:^|[\\/])$oldPublishName(?:[\\/]|$)" },
    [pscustomobject]@{ Name = "internal-project-term"; Pattern = "(?i)$projectWord|$projectRepo|ChatGPT\s*-\s*Codex\s+bridge\s+app" },
    [pscustomobject]@{ Name = "private-path-label"; Pattern = '(?-i:DCS\s+Downloads|Authorized\s+Instructions)' },
    [pscustomobject]@{ Name = "real-chatgpt-conversation-id"; Pattern = '(?i)(?:chatgpt\.com/(?:g/[^/\s]+/)?c/|conversation[_-]?id["''\s:=]+)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' },
    [pscustomobject]@{ Name = "real-message-id"; Pattern = '(?i)(?:message|parent)[_-]?id["''\s:=]+(?:msg[_-])?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' },
    [pscustomobject]@{ Name = "real-codex-thread-id"; Pattern = '(?i)(?:codex[_ -]?)?thread[_-]?id["''\s:=]+(?:thread[_-])?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' },
    [pscustomobject]@{ Name = "repository-identity"; Pattern = ('(?i)(?:github\.com/|api\.github\.com/repos/)(?!(?:(?:YOUR[-_]?OWNER|example)(?:/|%2f)|{0}(?=$|[^A-Za-z0-9_.-])))[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+' -f $publicRepository) },
    [pscustomobject]@{ Name = "secret-token-format"; Pattern = '(?i)(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|Bearer\s+[A-Za-z0-9._~+/-]{16,}={0,2})' },
    [pscustomobject]@{ Name = "assigned-secret-or-cookie"; Pattern = '(?i)(?:cookie|set-cookie|access[_-]?token|refresh[_-]?token|api[_-]?key|authorization)\s*["'']?\s*[:=]\s*["''](?!<|your-|example|not-a-real|redacted|__)[A-Za-z0-9._~+/%=-]{16,}["'']' },
    [pscustomobject]@{ Name = "named-incident"; Pattern = '(?i)incident(?:[_ -](?:name|code|id))?\s*[:=]\s*["'']?[A-Za-z0-9][A-Za-z0-9._-]{3,}' },
    [pscustomobject]@{ Name = "personal-email"; Pattern = '(?i)\b[A-Z0-9._%+-]+@(?!example\.(?:com|org|net|invalid)\b)[A-Z0-9.-]+\.[A-Z]{2,}\b' }
)

foreach ($value in (@($env:USERNAME, $env:COMPUTERNAME) | Sort-Object -Unique)) {
    if (-not [string]::IsNullOrWhiteSpace($value) -and $value.Length -ge 3 -and $value -notmatch '^(?:user|admin|runner|desktop)$') {
        $rules += [pscustomobject]@{
            Name = "local-user-or-host-identity"
            Pattern = "(?i)(?<![A-Za-z0-9])$([regex]::Escape($value))(?![A-Za-z0-9])"
        }
    }
}
foreach ($term in $AdditionalBlockedTerm) {
    if (-not [string]::IsNullOrWhiteSpace($term)) {
        $rules += [pscustomobject]@{ Name = "additional-blocked-term"; Pattern = [regex]::Escape($term) }
    }
}

$allowedTextExtensions = @(".cs", ".csproj", ".md", ".ps1", ".cmd", ".json", ".txt", ".gitignore")
$scannerRelativePath = "tools/watcher-release/Test-PublicSanitization.ps1"
$findings = [Collections.Generic.List[object]]::new()

Get-ChildItem -LiteralPath $scanRoot -Recurse -Force | ForEach-Object {
    $relative = $_.FullName.Substring($scanRoot.Length).TrimStart('\', '/').Replace('\', '/')
    if ($_.PSIsContainer) {
        if ($_.Name -in @(".git", "bin", "obj", "artifacts", "captures", "screenshots", "reports", "keys")) {
            $findings.Add([pscustomobject]@{ File = $relative; Line = 0; Rule = "forbidden-directory" })
        }
        return
    }
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $findings.Add([pscustomobject]@{ File = $relative; Line = 0; Rule = "reparse-point" })
        return
    }
    if ($relative -eq $scannerRelativePath) {
        # The scanner encodes its signatures and is the only content exemption.
        return
    }
    if ($_.Name -ne "LICENSE" -and $_.Extension.ToLowerInvariant() -notin $allowedTextExtensions) {
        $findings.Add([pscustomobject]@{ File = $relative; Line = 0; Rule = "unexpected-file-type" })
        return
    }

    $lineNumber = 0
    try {
        foreach ($line in [IO.File]::ReadLines($_.FullName)) {
            $lineNumber++
            foreach ($rule in $rules) {
                if ($line -match $rule.Pattern) {
                    $findings.Add([pscustomobject]@{ File = $relative; Line = $lineNumber; Rule = $rule.Name })
                }
            }
        }
        $content = [IO.File]::ReadAllText($_.FullName)
        $keyMatch = [regex]::Match($content, '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----\s*\r?\n[A-Za-z0-9+/=]{16,}')
        if ($keyMatch.Success) {
            $keyLine = ([regex]::Matches($content.Substring(0, $keyMatch.Index), "`n")).Count + 1
            $findings.Add([pscustomobject]@{ File = $relative; Line = $keyLine; Rule = "private-key-material" })
        }
    }
    catch {
        $findings.Add([pscustomobject]@{ File = $relative; Line = 0; Rule = "unreadable-file" })
    }
}

if ($findings.Count -gt 0) {
    Write-Host "PUBLIC SANITIZATION BLOCKED: $($findings.Count) finding(s)" -ForegroundColor Red
    foreach ($finding in $findings | Sort-Object File, Line, Rule) {
        Write-Host ("{0}:{1}: [{2}]" -f $finding.File, $finding.Line, $finding.Rule)
    }
    Write-Host "No matching values are printed; inspect each reported source line locally."
    exit 1
}

Write-Host "PUBLIC SANITIZATION PASSED: no blocked content found in $scanRoot" -ForegroundColor Green
exit 0
