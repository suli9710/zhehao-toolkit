param([string]$PrivateKeyPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ZhehaoToolkitRelease\update-signing-key.pem'))
$ErrorActionPreference = 'Stop'
$configPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'release.json'
if (Test-Path -LiteralPath $configPath) { throw 'Release configuration already exists. Do not rotate the update key implicitly.' }
if (Test-Path -LiteralPath $PrivateKeyPath) { throw 'A signing key already exists at this path.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $PrivateKeyPath) -Force | Out-Null
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    [IO.File]::WriteAllText($PrivateKeyPath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
    $keySecurity = [Security.AccessControl.FileSecurity]::new()
    $keyOwner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $keySecurity.SetOwner($keyOwner)
    $keySecurity.SetAccessRuleProtection($true, $false)
    $keySecurity.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($keyOwner, 'FullControl', 'Allow'))
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($PrivateKeyPath), $keySecurity)
    $publicKey = $rsa.ExportSubjectPublicKeyInfo()
    $config = [ordered]@{
        product = '喆好工具箱'
        version = '1.8.5'
        repository = 'suli9710/zhehao-toolkit'
        public_key_spki = [Convert]::ToBase64String($publicKey)
        signing_key_id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicKey))
    }
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
}
finally { $rsa.Dispose() }
Write-Output 'Generated the update verification public key. The private signing key is stored outside the repository.'
