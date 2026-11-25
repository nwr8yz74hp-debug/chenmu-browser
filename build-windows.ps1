$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
$Project = Join-Path $ProjectRoot 'CHENMU-Browser.csproj'
$Output = Join-Path $ProjectRoot 'dist'
$InstallerScript = Join-Path $ProjectRoot 'CHENMU-Browser-Setup.nsi'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '需要安装 .NET SDK 10.0.404 或更高版本。'
}
$SdkText = (& dotnet --version).Trim()
$SdkVersion = [version]($SdkText.Split('-')[0])
if ($SdkVersion -lt [version]'10.0.404') {
    throw "需要 .NET SDK 10.0.404 或更高版本；当前检测到 $SdkText。"
}

[xml]$ProjectDocument = Get-Content -LiteralPath $Project -Raw
$VersionNode = $ProjectDocument.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $VersionNode) {
    throw '项目文件中找不到版本号。'
}
$Version = $VersionNode.InnerText.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "项目版本号无效：$Version"
}

$NsisCommand = Get-Command makensis -ErrorAction SilentlyContinue
$NsisPath = if ($NsisCommand) { $NsisCommand.Source } else { $null }
if (-not $NsisPath -and ${env:ProgramFiles(x86)}) {
    $Candidate = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'
    if (Test-Path -LiteralPath $Candidate) { $NsisPath = $Candidate }
}
if (-not $NsisPath -or -not (Test-Path -LiteralPath $NsisPath)) {
    throw '生成安装器需要 NSIS 3.x（makensis）。请先安装 NSIS，再重新运行此脚本。'
}

Remove-Item $Output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Output | Out-Null

& dotnet restore $Project
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore 失败。' }
& dotnet build $Project -c Release -r win-x64 -p:PlatformTarget=x64 -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败。' }

Copy-Item -LiteralPath (Join-Path $ProjectRoot 'RELEASE_NOTES.md') -Destination (Join-Path $Output 'RELEASE_NOTES.md') -Force

$WebViewLoader = Join-Path $Output 'WebView2Loader.dll'
if (-not (Test-Path -LiteralPath $WebViewLoader)) {
    throw '发布目录缺少 WebView2Loader.dll；请确认构建使用了 win-x64 RID。'
}

$Installer = Join-Path $Output 'CHENMU-Browser-Setup-x64.exe'
Remove-Item $Installer -Force -ErrorAction SilentlyContinue
Push-Location $ProjectRoot
try {
    & $NsisPath "-DPRODUCT_VERSION=$Version" $InstallerScript
    if ($LASTEXITCODE -ne 0) { throw 'NSIS 安装器编译失败。' }
}
finally {
    Pop-Location
}

Write-Host "构建完成：$Output"
Write-Host "版本：$Version"
Write-Host '安装器按当前用户安装到 LocalAppData；目标系统需有 .NET Framework 4.8 和 WebView2 Evergreen Runtime。'
