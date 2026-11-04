param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Package
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".work\dotnet-cli"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".work\packages"
$fxc = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Recurse -Filter fxc.exe -File |
    Where-Object FullName -Match '\\x64\\' | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
if (-not $fxc) { throw 'Windows SDK의 x64 HLSL 컴파일러(fxc.exe)를 찾을 수 없습니다.' }
& $fxc /nologo /T ps_2_0 /E main /O3 `
    /Fo (Join-Path $projectRoot 'src\AnotherSurface.Windows\Shaders\VideoColor.ps') `
    (Join-Path $projectRoot 'src\AnotherSurface.Windows\Shaders\VideoColor.hlsl')
if ($LASTEXITCODE -ne 0) { throw '영상 색상 셰이더 빌드에 실패했습니다.' }

dotnet build `
    (Join-Path $projectRoot "src\AnotherSurface.App\AnotherSurface.App.csproj") `
    -c $Configuration `
    --disable-build-servers `
    -m:1
if ($LASTEXITCODE -ne 0) {
    throw "Another Surface 앱 빌드에 실패했습니다."
}

if (-not $SkipTests) {
    dotnet test `
        (Join-Path $projectRoot "tests\AnotherSurface.Tests\AnotherSurface.Tests.csproj") `
        -c $Configuration `
        --disable-build-servers `
        -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Another Surface 테스트에 실패했습니다."
    }
}

if ($Package) {
    $running = Get-Process AnotherSurface,AnotherSurface.App,AnotherSurface.Renderer.Native -ErrorAction SilentlyContinue
    if ($running) {
        throw "배포 폴더를 갱신하기 전에 Another Surface를 종료하세요."
    }
    [xml]$appProject = Get-Content -LiteralPath (Join-Path $projectRoot "src\AnotherSurface.App\AnotherSurface.App.csproj")
    $version = $appProject.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $output = Join-Path $projectRoot "src\AnotherSurface.App\bin\$Configuration\net8.0-windows10.0.17763.0"
    $packageRoot = Join-Path $projectRoot "artifacts"
    $packageDirectory = Join-Path $packageRoot "AnotherSurface"
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $staging = Join-Path $projectRoot ".work\tmp\package-$stamp"
    $backup = Join-Path $projectRoot ".work\tmp\AnotherSurface-$version-before-$stamp"
    foreach ($target in @($output, $packageDirectory, $staging, $backup)) {
        if (-not ([IO.Path]::GetFullPath($target)).StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "배포 경로가 저장소 밖을 가리킵니다: $target"
        }
        if (Test-Path -LiteralPath $target) {
            if (Get-ChildItem -LiteralPath $target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
                throw "배포 경로에 재분석 지점이 있습니다: $target"
            }
            if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "배포 경로가 재분석 지점입니다: $target"
            }
        }
    }
    foreach ($required in @("AnotherSurface.exe", "AnotherSurface.dll", "AnotherSurface.Core.dll", "AnotherSurface.Windows.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $required))) { throw "배포 파일 누락: $required" }
    }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    # Publish from the dependency graph into a fresh folder. Copying bin/ would
    # also ship obsolete DLLs left behind after a dependency was removed.
    dotnet publish (Join-Path $projectRoot "src\AnotherSurface.App\AnotherSurface.App.csproj") `
        -c $Configuration --no-build --no-restore -o $staging `
        /p:CopyOutputSymbolsToPublishDirectory=false
    if ($LASTEXITCODE -ne 0) { throw "배포 파일 게시에 실패했습니다." }
    if (Get-ChildItem -LiteralPath $staging -Filter *.exe -Recurse -File | Where-Object Name -NE 'AnotherSurface.exe') {
        throw "별도 렌더러 실행 파일이 단일 프로세스 배포본에 포함되었습니다."
    }
    if (Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object Name -Match 'WebView2|WebView|Microsoft.Web') {
        throw "제거된 브라우저 의존성이 배포본에 포함되었습니다."
    }
    $files = @(Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($staging, $_.FullName); SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{
        Version = $version
        SourceCommit = (& git -C $projectRoot rev-parse HEAD)
        SourceDirty = [bool](& git -C $projectRoot status --porcelain --untracked-files=normal)
        Configuration = $Configuration
        BuiltAtUtc = [DateTime]::UtcNow.ToString("o")
        Files = $files
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging "BuildInfo.json") -Encoding UTF8
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    if (Test-Path -LiteralPath $packageDirectory) { Move-Item -LiteralPath $packageDirectory -Destination $backup }
    Move-Item -LiteralPath $staging -Destination $packageDirectory
    foreach ($file in $files) {
        if ((Get-FileHash -LiteralPath (Join-Path $packageDirectory $file.Path) -Algorithm SHA256).Hash -ne $file.SHA256) {
            throw "배포 파일 해시 불일치: $($file.Path)"
        }
    }
    Write-Output "Package verified: $packageDirectory ($($files.Count) files). Backup: $backup"
}
