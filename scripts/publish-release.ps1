param([string]$InstallerPath, [switch]$ClearPreviousDescriptions)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src\AnotherSurface.App\AnotherSurface.App.csproj')
$version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
if (-not $InstallerPath) { $InstallerPath = Join-Path $repoRoot "artifacts\installers\AnotherSurface-$version-Setup-x64.exe" }
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw '먼저 설치 EXE를 빌드하세요.' }
if (git -C $repoRoot status --porcelain) { throw '변경 사항을 커밋한 뒤 릴리즈를 게시하세요.' }
$head = (& git -C $repoRoot rev-parse HEAD).Trim()
$name = [IO.Path]::GetFileName($InstallerPath)
if ($name -ne "AnotherSurface-$version-Setup-x64.exe") { throw '설치 EXE 버전이 현재 소스와 다릅니다.' }

# Use the existing GitHub credential without printing it or writing it to disk.
$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'Never'
$credentialLines = @("protocol=https`nhost=github.com`n`n" | git -C $repoRoot credential fill)
$credential = @{}
foreach ($line in $credentialLines) {
    $parts = $line -split '=', 2
    if ($parts.Count -eq 2) { $credential[$parts[0]] = $parts[1] }
}
if (-not $credential.password) { throw 'GitHub 인증 정보를 찾을 수 없습니다.' }
$headers = @{ Authorization = 'Bearer ' + $credential.password; 'User-Agent' = 'AnotherSurface-release'; Accept = 'application/vnd.github+json' }
$api = 'https://api.github.com/repos/JellytoCodes/another-surface'
$branch = Invoke-RestMethod -Uri "$api/branches/main" -Headers $headers
if ($branch.commit.sha -ne $head) { throw '먼저 검증한 소스를 origin/main에 푸시하세요.' }
$releases = @(Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers)
$release = $releases | Where-Object tag_name -EQ $tag | Select-Object -First 1
if ($release -and -not $release.draft) { throw '이미 게시한 버전을 덮어쓰지 않습니다.' }
if (-not $release) {
    $payload = @{ tag_name = $tag; target_commitish = $head; name = "Another Surface $version"; body = ''; draft = $true; prerelease = $false }
    $release = Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json)))
}
$assets = @(Invoke-RestMethod -Uri "$api/releases/$($release.id)/assets" -Headers $headers)
if ($assets | Where-Object name -NE $name) { throw '초안에 다른 첨부 파일이 있습니다. 자동으로 삭제하지 않습니다.' }
$asset = $assets | Where-Object name -EQ $name | Select-Object -First 1
if (-not $asset) {
    $upload = $release.upload_url.Split('{')[0] + '?name=' + [Uri]::EscapeDataString($name)
    $asset = Invoke-RestMethod -Uri $upload -Method Post -Headers $headers -InFile $InstallerPath -ContentType 'application/octet-stream'
}
$hash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($asset.state -ne 'uploaded' -or $asset.size -ne (Get-Item -LiteralPath $InstallerPath).Length -or $asset.digest -ne "sha256:$hash") {
    throw '업로드한 설치 EXE의 크기 또는 해시가 다릅니다.'
}
# Release pages intentionally contain only the installer attachment.
$null = Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"body":"","draft":false,"make_latest":"true"}'
if ($ClearPreviousDescriptions) {
    foreach ($previous in $releases | Where-Object { -not $_.draft -and $_.id -ne $release.id -and $_.body }) {
        $null = Invoke-RestMethod -Uri "$api/releases/$($previous.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"body":""}'
        $check = Invoke-RestMethod -Uri "$api/releases/$($previous.id)" -Headers $headers
        if ($check.body) { throw "이전 릴리즈 설명이 남아 있습니다: $($previous.tag_name)" }
    }
}
$latest = Invoke-RestMethod -Uri "$api/releases/latest" -Headers $headers
$reference = Invoke-RestMethod -Uri "$api/git/ref/tags/$tag" -Headers $headers
if ($latest.id -ne $release.id -or $latest.draft -or $latest.body -or @($latest.assets).Count -ne 1 -or $reference.object.sha -ne $head) {
    throw '최종 릴리즈 검증에 실패했습니다.'
}
[ordered]@{ URL = $latest.html_url; Tag = $tag; Commit = $head; Body = $latest.body; File = $asset.name; Bytes = $asset.size; SHA256 = $hash } | ConvertTo-Json
