param([string]$OutputDirectory = 'dist', [switch]$LiveGitHub)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$distributionDirectory = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectDirectory $OutputDirectory }))
$release = Get-Content -LiteralPath (Join-Path $projectDirectory 'release.json') -Raw -Encoding utf8 | ConvertFrom-Json
$signedJson = [IO.File]::ReadAllText((Join-Path $distributionDirectory 'zhehao-update.json'))
$cecilPath = Join-Path $projectDirectory 'inspection\tools\mono.cecil\lib\netstandard2.0\Mono.Cecil.dll'
Add-Type -Path $cecilPath
$mainAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $projectDirectory 'build\LaoYing.Optimizer.dll'))
try {
    $runtimeResource = $mainAssembly.MainModule.Resources | Where-Object Name -eq 'Zhehao.Toolkit.Runtime.dll'
    $runtimeBytes = $runtimeResource.GetResourceData()
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($runtimeBytes)) -ne (Get-FileHash -LiteralPath (Join-Path $projectDirectory 'build\Zhehao.Toolkit.Runtime.dll')).Hash) {
        throw 'Embedded update runtime differs from its verified source build.'
    }
    $about = $mainAssembly.MainModule.GetType('LaoYing.Optimizer.App.Views.AboutView')
    if ($about.Methods.Count -ne 3 -or $about.Properties.Count -ne 1 -or $about.Fields.Count -ne 1 -or $about.Interfaces.Count -ne 0) { throw 'Legacy About-page controls remain.' }
    $aboutMarkup = ($about.Methods | Where-Object Name -eq 'InitializeComponent').Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' } | Select-Object -ExpandProperty Operand
    foreach ($trace in @('LaoYing', '老嘤', '老嘤评测', '老婴评测', '1042550392', '3882359631', '咖啡', '制作', '©', 'B 站', '抖音', 'QQ')) {
        if ($aboutMarkup.Contains($trace)) { throw ('Legacy About-page text remains: ' + $trace) }
    }
    if (-not $aboutMarkup.Contains('版本 ' + $release.version) -or -not $aboutMarkup.Contains('软件简介')) { throw 'About-page content is incomplete.' }
}
finally { $mainAssembly.Dispose() }
$runtimeStream = [IO.MemoryStream]::new($runtimeBytes, $false)
try { $runtime = [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromStream($runtimeStream) }
finally { $runtimeStream.Dispose() }
$updates = $runtime.GetType('Zhehao.Toolkit.Runtime.GitHubUpdates', $true)
$verifyManifest = $updates.GetMethod('VerifyManifest')
$manifest = $verifyManifest.Invoke($null, @($signedJson))
$physicalProject = & python -c 'import pathlib,sys; sys.stdout.reconfigure(encoding="utf-8"); print(pathlib.Path(sys.argv[1]).resolve())' $projectDirectory
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve test dependencies.' }
$infrastructure = [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath((Join-Path $physicalProject.Trim() 'inspection\assemblies\LaoYing.Optimizer.Infrastructure.dll'))
$windows = [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath((Join-Path $physicalProject.Trim() 'inspection\assemblies\LaoYing.Optimizer.Windows.dll'))
$releaseType = $infrastructure.GetType('LaoYing.Optimizer.Infrastructure.Updates.ApplicationRelease', $true)
$verifiedType = $windows.GetType('LaoYing.Optimizer.Windows.Updates.VerifiedUpdatePackage', $true)
$parseRelease = $updates.GetMethod('ParseRelease').MakeGenericMethod($releaseType)
$verifyFile = ($updates.GetMethods() | Where-Object { $_.Name -eq 'VerifyAsync' -and $_.GetParameters().Count -eq 3 }).MakeGenericMethod($verifiedType)
$checks = [Collections.Generic.List[object]]::new()

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Action, [type]$ExpectedException = [Security.Cryptography.CryptographicException])
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        $cause = $_.Exception
        while ($cause.InnerException) { $cause = $cause.InnerException }
        if (-not $ExpectedException.IsInstanceOfType($cause)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw ('Update validation accepted invalid input: ' + $Name) }
    $checks.Add([ordered]@{ name = $Name; result = 'passed' })
}

$checks.Add([ordered]@{ name = 'embedded runtime and minimal About-page content'; result = 'passed' })
if ($manifest.Version -ne $release.version -or $manifest.Sha256.Length -ne 64) { throw 'Signed release metadata is invalid.' }
$checks.Add([ordered]@{ name = 'RSA-PSS SHA-256 signed manifest'; result = 'passed' })
$releaseData = [ordered]@{
    draft = $false
    prerelease = $false
    tag_name = 'v' + $release.version
    html_url = 'https://github.com/' + $release.repository + '/releases/tag/v' + $release.version
    body = '喆好工具箱更新'
    assets = @([ordered]@{ name = $manifest.FileName; browser_download_url = $manifest.DownloadUrl; size = $manifest.Size; digest = 'sha256:' + $manifest.Sha256.ToLowerInvariant() })
}
[string]$releaseJson = $releaseData | ConvertTo-Json -Depth 6 -Compress
$previousVersion = [Version]$release.version
[string]$previousVersionText = [Version]::new($previousVersion.Major, $previousVersion.Minor, $previousVersion.Build - 1).ToString()
$candidate = $parseRelease.Invoke($null, [object[]]@([string]$releaseJson, [string]$signedJson, [string]$previousVersionText))
if ($candidate.Version -ne $release.version -or $candidate.AssetSha256 -ne $manifest.Sha256) { throw 'Release parsing failed.' }
$checks.Add([ordered]@{ name = 'GitHub release assets bound to signed version, size and hash'; result = 'passed' })
if ($null -ne $parseRelease.Invoke($null, @($releaseJson, $signedJson, $release.version))) { throw 'Updater offers the current version again.' }
$checks.Add([ordered]@{ name = 'current-version and downgrade prevention'; result = 'passed' })
$envelope = $signedJson | ConvertFrom-Json
$brokenSignature = [Convert]::FromBase64String($envelope.signature)
$brokenSignature[0] = $brokenSignature[0] -bxor 1
[string]$invalidSignatureJson = @{ payload = $envelope.payload; signature = [Convert]::ToBase64String($brokenSignature) } | ConvertTo-Json -Compress
Assert-Rejected 'tampered signature rejected' { $verifyManifest.Invoke($null, @($invalidSignatureJson)) }
$payloadBytes = [Convert]::FromBase64String($envelope.payload)
$payloadBytes[0] = $payloadBytes[0] -bxor 1
[string]$invalidPayloadJson = @{ payload = [Convert]::ToBase64String($payloadBytes); signature = $envelope.signature } | ConvertTo-Json -Compress
Assert-Rejected 'tampered manifest rejected' { $verifyManifest.Invoke($null, @($invalidPayloadJson)) }
$wrongReleaseJson = $releaseJson.Replace('https://github.com/' + $release.repository, 'https://github.com/example/another-repository')
Assert-Rejected 'foreign GitHub repository rejected' { $parseRelease.Invoke($null, @($wrongReleaseJson, $signedJson, $previousVersionText)) }
$wrongSizeJson = $releaseJson.Replace(('"size":' + $manifest.Size), ('"size":' + ($manifest.Size - 1)))
Assert-Rejected 'asset size mismatch rejected' { $parseRelease.Invoke($null, @($wrongSizeJson, $signedJson, $previousVersionText)) }
$updates.GetMethod('CacheManifest').Invoke($null, @($release.version, $signedJson)) | Out-Null
$exePath = Join-Path $distributionDirectory '喆好工具箱.exe'
$verified = $verifyFile.Invoke($null, [object[]]@($null, [string]$exePath, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
if ($verified.ProductVersion -ne $release.version -or $verified.SignerThumbprint -ne $release.signing_key_id -or $verified.Sha256 -ne $manifest.Sha256) { throw 'Executable verification failed.' }
$checks.Add([ordered]@{ name = 'release EXE verified against pinned signing key and signed hash'; result = 'passed' })
$fixturePath = Join-Path $physicalProject.Trim() ('build\corrupt-update-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    [IO.File]::Copy($exePath, $fixturePath)
    $fixture = [IO.File]::Open($fixturePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $fixture.Position = 10020000
        $byte = $fixture.ReadByte()
        $fixture.Position = 10020000
        $fixture.WriteByte($byte -bxor 1)
    }
    finally { $fixture.Dispose() }
    Assert-Rejected 'corrupted EXE rejected before installation' { $verifyFile.Invoke($null, [object[]]@($null, [string]$fixturePath, [Threading.CancellationToken]::None)).GetAwaiter().GetResult() }
}
finally { if (Test-Path -LiteralPath $fixturePath) { Remove-Item -LiteralPath $fixturePath -Force } }
if ($LiveGitHub) {
    $check = $updates.GetMethod('CheckAsync').MakeGenericMethod($releaseType)
    $remote = $check.Invoke($null, [object[]]@(('https://github.com/' + $release.repository), [string]$previousVersionText, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
    if ($remote.Version -ne $release.version -or $remote.AssetSha256 -ne $manifest.Sha256) { throw 'Live GitHub feed differs from the signed release.' }
    $checks.Add([ordered]@{ name = 'live anonymous GitHub update check'; result = 'passed' })
}
$reportPath = Join-Path $distributionDirectory '验证报告.json'
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 | ConvertFrom-Json
$report | Add-Member -NotePropertyName github_update_test -NotePropertyValue ([ordered]@{
    result = 'passed'
    update_repository = $release.repository
    automatic_download = $true
    package_verification = 'RSA-PSS SHA-256 signed manifest, pinned public key, EXE SHA-256, size and version'
    restart_transaction_retained = $true
    live_github_checked = $LiveGitHub.IsPresent
    install_or_hardware_mutations_tested = $false
    cases = $checks.ToArray()
}) -Force
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output ('GitHub update and About-page checks passed: ' + $checks.Count)
