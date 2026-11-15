param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GamePath 'Spaceflight Simulator_Data\Managed'
$uiTools = Join-Path $GamePath 'Mods\UITools\UITools.dll'
$out = Join-Path $PSScriptRoot 'out'
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'C# compiler not found.' }
if (-not (Test-Path -LiteralPath $uiTools -PathType Leaf)) { throw 'UITools.dll not found in the game Mods directory.' }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$refs = @('netstandard','System.Runtime','0Harmony','Assembly-CSharp','Assembly-CSharp-firstpass','Unity.TextMeshPro','UnityEngine','UnityEngine.CoreModule','UnityEngine.Physics2DModule','UnityEngine.IMGUIModule','UnityEngine.TextRenderingModule','UnityEngine.UI','UnityEngine.UIModule')
$argsList = @('/nologo','/target:library','/langversion:latest',"/out:$(Join-Path $out 'HIMA.dll')")
foreach ($name in $refs) {
    $ref = Join-Path $managed ($name + '.dll')
    if (-not (Test-Path -LiteralPath $ref -PathType Leaf)) { throw "Missing game reference: $name.dll" }
    $argsList += "/reference:$ref"
}
$argsList += "/reference:$uiTools"
$argsList += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $CompilerPath @argsList
if ($LASTEXITCODE -ne 0) { throw "Compiler failed with exit code $LASTEXITCODE" }
Write-Host "Built $(Join-Path $out 'HIMA.dll')"
