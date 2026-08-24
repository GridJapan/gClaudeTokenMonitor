# ctm と CtmMonitor をビルドする。
# 依存: Go 1.22+ / .NET Framework 4.x（Windows 標準の csc.exe）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

New-Item -ItemType Directory -Force -Path "$root\bin" | Out-Null

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    Write-Host "Go が見つかりません。次を実行してください:" -ForegroundColor Yellow
    Write-Host "    winget install GoLang.Go"
    Write-Host "インストール完了を待ってから、新しいターミナルで build.ps1 を再実行してください。"
    exit 1
}

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

# ATOM ファームを同梱パック（自動更新・手動書き込み用）。
# atom を pio run 済みのときだけ。無ければ黙ってスキップ（機能が無効になるだけ）。
$fwSrc = "$root\atom\.pio\build\atoms3r"
if (Test-Path "$fwSrc\firmware.bin") {
    $m = Select-String -Path "$root\atom\src\main.cpp" -Pattern 'FW_VER = "([^"]+)"'
    if ($m) {
        $fwVer = $m.Matches[0].Groups[1].Value
        $fwDst = "$root\bin\atom-fw"
        New-Item -ItemType Directory -Force -Path $fwDst | Out-Null
        Copy-Item "$fwSrc\firmware.bin", "$fwSrc\bootloader.bin", "$fwSrc\partitions.bin" $fwDst -Force
        $b0 = "$env:USERPROFILE\.platformio\packages\framework-arduinoespressif32\tools\partitions\boot_app0.bin"
        if (Test-Path $b0) { Copy-Item $b0 $fwDst -Force }
        "{`"ver`": `"$fwVer`"}" | Set-Content "$fwDst\fw.json" -Encoding ascii
        Write-Host "atom-fw v$fwVer を同梱"
    }
}

Write-Host "完了:"
Get-ChildItem "$root\bin" -Filter *.exe | Format-Table Name, Length, LastWriteTime -AutoSize
