param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$distributionDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$physicalProjectDirectory = & python -c 'import pathlib,sys; sys.stdout.reconfigure(encoding="utf-8"); print(pathlib.Path(sys.argv[1]).resolve(strict=True))' $projectDirectory
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the build workspace.' }
$physicalProjectDirectory = $physicalProjectDirectory.Trim()
$cecilPath = Join-Path $projectDirectory 'inspection\tools\mono.cecil\lib\netstandard2.0\Mono.Cecil.dll'
if (-not ('ZhehaoBrandPatcher' -as [type])) {
    Add-Type -Path $cecilPath
    $references = @($cecilPath)
    $references += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object { $_.FullName })
    Add-Type -Path (Join-Path $PSScriptRoot 'BrandPatcher.cs') -ReferencedAssemblies $references
}
$originalAssembly = Join-Path $projectDirectory 'inspection\assemblies\LaoYing.Optimizer.dll'
$patchedAssembly = Join-Path $physicalProjectDirectory 'build\LaoYing.Optimizer.dll'
$unchangedGuards = [ZhehaoBrandPatcher]::VerifyPboGuards($originalAssembly, $patchedAssembly)
$assembly = [System.Reflection.Assembly]::LoadFile($patchedAssembly)
$bindingType = $assembly.GetType('LaoYing.Optimizer.App.Modules.Pbo.WindowsPboToolboxBindingFiles', $true)
$protectionType = $assembly.GetType('LaoYing.Optimizer.App.Modules.Pbo.PboToolboxBindingProtection', $true)
$bindingFlags = [System.Reflection.BindingFlags]'Static,NonPublic'
$resolve = $bindingType.GetMethod('ResolveExecutablePath', $bindingFlags)
$reject = $protectionType.GetMethod('RejectReparsePoints', $bindingFlags)
$cases = [System.Collections.Generic.List[object]]::new()

function Assert-ResolvedPath {
    param([string]$Name, [string]$InputPath, [string]$ExpectedPath)
    $actual = $resolve.Invoke($null, [object[]]@($InputPath))
    if (-not [string]::Equals($actual, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Incorrect executable path for ' + $Name + ': ' + $actual)
    }
    $reject.Invoke($null, [object[]]@($actual)) | Out-Null
    $cases.Add([ordered]@{ name = $Name; result = 'passed' })
}

function Assert-PathRejected {
    param([string]$Name, [System.Reflection.MethodInfo]$Method, [string]$Path, [type]$ExceptionType)
    $rejected = $false
    try { $Method.Invoke($null, [object[]]@($Path)) | Out-Null }
    catch {
        $cause = $_.Exception
        while ($cause.InnerException) { $cause = $cause.InnerException }
        if (-not $ExceptionType.IsInstanceOfType($cause)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw ('Unsafe or invalid path accepted: ' + $Name) }
    $cases.Add([ordered]@{ name = $Name; result = 'passed' })
}

$releaseExecutable = Join-Path $distributionDirectory '喆好工具箱.exe'
$physicalExecutable = & python -c 'import pathlib,sys; sys.stdout.reconfigure(encoding="utf-8"); print(pathlib.Path(sys.argv[1]).resolve(strict=True))' $releaseExecutable
if ($LASTEXITCODE -ne 0) { throw 'Unable to locate the packaged executable.' }
$physicalExecutable = $physicalExecutable.Trim()
Assert-ResolvedPath 'current launch path' $releaseExecutable $physicalExecutable
Assert-ResolvedPath 'physical launch path' $physicalExecutable $physicalExecutable

# A tiny file is sufficient to test path handling after downloading, moving and renaming.
# All fixtures and junction targets stay inside this uniquely owned build directory.
$buildRoot = Join-Path $physicalProjectDirectory 'build'
$fixtureRoot = Join-Path $buildRoot ('pbo-path-check-' + [Guid]::NewGuid().ToString('N'))
if (-not [IO.Path]::GetFullPath($fixtureRoot).StartsWith([IO.Path]::GetFullPath($buildRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Path fixture escapes the build workspace.'
}
$realDirectory = Join-Path $fixtureRoot '实体目录'
$movedDirectory = Join-Path $realDirectory '带 空格的下载目录'
$innerJunction = Join-Path $realDirectory '模块联接'
$outerJunction = Join-Path $fixtureRoot '下载入口'
$fixtureExecutable = Join-Path $movedDirectory '喆好 更名工具箱.exe'
$hardLink = Join-Path $movedDirectory '不能登记的硬链接.exe'
$longDirectories = [System.Collections.Generic.List[string]]::new()
$longExecutable = $null
try {
    New-Item -ItemType Directory -Path $movedDirectory -Force | Out-Null
    [IO.File]::WriteAllBytes($fixtureExecutable, [byte[]]@(77,90,0,0))
    New-Item -ItemType Junction -Path $innerJunction -Target $movedDirectory | Out-Null
    New-Item -ItemType Junction -Path $outerJunction -Target $realDirectory | Out-Null
    $nestedLaunchPath = Join-Path $outerJunction '模块联接\喆好 更名工具箱.exe'
    Assert-ResolvedPath 'relocated file with Chinese name and spaces' $fixtureExecutable $fixtureExecutable
    Assert-ResolvedPath 'nested directory junctions' $nestedLaunchPath $fixtureExecutable
    Assert-ResolvedPath 'extended-length launch path' ('\\?\' + $nestedLaunchPath) $fixtureExecutable
    $longDirectory = $movedDirectory
    for ($segment = 1; $segment -le 3; $segment++) {
        $longDirectory = Join-Path $longDirectory (('长' * 70) + $segment)
        $longDirectories.Add($longDirectory)
    }
    New-Item -ItemType Directory -Path $longDirectory -Force | Out-Null
    $longExecutable = Join-Path $longDirectory '喆好工具箱.exe'
    [IO.File]::WriteAllBytes($longExecutable, [byte[]]@(77,90,0,0))
    if ($longExecutable.Length -le 260) { throw 'Long-path fixture is too short.' }
    Assert-ResolvedPath 'physical path longer than 260 characters' $longExecutable $longExecutable
    Assert-PathRejected 'original protection still rejects junction aliases' $reject $nestedLaunchPath ([UnauthorizedAccessException])
    Assert-PathRejected 'missing executable' $resolve (Join-Path $movedDirectory 'missing.exe') ([ComponentModel.Win32Exception])
    Assert-PathRejected 'directory used as executable' $resolve $movedDirectory ([ComponentModel.Win32Exception])
    New-Item -ItemType HardLink -Path $hardLink -Target $fixtureExecutable | Out-Null
    Assert-PathRejected 'original hard-link protection preserved' $resolve $hardLink ([IO.IOException])
    $exclusive = [IO.File]::Open($fixtureExecutable, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $exclusive.Dispose()
    $cases.Add([ordered]@{ name = 'file handles released after resolution'; result = 'passed' })
}
finally {
    # Unlink only our junction entries, then remove the known file and empty directories.
    $fixtureEntries = @($outerJunction, $innerJunction, $hardLink)
    if ($longExecutable) { $fixtureEntries += $longExecutable }
    for ($index = $longDirectories.Count - 1; $index -ge 0; $index--) { $fixtureEntries += $longDirectories[$index] }
    $fixtureEntries += @($fixtureExecutable, $movedDirectory, $realDirectory, $fixtureRoot)
    foreach ($entry in $fixtureEntries) {
        if (Test-Path -LiteralPath $entry) { Remove-Item -LiteralPath $entry -Force }
    }
}
$metadata = Get-Content -LiteralPath (Join-Path $distributionDirectory '喆好工具箱.build.json') -Raw -Encoding utf8 | ConvertFrom-Json
$assemblyDigest = (Get-FileHash -LiteralPath $patchedAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
if ($assemblyDigest -ne $metadata.assembly_sha256) { throw 'Path verification assembly differs from the packaged release.' }
$reportPath = Join-Path $distributionDirectory '验证报告.json'
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 | ConvertFrom-Json
$pathReport = [ordered]@{
    result = 'passed'
    embedded_in_executable = $true
    mode = 'read-only final-path resolution; no PBO registration or hardware operations'
    release_assembly_sha256 = $assemblyDigest
    original_guard_methods_unchanged = $unchangedGuards
    patched_launch_path_calls = 2
    current_launch_path = $releaseExecutable
    resolved_launch_path = $physicalExecutable
    cases = $cases.ToArray()
    hardware_mutations_tested = $false
}
$report | Add-Member -NotePropertyName pbo_executable_path_test -NotePropertyValue $pathReport -Force
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output ('Embedded PBO path resolution passed ' + $cases.Count + ' checks; ' + $unchangedGuards + ' original guard methods unchanged.')
