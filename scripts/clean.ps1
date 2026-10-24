[CmdletBinding(SupportsShouldProcess)]
param([switch]$Caches)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$targets = @('.work\tmp', '.work\archive')
foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src'),(Join-Path $repoRoot 'tests') -Directory) {
    $targets += Join-Path $project.FullName 'bin'
    $targets += Join-Path $project.FullName 'obj'
}
if ($Caches) { $targets += '.work\packages', '.work\dotnet-cli' }
foreach ($target in $targets) {
    $resolved = [IO.Path]::GetFullPath($target, $repoRoot)
    if (-not $resolved.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "정리 대상이 저장소 밖입니다: $resolved"
    }
    if (-not (Test-Path -LiteralPath $resolved)) { continue }
    $entry = Get-Item -LiteralPath $resolved -Force
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) {
        throw "재분석 지점은 정리하지 않습니다: $resolved"
    }
    if ($PSCmdlet.ShouldProcess($resolved, '생성 파일 정리')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
        Write-Output "Removed: $resolved"
    }
}
