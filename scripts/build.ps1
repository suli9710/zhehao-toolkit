param([switch]$VerifyStartup, [string]$OutputDirectory = 'dist', [switch]$SignRelease)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core') { throw 'Use PowerShell 7 (pwsh) to build this package.' }
$projectDirectory = Split-Path -Parent $PSScriptRoot
$distributionDirectory = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectDirectory $OutputDirectory }))
Push-Location -LiteralPath $projectDirectory
try {
    function Invoke-ToolkitPython {
        param([string[]]$Arguments)
        & python @Arguments
        if ($LASTEXITCODE -ne 0) { throw ('Python build step failed: ' + ($Arguments -join ' ')) }
    }
    Invoke-ToolkitPython -Arguments @('-c', 'import pefile, PIL, PySide6')
    $toolDirectory = Join-Path $projectDirectory 'inspection\tools'
    New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
    $cecilArchive = Join-Path $toolDirectory 'mono.cecil.nupkg'
    $cecilLibrary = Join-Path $toolDirectory 'mono.cecil\lib\netstandard2.0\Mono.Cecil.dll'
    $cecilDigest = 'D2A23832AAA948BA9A01ACC42B5726E34C5F995958F1B30D45C0E7C70B3A72D5'
    if (-not (Test-Path -LiteralPath $cecilArchive)) {
        Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/mono.cecil/0.11.6/mono.cecil.0.11.6.nupkg' -OutFile $cecilArchive -TimeoutSec 60
    }
    if ((Get-FileHash -LiteralPath $cecilArchive -Algorithm SHA256).Hash -ne $cecilDigest) { throw 'Mono.Cecil archive hash mismatch.' }
    if (-not (Test-Path -LiteralPath $cecilLibrary)) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($cecilArchive, (Join-Path $toolDirectory 'mono.cecil'), $true)
    }
    Invoke-ToolkitPython -Arguments @('scripts\download_release.py')
    Invoke-ToolkitPython -Arguments @('scripts\inspect_bundle.py', 'inspection\LaoYing.Toolkit.v1.8.4.exe', '--extract-main')
    Invoke-ToolkitPython -Arguments @('scripts\build_icons.py')
    & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $PSScriptRoot 'compile-update-runtime.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Embedded update runtime compilation failed.' }
    & (Join-Path $PSScriptRoot 'patch-assembly.ps1')
    Invoke-ToolkitPython -Arguments @('scripts\repack_bundle.py', '--output-dir', $distributionDirectory)
    Invoke-ToolkitPython -Arguments @('scripts\verify_branding.py', '--output-dir', $distributionDirectory)
    if ($SignRelease) {
        & (Join-Path $PSScriptRoot 'sign-release.ps1') -OutputDirectory $distributionDirectory
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $PSScriptRoot 'verify-update.ps1') -OutputDirectory $distributionDirectory
        if ($LASTEXITCODE -ne 0) { throw 'GitHub update verification failed.' }
    }
    # Run in a fresh host so read-only reflection does not lock the assembly for later builds.
    & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $PSScriptRoot 'verify-pbo-path.ps1') -OutputDirectory $distributionDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Embedded PBO executable path verification failed.' }
    if ($VerifyStartup) {
        Invoke-ToolkitPython -Arguments @('scripts\repack_bundle.py', '--smoke')
        $smokeProcess = Start-Process -FilePath (Join-Path $projectDirectory 'build\喆好工具箱-启动检查.exe') -ArgumentList '--portable-smoke-exit' -WorkingDirectory (Join-Path $projectDirectory 'build') -WindowStyle Hidden -PassThru
        if (-not $smokeProcess.WaitForExit(20000)) { throw ('Smoke test timed out, PID ' + $smokeProcess.Id) }
        if ($smokeProcess.ExitCode -ne 0) { throw ('Smoke test failed, exit code ' + $smokeProcess.ExitCode) }
        $verificationPath = Join-Path $distributionDirectory '验证报告.json'
        $verification = Get-Content -LiteralPath $verificationPath -Raw -Encoding utf8 | ConvertFrom-Json
        $buildMetadata = Get-Content -LiteralPath (Join-Path $distributionDirectory '喆好工具箱.build.json') -Raw -Encoding utf8 | ConvertFrom-Json
        $verification.runtime_execution_test = [ordered]@{
            result = 'passed'
            mode = 'original isolated smoke test; non-elevated test host'
            release_assembly_sha256 = $buildMetadata.assembly_sha256
            exit_code = $smokeProcess.ExitCode
            hardware_mutations_tested = $false
        }
        $verification | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $verificationPath -Encoding utf8
        Write-Output 'Release assembly startup test passed.'
    }
    Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md') -Destination (Join-Path $distributionDirectory '使用说明.md') -Force
    Write-Output ('Built executable: ' + (Join-Path $distributionDirectory '喆好工具箱.exe'))
}
finally { Pop-Location }
