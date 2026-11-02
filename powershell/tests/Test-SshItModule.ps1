<#
.SYNOPSIS
    Exercises the SshIt PowerShell module against real sessions.

.DESCRIPTION
    Run it from anywhere:

        powershell -ExecutionPolicy Bypass -File powershell\tests\Test-SshItModule.ps1

    It uses the first remote session on this machine (or -Session <id>). Every call goes through SSH-it.exe,
    so the module is proven against the same code the window uses, not a mock.
#>
[CmdletBinding()]
param([string]$Session = '')

$ErrorActionPreference = 'Stop'
$pass = 0
$fail = 0

function Check([string]$Name, [scriptblock]$Test) {
    try {
        $result = & $Test
        if ($result) { $script:pass++; "PASS  $Name" }
        else { $script:fail++; "FAIL  $Name" }
    }
    catch {
        $script:fail++
        "FAIL  $Name  ($($_.Exception.Message))"
    }
}

# Test the build in this tree, not a possibly older install: the module prefers the installed exe,
# which on a development machine can predate the verbs it calls.
if (-not $env:SSHIT_EXE) {
    $dev = Join-Path $PSScriptRoot '..\..\src\SshIt\bin\Debug\net9.0-windows\SSH-it.exe'
    if (Test-Path $dev) { $env:SSHIT_EXE = (Resolve-Path $dev).Path }
}

$modulePath = Join-Path $PSScriptRoot '..\SshIt\SshIt.psd1'
Import-Module $modulePath -Force

# Any machine: test against whichever remote session exists, never a hardcoded one.
if (-not $Session) {
    # Pick the first remote session that actually answers: a stale entry with a missing key must not
    # fail the suite that is meant to prove the module, not the user's session list.
    foreach ($pick in @(Get-SshItSession)) {
        # Remote means it can both run a command and list a remote directory; a local shell or a
        # stale entry with a missing key fails the second half and is passed over.
        try {
            if ((Invoke-SshItCommand $pick.id 'exit 0').ExitCode -eq 0 -and
                @(Get-SshItFile $pick.id '/').Count -ge 1) { $Session = $pick.id; break }
        } catch { }
    }
    if (-not $Session) { throw 'no reachable remote session to test against: add one, or pass -Session <id>' }
}

"exe: " + (Get-SshItExe)

Check 'the module exports 17 functions' {
    $exported = (Get-Command -Module SshIt).Count
    $exported -eq 17
}

Check 'sessions come back as objects with id and name' {
    $sessions = @(Get-SshItSession)
    $sessions.Count -ge 1 -and ($sessions | Where-Object { $_.id -eq $Session }).name
}

Check 'a command returns its exit code and stdout' {
    $result = Invoke-SshItCommand $Session 'hostname'
    $result.ExitCode -eq 0 -and $result.Stdout.Trim().Length -gt 0
}

Check 'a non-zero remote exit is reported, not thrown' {
    $result = Invoke-SshItCommand $Session 'exit 7'
    $result.ExitCode -eq 7 -and $result.Ok -eq $false
}

Check 'an unknown session throws a refusal that names it' {
    try { Invoke-SshItCommand 'no-such-host' 'true' | Out-Null; $false }
    catch { $_.Exception.Message -match 'no session' }
}

Check 'the ledger returns rows through the module' {
    $rows = @(Get-SshItLedger -Limit 2)
    $rows.Count -ge 1 -and $rows[0].PSObject.Properties.Name -contains 'Command'
}

Check 'the tunnel list is a list even when empty' {
    $tunnels = Get-SshItTunnel
    $null -eq $tunnels -or @($tunnels).Count -eq 0
}

Check 'a remote listing returns named entries' {
    $files = @(Get-SshItFile $Session '/')
    $files.Count -ge 1 -and $files[0].PSObject.Properties.Name -contains 'name'
}

Check 'the evidence chain verifies and reports its row count' {
    $evidence = Test-SshItEvidence
    $evidence.ok -eq $true -and $evidence.rows -gt 0
}

Check 'a diff of two runs of one command returns a result' {
    $diff = Compare-SshItOutput $Session 'uptime'
    if ($null -eq $diff) { throw 'no diff object came back' }
    $diff.summary -match 'line'
}

''
'RESULT: {0} passed, {1} failed' -f $pass, $fail
if ($fail -gt 0) { exit 1 }
