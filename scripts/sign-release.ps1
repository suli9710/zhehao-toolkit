param(
    [string]$OutputDirectory = 'dist',
    [string]$PrivateKeyPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ZhehaoToolkitRelease\update-signing-key.pem')
)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$distributionDirectory = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectDirectory $OutputDirectory }))
$release = Get-Content -LiteralPath (Join-Path $projectDirectory 'release.json') -Raw -Encoding utf8 | ConvertFrom-Json
$executable = Join-Path $distributionDirectory '喆好工具箱.exe'
$metadata = Get-Content -LiteralPath (Join-Path $distributionDirectory '喆好工具箱.build.json') -Raw -Encoding utf8 | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $metadata.output_sha256 -or $metadata.version -ne $release.version) { throw 'Release hash or version differs from build metadata.' }
$assetName = 'Zhehao.Toolkit.v' + $release.version + '.exe'
$notesPath = Join-Path $projectDirectory ('docs\releases\v' + $release.version + '.md')
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) { throw 'Signed release notes are missing.' }
$payload = [ordered]@{
    format = 1
    product = $release.product
    repository = $release.repository
    version = $release.version
    file_name = $assetName
    size = (Get-Item -LiteralPath $executable).Length
    sha256 = $hash
    download_url = 'https://github.com/' + $release.repository + '/releases/download/v' + $release.version + '/' + $assetName
    notes = [IO.File]::ReadAllText($notesPath, [Text.Encoding]::UTF8)
}
$payloadBytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText($PrivateKeyPath))
    if ([Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -ne $release.public_key_spki) { throw 'Signing key differs from the embedded public key.' }
    $signature = $rsa.SignData($payloadBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    $envelope = [ordered]@{ payload = [Convert]::ToBase64String($payloadBytes); signature = [Convert]::ToBase64String($signature) }
    $envelope | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $distributionDirectory 'zhehao-update.json') -Encoding utf8
}
finally { $rsa.Dispose() }
[IO.File]::WriteAllText((Join-Path $distributionDirectory 'SHA256SUMS.txt'), ($hash + '  ' + $assetName + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output ('Signed update manifest for ' + $release.version + '. Private key remains outside the repository.')
