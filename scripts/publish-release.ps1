param([string]$OutputDirectory = 'dist', [string]$NotesFile)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core') { throw 'Use PowerShell 7 (pwsh) to publish this package.' }
$projectDirectory = Split-Path -Parent $PSScriptRoot
$distributionDirectory = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectDirectory $OutputDirectory }))
$release = Get-Content -LiteralPath (Join-Path $projectDirectory 'release.json') -Raw -Encoding utf8 | ConvertFrom-Json
$tag = 'v' + $release.version
if (-not $NotesFile) { $NotesFile = Join-Path $projectDirectory ('docs\releases\' + $tag + '.md') }
$NotesFile = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($NotesFile)) { $NotesFile } else { Join-Path $projectDirectory $NotesFile }))
if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw 'Release notes are missing.' }
Get-Command gh -ErrorAction Stop | Out-Null

function Invoke-Gh {
    param([string[]]$Arguments)
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('GitHub command failed: gh ' + ($Arguments -join ' ')) }
    return $output
}

$report = Get-Content -LiteralPath (Join-Path $distributionDirectory '验证报告.json') -Raw -Encoding utf8 | ConvertFrom-Json
$metadata = Get-Content -LiteralPath (Join-Path $distributionDirectory '喆好工具箱.build.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($report.result -ne 'passed' -or $report.runtime_execution_test.result -ne 'passed' -or
    $report.pbo_executable_path_test.result -ne 'passed' -or $report.github_update_test.result -ne 'passed') {
    throw 'Build, startup, embedded PBO and signed-update gates must all pass before publishing.'
}
if ($metadata.inspection_build -or $metadata.version -ne $release.version -or $metadata.brand -ne $release.product -or
    $metadata.update_repository -ne $release.repository -or $report.github_update_test.update_repository -ne $release.repository -or
    $report.runtime_execution_test.release_assembly_sha256 -ne $metadata.assembly_sha256 -or
    $report.pbo_executable_path_test.release_assembly_sha256 -ne $metadata.assembly_sha256) {
    throw 'Verification gates belong to a different build or release.'
}
$executable = Join-Path $distributionDirectory '喆好工具箱.exe'
$assetName = 'Zhehao.Toolkit.' + $tag + '.exe'
$assetPath = Join-Path $distributionDirectory $assetName
$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $executable).Length
if ($hash -ne $metadata.output_sha256 -or $size -ne $metadata.size) { throw 'Release EXE differs from its verified build.' }
$manifestPath = Join-Path $distributionDirectory 'zhehao-update.json'
$envelope = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
$payloadBytes = [Convert]::FromBase64String($envelope.payload)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    [int]$consumed = 0
    $rsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($release.public_key_spki), [ref]$consumed)
    if (-not $rsa.VerifyData($payloadBytes, [Convert]::FromBase64String($envelope.signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'Release manifest signature is invalid.'
    }
}
finally { $rsa.Dispose() }
$payload = [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
if ($payload.format -ne 1 -or $payload.product -ne $release.product -or $payload.repository -ne $release.repository -or
    $payload.version -ne $release.version -or $payload.file_name -ne $assetName -or $payload.sha256 -ne $hash -or
    $payload.size -ne $size -or $payload.download_url -ne ('https://github.com/' + $release.repository + '/releases/download/' + $tag + '/' + $assetName)) {
    throw 'Signed update manifest differs from the release being published.'
}
$sumsPath = Join-Path $distributionDirectory 'SHA256SUMS.txt'
if ([IO.File]::ReadAllText($sumsPath).Trim() -ne ($hash + '  ' + $assetName)) { throw 'Published checksum differs from the verified EXE.' }

Push-Location -LiteralPath $projectDirectory
try {
    $status = & git status --porcelain=v1 --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Commit project changes before publishing.' }
    $sourceCommit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'The project has no Git commit.' }
    $remoteCommit = (Invoke-Gh -Arguments @('api', ('repos/' + $release.repository + '/commits/main'), '--jq', '.sha')) -join ''
    if ($remoteCommit -ne $sourceCommit) { throw 'Push this commit to the repository main branch before publishing.' }
    $pages = ((Invoke-Gh -Arguments @('api', ('repos/' + $release.repository + '/releases?per_page=100'), '--paginate', '--slurp')) -join "`n") | ConvertFrom-Json
    $existing = @($pages | ForEach-Object { $_ } | Where-Object { $_.tag_name -eq $tag })
    if ($existing.Count -gt 1) { throw 'More than one release uses this version.' }
    if ($existing.Count -eq 1 -and -not $existing[0].draft) { throw 'This version is already public. Increase release.json version instead of replacing it.' }
    if ($existing.Count -eq 0) {
        Invoke-Gh -Arguments @('release', 'create', $tag, '--repo', $release.repository, '--target', $sourceCommit, '--title', ($release.product + ' ' + $tag), '--notes-file', $NotesFile, '--draft')
        $createdPages = ((Invoke-Gh -Arguments @('api', ('repos/' + $release.repository + '/releases?per_page=100'), '--paginate', '--slurp')) -join "`n") | ConvertFrom-Json
        $created = @($createdPages | ForEach-Object { $_ } | Where-Object { $_.tag_name -eq $tag })
        if ($created.Count -ne 1 -or -not $created[0].draft) { throw 'Cannot locate the new draft release.' }
        $releaseId = $created[0].id
    }
    else {
        if ($existing[0].target_commitish -ne $sourceCommit) { throw 'Existing draft targets a different commit.' }
        $releaseId = $existing[0].id
        Invoke-Gh -Arguments @('release', 'edit', $tag, '--repo', $release.repository, '--title', ($release.product + ' ' + $tag), '--notes-file', $NotesFile)
    }
    # Copy, rather than hard-link: the original package protection rejects multi-link EXEs.
    [IO.File]::Copy($executable, $assetPath, $true)
    if ((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'EXE changed while preparing the upload.' }
    # GitHub's tag lookup may omit drafts until the tag is published; use the release ID.
    $releaseApiPath = 'repos/' + $release.repository + '/releases/' + $releaseId
    $draft = ((Invoke-Gh -Arguments @('api', $releaseApiPath)) -join "`n") | ConvertFrom-Json
    if (-not $draft.draft) { throw 'The release became public before upload.' }
    Invoke-Gh -Arguments @('release', 'upload', $tag, $assetPath, $manifestPath, $sumsPath, '--repo', $release.repository, '--clobber')
    $uploaded = ((Invoke-Gh -Arguments @('api', $releaseApiPath)) -join "`n") | ConvertFrom-Json
    if (-not $uploaded.draft) { throw 'The release became public during upload.' }
    foreach ($localPath in @($assetPath, $manifestPath, $sumsPath)) {
        $name = [IO.Path]::GetFileName($localPath)
        $remoteAsset = @($uploaded.assets | Where-Object name -eq $name)
        $localDigest = 'sha256:' + (Get-FileHash -LiteralPath $localPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteAsset.Count -ne 1 -or $remoteAsset[0].size -ne (Get-Item -LiteralPath $localPath).Length -or
            $remoteAsset[0].digest -ne $localDigest) { throw ('GitHub asset size or SHA-256 differs: ' + $name) }
    }
    Invoke-Gh -Arguments @('release', 'edit', $tag, '--repo', $release.repository, '--draft=false', '--latest')
    Write-Output ('Published signed release: https://github.com/' + $release.repository + '/releases/tag/' + $tag)
}
finally { Pop-Location }
