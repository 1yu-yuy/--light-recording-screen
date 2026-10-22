$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:"$PSScriptRoot\轻录屏.exe" "$PSScriptRoot\FloatRecorder.cs" "$PSScriptRoot\AudioCapture.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
