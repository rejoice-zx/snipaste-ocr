param([string] $OutputPath = (Join-Path $PSScriptRoot 'SnipasteOcr.exe'))

$ErrorActionPreference = 'Stop'

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$source = Join-Path $PSScriptRoot 'SnipasteOcr.cs'
$providers = Join-Path $PSScriptRoot 'Providers.cs'
$icon = Join-Path $PSScriptRoot 'SnipasteOcr.ico'
$ocrScript = Join-Path $PSScriptRoot 'recognize.ps1'

$frameworkAssemblies = 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Net.Http.dll', 'System.Web.Extensions.dll', 'System.Security.dll'
$references = $frameworkAssemblies | ForEach-Object { "/reference:$(Join-Path $framework $_)" }

& $compiler /nologo /optimize+ /target:winexe /platform:x64 /codepage:65001 @references "/win32icon:$icon" "/resource:$ocrScript,SnipasteOcr.recognize.ps1" "/out:$OutputPath" $source $providers
if ($LASTEXITCODE -ne 0) { throw 'SnipasteOcr build failed.' }
Get-Item -LiteralPath $OutputPath | Select-Object FullName, Length
