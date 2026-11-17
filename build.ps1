$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $compiler /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ('/win32icon:' + (Join-Path $PSScriptRoot 'SSMS-Classic.ico')) ('/out:' + (Join-Path $PSScriptRoot 'SSMS-ClassicIcon.exe')) (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'TaskbarStyle.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Exit the running helper before rebuilding.' }
