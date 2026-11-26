$ErrorActionPreference = 'Stop'

# This test runs only on ephemeral Windows CI runners. Verify the known GitHub
# Release asset before executing it, then test the actual v1.5.4 -> v1.5.5 path.
$oldInstaller = Join-Path $env:TEMP 'CHENMU-Browser-v1.5.4-Setup.exe'
$oldUrl = 'https://github.com/nwr8yz74hp-debug/chenmu-browser/releases/download/v1.5.4/CHENMU-Browser-Setup-x64.exe'
$expectedOldHash = 'df0047e65a412a402e4ae2d59d9fb073688d37eca4896be4425850d39cb68b86'
Invoke-WebRequest -Uri $oldUrl -OutFile $oldInstaller
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $oldInstaller).Hash.ToLowerInvariant() -ne $expectedOldHash) {
    throw 'The downloaded v1.5.4 installer does not match its GitHub Release digest.'
}

[xml]$project = Get-Content -LiteralPath 'CHENMU-Browser.csproj' -Raw
$expectedVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($expectedVersion -ne '1.5.5') { throw "This upgrade check expects v1.5.5, got $expectedVersion." }
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CHENMU Browser v154 Upgrade Test'
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CHENMU Browser'
$installArgs = '/S /D=' + $installDir

function Invoke-SilentInstaller([string]$Path, [string]$Arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Path
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw "Could not start installer: $Path" }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Installer exited with code $($process.ExitCode): $Path" }
}

Invoke-SilentInstaller $oldInstaller $installArgs
$previous = Get-ItemProperty -LiteralPath $registryPath -Name @('DisplayVersion', 'InstallLocation')
if ($previous.DisplayVersion -ne '1.5.4') { throw "Expected v1.5.4, got $($previous.DisplayVersion)." }

$newInstaller = Join-Path $env:GITHUB_WORKSPACE 'dist\CHENMU-Browser-Setup-x64.exe'
Invoke-SilentInstaller $newInstaller $installArgs
$installed = Get-ItemProperty -LiteralPath $registryPath -Name @('DisplayVersion', 'InstallLocation')
if ($installed.DisplayVersion -ne $expectedVersion) { throw "Expected v$expectedVersion, got $($installed.DisplayVersion)." }
$trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$actualDir = [System.IO.Path]::GetFullPath($installed.InstallLocation).TrimEnd($trimChars)
$expectedDir = [System.IO.Path]::GetFullPath($installDir).TrimEnd($trimChars)
if (-not [string]::Equals($actualDir, $expectedDir, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected install path: $actualDir" }
$app = Join-Path $installDir 'CHENMU Browser.exe'
if (-not (Test-Path -LiteralPath $app)) { throw 'The upgraded browser executable is missing.' }
$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion
if ($fileVersion -ne "$expectedVersion.0") { throw "Unexpected executable version: $fileVersion" }
$notice = Join-Path $env:LOCALAPPDATA 'CHENMU Browser\update-notice.txt'
if (-not (Test-Path -LiteralPath $notice) -or (Get-Content -LiteralPath $notice -Raw).Trim() -ne "v$expectedVersion") { throw 'The v1.5.5 update notice marker was not created.' }
$notesPath = Join-Path $installDir 'RELEASE_NOTES.md'
if (-not (Test-Path -LiteralPath $notesPath) -or (Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8) -notmatch '# CHENMU Browser v1\.5\.5 更新说明') { throw 'The installed v1.5.5 release notes are missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $installDir 'WebView2Loader.dll'))) { throw 'The installed WebView2 native loader is missing.' }
Write-Host "PASS: verified GitHub v1.5.4 installer -> v$expectedVersion silent upgrade at $actualDir; file version, update marker, release notes and WebView2 loader checked."
