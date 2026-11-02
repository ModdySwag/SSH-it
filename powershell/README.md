# SshIt — a PowerShell module for SSH-it

Script the client you already use: the same sessions, keys, jump hosts, leases and signed policy the
window uses. Every function is one `SSH-it.exe` verb, so nothing here re-implements SSH.

## Use it

```powershell
Import-Module .\powershell\SshIt\SshIt.psd1

Get-SshItSession                          # id, name, target, host, port, tags
Invoke-SshItCommand web-1 'uptime'     # ExitCode, Stdout, Stderr, DurationMs
Get-SshItFile web-1 /var/log           # entries: name, path, directory, size, mode, modified
Test-SshItEvidence                        # the signed chain: ok, rows, first break if any
Compare-SshItOutput web-1 'uptime'     # the same command twice, diffed by line
Get-SshItLedger 'error' -Limit 20         # search command and output
New-SshItTunnel web-1 'L:8080:localhost:80'
Get-SshItPosture                          # what each saved host actually advertises
Get-SshItBlastRadius web-1 'rm -rf /tmp/*'
```

A remote command that exits non-zero is not an error: `Invoke-SshItCommand` returns the exit code and the
output, and your script decides. A **refusal** — an unknown session, a closed policy, a missing lease —
throws with the reason.

The executable is found in this order: `$env:SSHIT_EXE`, then the per-user install, then the repository's
Debug build. Point `SSHIT_EXE` somewhere else to test a specific build.

## Two things worth knowing

- **The exe is a GUI-subsystem program.** PowerShell's `&` call operator never sees its stdout; the module
  runs it through `Start-Process` with real redirects. Any other tool driving the CLI must do the same.
- **Nothing prints = a refusal, an old build, or another instance.** An installed build older than this
  module does not know the newer verbs, so the launch hands off to a window and exits 0 with no output.
  The module turns that silence into an exception rather than an empty result.

## Test it

```powershell
powershell -ExecutionPolicy Bypass -File .\powershell\tests\Test-SshItModule.ps1
```

Ten checks against real sessions; it prefers the tree's Debug build and falls back to the install.
