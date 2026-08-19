param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} `
    "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio Installer의 vswhere.exe를 찾을 수 없습니다."
}

$msbuild = & $vswhere `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find "MSBuild\**\Bin\amd64\MSBuild.exe" |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild)) {
    throw "Visual Studio C++ x64 빌드 도구를 찾을 수 없습니다."
}

$nativeProject = Join-Path $projectRoot `
    "Vellume.Renderer.Native\Vellume.Renderer.Native.vcxproj"
$pathRows = @(& cmd.exe /d /c "set path") |
    Where-Object { $_ -match '^(?i:path)=' }
if ($pathRows.Count -gt 1) {
    # Some automation hosts inject both PATH and Path. .NET Framework
    # MSBuild treats them as duplicate dictionary keys when it starts cl.exe.
    $nativeCommand =
        "set PATH=& call `"$msbuild`" `"$nativeProject`" " +
        "/t:Build /p:Configuration=$Configuration /p:Platform=x64 /m:1"
    & cmd.exe /d /c $nativeCommand
}
else {
    & $msbuild `
        $nativeProject `
        /t:Build `
        "/p:Configuration=$Configuration" `
        /p:Platform=x64 `
        /m:1
}
if ($LASTEXITCODE -ne 0) {
    throw "C++ 네이티브 렌더러 빌드에 실패했습니다."
}

dotnet build `
    (Join-Path $projectRoot "Vellume.App\Vellume.App.csproj") `
    -c $Configuration `
    --disable-build-servers `
    -m:1
if ($LASTEXITCODE -ne 0) {
    throw "Vellume 앱 빌드에 실패했습니다."
}

if (-not $SkipTests) {
    dotnet test `
        (Join-Path $projectRoot "Vellume.Tests\Vellume.Tests.csproj") `
        -c $Configuration `
        --disable-build-servers `
        -m:1 `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Vellume 테스트에 실패했습니다."
    }
}
