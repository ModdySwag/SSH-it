<#
.SYNOPSIS
    Script SSH-it from PowerShell: sessions, commands, files, tunnels, the ledger and the audit verbs.

.DESCRIPTION
    Every function is a thin wrapper over one SSH-it.exe verb, so it uses the same sessions, keys,
    jump hosts, leases and signed policy the window does — nothing here re-implements SSH.

    The executable is found in this order: $env:SSHIT_EXE, the per-user install, then the repository's
    Debug build. Set SSHIT_EXE to point somewhere else.

    A remote command that exits non-zero is NOT an error: Invoke-SshItCommand returns the exit code and
    the output, and the caller decides. A refusal (unknown session, closed policy, missing lease) throws.
#>

Set-StrictMode -Version Latest

function Get-SshItExe {
    <# The executable every function runs. #>
    [CmdletBinding()]
    param()
    if ($env:SSHIT_EXE -and (Test-Path -LiteralPath $env:SSHIT_EXE)) { return (Resolve-Path -LiteralPath $env:SSHIT_EXE).Path }
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\SSH-it\SSH-it.exe'
    if (Test-Path -LiteralPath $installed) { return $installed }
    $dev = Join-Path $PSScriptRoot '..\..\src\SshIt\bin\Debug\net9.0-windows\SSH-it.exe'
    if (Test-Path -LiteralPath $dev) { return (Resolve-Path -LiteralPath $dev).Path }
    throw 'SSH-it.exe was not found. Install it, or set $env:SSHIT_EXE to its path.'
}

function ConvertTo-SshItArgumentString {
    <# Start-Process joins the array with spaces and does not quote, so quote here. #>
    [CmdletBinding()]
    param([string[]]$Arguments)
    ($Arguments | ForEach-Object {
        if ($_ -eq '') { '""' }
        elseif ($_ -match '[\s"]') {
            # CommandLineToArgvW: backslashes are literal unless they precede a quote, where they double.
            $text = [regex]::Replace($_, '(\*)"', '$1$1\"')
            $text = [regex]::Replace($text, '(\+)$', '$1$1')
            '"' + $text + '"'
        }
        else { $_ }
    }) -join ' '
}

function Invoke-SshItVerb {
    <# Runs one verb, returns its stdout as text, and throws on an unexpected exit code. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int[]]$AcceptExit = @(0),
        [switch]$AllowEmpty
    )
    $exe = Get-SshItExe

    # A hung verb must not hang the caller. --bench is the usual long one; 60 s is far above it.
    $timeoutSeconds = 60
    if ($env:SSH_IT_VERB_TIMEOUT) {
        $parsed = 0
        if ([int]::TryParse($env:SSH_IT_VERB_TIMEOUT, [ref]$parsed) -and $parsed -gt 0) { $timeoutSeconds = $parsed }
    }

    # SSH-it.exe is a GUI-subsystem app: PowerShell's `&` call operator never sees its stdout (the
    # process has no attached console). Start-Process -PassThru also loses ExitCode after a TIMED
    # WaitForExit in Windows PowerShell 5.1 (observed: hasExited=True, ExitCode empty), so the module
    # starts the process directly and drains both pipes asynchronously.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ConvertTo-SshItArgumentString $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw "SSH-it $($Arguments[0]) did not finish within ${timeoutSeconds}s (killed)"
    }
    $code = $process.ExitCode
    $stdout = $outTask.GetAwaiter().GetResult()
    $stderr = $errTask.GetAwaiter().GetResult()

    if ([string]::IsNullOrWhiteSpace($stdout) -and -not $AllowEmpty) {
        # Silence from a headless verb is never a normal answer. The usual causes are an installed build
        # older than this module (its AgentCli.Wants list cannot know the verb, so the launch hands off to
        # a window and prints nothing) or another SSH-it instance taking the request. A refusal — an
        # unknown session, a closed policy — arrives on stderr, so carry it into the message.
        $reason = $stderr.Trim()
        if (-not $reason) { $reason = 'no output on either stream' }
        throw "SSH-it $($Arguments[0]) printed nothing (exit ${code}): $reason"
    }

    if ($AcceptExit -notcontains $code) {
        $detail = $stderr.Trim()
        if (-not $detail) { $detail = $stdout.Trim() }
        if (-not $detail) { $detail = 'no output' }
        throw "SSH-it $($Arguments[0]) failed (exit $code): $detail"
    }
    return $stdout
}

function ConvertFrom-SshItJson {
    <# Parses the JSON a verb printed, skipping any banner lines before it. #>
    [CmdletBinding()]
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $trimmed = $Text.Trim()
    if ($trimmed.StartsWith('{') -or $trimmed.StartsWith('[')) {
        return ($trimmed | ConvertFrom-Json)
    }
    foreach ($line in ($Text -split "`n")) {
        $candidate = $line.Trim()
        if ($candidate.StartsWith('{') -or $candidate.StartsWith('[')) {
            return ($candidate | ConvertFrom-Json)
        }
    }
    throw "SSH-it returned no JSON: $trimmed"
}

function Get-SshItSession {
    <# Every saved session, as objects: id, name, target, host, port, user, local, alias, tags. #>
    [CmdletBinding()]
    param()
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments @('--sessions', '--json'))
}

function Invoke-SshItCommand {
    <# Runs one command on a session. Returns exit code and output; a non-zero exit is not an error. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Session,
        [Parameter(Mandatory = $true, Position = 1)][string]$Command
    )
    # --exec exits with the remote command's code, so every code is accepted here; a refusal to resolve
    # the session prints no JSON, and ConvertFrom-SshItJson turns that into an exception.
    $text = Invoke-SshItVerb -Arguments @('--exec', $Session, $Command, '--json') -AcceptExit (0..255)
    $result = ConvertFrom-SshItJson $text
    if ($null -eq $result) { throw "SSH-it --exec $Session returned nothing. Check the session name." }
    # A refusal is now JSON on stdout with an `error` and no `exit`; a remote non-zero exit carries
    # `exit` and must still come back as a result, not an exception.
    if ($result.ok -eq $false -and ($result.PSObject.Properties.Name -contains "error") -and
        -not ($result.PSObject.Properties.Name -contains "exit")) {
        throw $result.error
    }
    [pscustomobject]@{
        Session    = $result.session
        Target     = $result.target
        Command    = $result.command
        ExitCode   = $result.exit
        Ok         = $result.ok
        Stdout     = $result.stdout
        Stderr     = $result.stderr
        DurationMs = $result.duration_ms
        TimedOut   = $result.timed_out
        Error      = $result.error
    }
}

function Get-SshItFile {
    <# Lists a remote directory. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Session,
        [Parameter(Position = 1)][string]$Path = '.'
    )
    $text = Invoke-SshItVerb -Arguments @('--sftp-list', $Session, $Path, '--json')
    ConvertFrom-SshItJson $text
}

function Receive-SshItFile {
    <# Downloads a remote file to a local path. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Session,
        [Parameter(Mandatory = $true, Position = 1)][string]$RemotePath,
        [Parameter(Mandatory = $true, Position = 2)][string]$LocalPath
    )
    $text = Invoke-SshItVerb -Arguments @('--sftp-get', $Session, $RemotePath, $LocalPath)
    ConvertFrom-SshItJson $text
}

function Send-SshItFile {
    <# Uploads a local file to a remote path. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Session,
        [Parameter(Mandatory = $true, Position = 1)][string]$LocalPath,
        [Parameter(Mandatory = $true, Position = 2)][string]$RemotePath
    )
    $text = Invoke-SshItVerb -Arguments @('--sftp-put', $Session, $LocalPath, $RemotePath)
    ConvertFrom-SshItJson $text
}

function Get-SshItLedger {
    <# Searches the command ledger: command, output, host, exit code and time. #>
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)][string[]]$Words = @(),
        [string]$Session,
        [string]$HostName,
        [string]$Since,
        [int]$Limit,
        [ValidateSet('any', '0', 'nonzero')][string]$Exit = 'any'
    )
    $arguments = @('--ledger')
    foreach ($word in $Words) { $arguments += $word }
    if ($Session) { $arguments += @('--session', $Session) }
    if ($HostName) { $arguments += @('--host', $HostName) }
    if ($Since) { $arguments += @('--since', $Since) }
    if ($Limit -gt 0) { $arguments += @('--limit', "$Limit") }
    if ($Exit -ne 'any') { $arguments += @('--exit', $Exit) }
    $arguments += '--json'

    # exit 1 means "nothing matched", which is an answer, not a failure
    $text = Invoke-SshItVerb -Arguments $arguments -AcceptExit @(0, 1) -AllowEmpty
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }
    ConvertFrom-SshItJson $text
}

function Get-SshItTunnel {
    <# Live forwarders started by --tunnel-up (or by the app). #>
    [CmdletBinding()]
    param()
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments @('--tunnel-list', '--json'))
}

function New-SshItTunnel {
    <# Starts a forwarder and returns it. Spec: L:bindport:host:port or R:... #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Session,
        [Parameter(Mandatory = $true, Position = 1)][string]$Spec
    )
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments @('--tunnel-up', $Session, $Spec, '--json'))
}

function Remove-SshItTunnel {
    <# Stops a forwarder by id or by its spec. #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true, Position = 0)][string]$Id)
    Invoke-SshItVerb -Arguments @('--tunnel-down', $Id) -AllowEmpty
}

function Get-SshItQueue {
    <# Transfer jobs waiting in the queue. #>
    [CmdletBinding()]
    param()
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments @('--queue-list', '--json'))
}

function Add-SshItTransfer {
    <# Queues a get or a put, in the same order every time. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Session,
        [Parameter(Mandatory = $true)][ValidateSet('get', 'put')][string]$Direction,
        [Parameter(Mandatory = $true, Position = 0)][string]$RemotePath,
        [Parameter(Mandatory = $true, Position = 1)][string]$LocalPath
    )
    $arguments = if ($Direction -eq 'get') {
        @('--queue-add', $Session, 'get', $RemotePath, $LocalPath)
    } else {
        @('--queue-add', $Session, 'put', $LocalPath, $RemotePath)
    }
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments $arguments)
}

function Invoke-SshItQueue {
    <# Runs the queue serially. Throws when any job failed. #>
    [CmdletBinding()]
    param([string]$Session)
    $arguments = @('--queue-run')
    if ($Session) { $arguments += $Session }
    $arguments += '--json'
    $text = Invoke-SshItVerb -Arguments $arguments -AcceptExit @(0, 1)
    $result = ConvertFrom-SshItJson $text
    if ($result -and $result.failed -gt 0) {
        throw "The queue finished with $($result.failed) failed job(s) of $($result.ran)."
    }
    $result
}

function Test-SshItEvidence {
    <# Verifies the signed evidence chain. Throws when it is broken, naming the first bad row. #>
    [CmdletBinding()]
    param()
    $text = Invoke-SshItVerb -Arguments @('--evidence-verify', '--json') -AcceptExit @(0, 1)
    $result = ConvertFrom-SshItJson $text
    if (-not $result.ok) {
        throw "The evidence chain is broken at row $($result.breakLine) (sequence $($result.breakSeq)): $($result.firstBreak)"
    }
    $result
}

function Get-SshItPosture {
    <# Scans every saved host's advertised algorithms and returns the findings. #>
    [CmdletBinding()]
    param([string]$Session)
    $arguments = @('--posture')
    if ($Session) { $arguments += $Session }
    $arguments += '--json'
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments $arguments)
}

function Get-SshItBlastRadius {
    <# Classifies a command and expands its operands on the host before it runs. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Session,
        [Parameter(Mandatory = $true, Position = 1)][string]$Command
    )
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments @('--blast', $Session, $Command, '--json'))
}

function Compare-SshItOutput {
    <# Runs the same command on two sessions (or twice on one) and diffs the output. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)][string]$Session,
        [Parameter(Mandatory = $true, Position = 1)][string]$Command,
        [string]$Against
    )
    $arguments = @('--diff', $Session, $Command)
    if ($Against) { $arguments += @('--against', $Against) }
    $arguments += '--json'
    ConvertFrom-SshItJson (Invoke-SshItVerb -Arguments $arguments)
}

Export-ModuleMember -Function Get-SshItExe, Get-SshItSession, Invoke-SshItCommand, Get-SshItFile,
    Receive-SshItFile, Send-SshItFile, Get-SshItLedger, Get-SshItTunnel, New-SshItTunnel,
    Remove-SshItTunnel, Get-SshItQueue, Add-SshItTransfer, Invoke-SshItQueue, Test-SshItEvidence,
    Get-SshItPosture, Get-SshItBlastRadius, Compare-SshItOutput
