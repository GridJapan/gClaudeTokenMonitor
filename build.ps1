# ctm と CtmMonitor をビルドする。
# 依存: Go 1.22+ / .NET Framework 4.x（Windows 標準の csc.exe）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

New-Item -ItemType Directory -Force -Path "$root\bin" | Out-Null

Write-Host "ctm.exe をビルド中..."
go build -ldflags "-s -w" -o "bin\ctm.exe" .

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (Test-Path $csc) {
    Write-Host "CtmMonitor.exe をビルド中..."
    & $csc -nologo -target:winexe -out:"bin\CtmMonitor.exe" -platform:anycpu -optimize+ `
        -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll `
        "app\CtmMonitor.cs"
} else {
    Write-Warning "csc.exe が見つからないため CtmMonitor.exe はスキップ"
}

Write-Host "完了:"
Get-ChildItem "$root\bin" -Filter *.exe | Format-Table Name, Length, LastWriteTime -AutoSize
