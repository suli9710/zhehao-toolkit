param([switch]$InspectionBuild)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$cecilPath = Join-Path $projectDirectory 'inspection\tools\mono.cecil\lib\netstandard2.0\Mono.Cecil.dll'
Add-Type -Path $cecilPath
$references = @($cecilPath)
$references += @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object { $_.FullName })
Add-Type -Path (Join-Path $PSScriptRoot 'BrandPatcher.cs') -ReferencedAssemblies $references
$outputName = if ($InspectionBuild) { 'LaoYing.Optimizer.inspection.dll' } else { 'LaoYing.Optimizer.dll' }
$inputAssembly = Join-Path $projectDirectory 'inspection\assemblies\LaoYing.Optimizer.dll'
$outputAssembly = Join-Path $projectDirectory ('build\' + $outputName)
$release = Get-Content -LiteralPath (Join-Path $projectDirectory 'release.json') -Raw -Encoding utf8 | ConvertFrom-Json
$aboutMarkup = Get-Content -LiteralPath (Join-Path $projectDirectory 'assets\about.xaml') -Raw -Encoding utf8
$changeLog = [ZhehaoBrandPatcher]::Patch($inputAssembly, $outputAssembly, (Join-Path $projectDirectory 'assets'), $PSHOME, $InspectionBuild.IsPresent,
    $release.version, $release.repository, $release.signing_key_id, (Join-Path $projectDirectory 'build\Zhehao.Toolkit.Runtime.dll'), $aboutMarkup)
$changeLog | Set-Content -LiteralPath (Join-Path $projectDirectory ('build\' + $outputName + '.changes.txt')) -Encoding utf8
$changeLog
