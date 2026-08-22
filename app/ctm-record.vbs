' ctm の常時記録をコンソールウィンドウなしで起動する。
' スタートアップフォルダに置く場合は、下の EXE パスを実際の場所に書き換えること。
Option Explicit
Dim fso, sh, exePath
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")

' 既定ではこのスクリプトと同じ階層の ..\bin\ctm.exe を使う。
exePath = fso.BuildPath(fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName)), "bin\ctm.exe")
If Not fso.FileExists(exePath) Then
    ' スタートアップフォルダから起動された場合はここを実際のパスに変更する。
    exePath = "C:\claude\dev\gjClaudeTokenMonitor\bin\ctm.exe"
End If

sh.Run """" & exePath & """ record -quiet", 0, False
