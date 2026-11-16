param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$InnoSetupCompiler
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $InnoSetupCompiler) {
    $compilerCandidates = @(
        $env:INNO_SETUP_ISCC,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $InnoSetupCompiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $InnoSetupCompiler -or -not (Test-Path -LiteralPath $InnoSetupCompiler)) {
    throw 'Inno Setup의 ISCC.exe 경로를 -InnoSetupCompiler로 지정하세요.'
}

& (Join-Path $PSScriptRoot 'build-release.ps1') -Configuration $Configuration -SkipTests:$SkipTests
[xml]$appProject = Get-Content -LiteralPath (Join-Path $projectRoot 'src\AnotherSurface.App\AnotherSurface.App.csproj')
$version = $appProject.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$staging = Join-Path $projectRoot ('.work\tmp\installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$installerOutput = Join-Path $projectRoot 'artifacts\installers'

# Publish with the runtime and build options together: this is an offline installer.
dotnet publish (Join-Path $projectRoot 'src\AnotherSurface.App\AnotherSurface.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $staging `
    --disable-build-servers -m:1 /p:CopyOutputSymbolsToPublishDirectory=false /p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw '설치본 게시에 실패했습니다.' }
foreach ($required in @('AnotherSurface.exe', 'AnotherSurface.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "설치 파일 누락: $required" }
}
if (Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object Name -Match 'AnotherSurface\.Tests|testhost|xunit|Renderer\.Native|WebView2') {
    throw '개발용 파일이나 구형 렌더러가 설치본에 포함되었습니다.'
}
if (@(Get-ChildItem -LiteralPath (Join-Path $staging 'Assets\Stickers') -File).Count -ne 25) {
    throw '기본 스티커 25종이 설치본에 없습니다.'
}
$files = @(Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($staging, $_.FullName); SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{
    Version = $version
    SourceCommit = (& git -C $projectRoot rev-parse HEAD)
    SourceDirty = [bool](& git -C $projectRoot status --porcelain --untracked-files=normal)
    Configuration = $Configuration
    RuntimeIdentifier = 'win-x64'
    SelfContained = $true
    BuiltAtUtc = [DateTime]::UtcNow.ToString('o')
    Files = $files
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging 'BuildInfo.json') -Encoding utf8

& $InnoSetupCompiler "/DAppVersion=$version" "/DPublishDir=$staging" "/DInstallerOutputDir=$installerOutput" (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw '설치 EXE 빌드에 실패했습니다.' }
$installer = Join-Path $installerOutput "AnotherSurface-$version-Setup-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw '생성된 설치 EXE가 없습니다.' }
Write-Output "Installer verified: $installer"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)"
