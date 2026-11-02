# Release checklist — SSH-it

Nothing here runs itself except `build-release.ps1`, and even that only builds. The release is cut by hand.

## Before cutting

- [ ] `dotnet build src/SshIt/SshIt.csproj` — 0 errors
- [ ] `SSH-it.exe --selftest` — the count the continuation names, 0 failed, 1 skipped
- [ ] `powershell -File powershell\tests\Test-SshItModule.ps1` — 10 passed
- [ ] The owner has finished his debugging pass and said the word
- [ ] Version bumped in `src/SshIt/SshIt.csproj` (`Version`, `FileVersion`) AND `installer/setup.iss` (`MyAppVersion`)

## Build

- [ ] `powershell -File build-release.ps1` — writes `dist\SSH-it-Setup-<version>.exe` and prints both hashes
- [ ] Delete the superseded installer from `dist\` so the folder names one release
- [ ] `packaging\winget\Moddy.SSH-it.yaml` gets the version and `InstallerSha256`

## Prove the installer

- [ ] `Start-Process dist\SSH-it-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART -Wait`
- [ ] `(Get-Item "$env:LOCALAPPDATA\Programs\SSH-it\SSH-it.exe").VersionInfo.FileVersion` — the new version
- [ ] Run `--selftest` on the INSTALLED binary, not the publish output
- [ ] Type into two live panes (a green suite has never proved input)

## Publish

- [ ] Sign the installer (the certificate is held outside the repo; never commit it)
- [ ] Upload the installer, publish the hashes, update the downloads page
- [ ] Tag the release and attach the installer + source archive
- [ ] Verify the download URL serves the same SHA-256 that was built
