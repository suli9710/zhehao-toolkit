$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$release = Get-Content -LiteralPath (Join-Path $projectDirectory 'release.json') -Raw -Encoding utf8 | ConvertFrom-Json
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UpdateRuntime.cs') -Raw -Encoding utf8
$source = $source.Replace('__REPOSITORY__', $release.repository).Replace('__PUBLIC_KEY__', $release.public_key_spki).Replace('__SIGNING_KEY_ID__', $release.signing_key_id)
$compiledPath = Join-Path $projectDirectory 'build\UpdateRuntime.compiled.dll'
$runtimePath = Join-Path $projectDirectory 'build\Zhehao.Toolkit.Runtime.dll'
if (Test-Path -LiteralPath $compiledPath) { Remove-Item -LiteralPath $compiledPath -Force }
$references = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object { $_.FullName })
Add-Type -TypeDefinition $source -ReferencedAssemblies $references -OutputAssembly $compiledPath -OutputType Library
$cecilPath = Join-Path $projectDirectory 'inspection\tools\mono.cecil\lib\netstandard2.0\Mono.Cecil.dll'
Add-Type -Path $cecilPath
$runtimeAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($compiledPath)
try {
    $runtimeAssembly.Name.Name = 'Zhehao.Toolkit.Runtime'
    $runtimeAssembly.Name.Version = [Version]($release.version + '.0')
    $runtimeAssembly.MainModule.Name = 'Zhehao.Toolkit.Runtime.dll'
    $runtimeAssembly.Write($runtimePath)
}
finally { $runtimeAssembly.Dispose() }
Write-Output 'Compiled the embedded GitHub update runtime.'
