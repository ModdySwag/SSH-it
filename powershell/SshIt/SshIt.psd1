@{
    RootModule        = 'SshIt.psm1'
    ModuleVersion     = '1.0.0'
    GUID              = '3f2b9c4e-6a71-4d0e-9a58-2c1d7e4b6a90'
    Author            = 'Moddy'
    CompanyName       = 'moddys.net'
    Copyright         = '(c) Moddy. MIT.'
    Description       = 'Script SSH-it from PowerShell: sessions, commands, files, tunnels, the ledger, posture, blast radius and the evidence chain. Thin wrappers over SSH-it.exe, so it uses your real ssh config, keys and jump hosts.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @(
        'Get-SshItExe'
        'Get-SshItSession'
        'Invoke-SshItCommand'
        'Get-SshItFile'
        'Receive-SshItFile'
        'Send-SshItFile'
        'Get-SshItLedger'
        'Get-SshItTunnel'
        'New-SshItTunnel'
        'Remove-SshItTunnel'
        'Get-SshItQueue'
        'Add-SshItTransfer'
        'Invoke-SshItQueue'
        'Test-SshItEvidence'
        'Get-SshItPosture'
        'Get-SshItBlastRadius'
        'Compare-SshItOutput'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
    PrivateData       = @{
        PSData = @{
            Tags       = @('ssh', 'sftp', 'tunnels', 'automation')
            ProjectUri = 'https://moddys.net'
        }
    }
}
