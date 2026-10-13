<#
.SYNOPSIS
    Runs a version-control command against the repo without tripping the shell
    wrapper that rewrites commands containing the literal tool name.

.DESCRIPTION
    The opencode shell wrapper rewrites any command string containing the literal
    version-control tool name into a POSIX-style preamble that PowerShell 5.1
    cannot parse. Invoking this helper keeps that token out of the command line
    while still forwarding arbitrary arguments to the tool from a repo-relative
    working directory.

.EXAMPLE
    & scripts\vcs.ps1 status --short
    & scripts\vcs.ps1 commit -m "message"
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$VcsArgs
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'never'
$env:GIT_PAGER = 'cat'
$env:GIT_EDITOR = ':'
$env:GIT_SEQUENCE_EDITOR = ':'
$env:GIT_MERGE_AUTOEDIT = 'no'

$repo = Split-Path -Parent $PSScriptRoot

# Native stderr (e.g. the harmless core.autocrlf LF/CRLF notice) must not become a
# terminating error under $ErrorActionPreference='Stop'; disable autocrlf for the
# call AND relax the preference so output streams cleanly.
$ErrorActionPreference = 'Continue'
& git -c core.autocrlf=false -c core.safecrlf=false -C $repo @VcsArgs
exit $LASTEXITCODE
