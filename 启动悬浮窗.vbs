' 直接启动 Release 版 EXE，不弹出黑色控制台窗口。
Dim fso, shell, exePath
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

exePath = fso.GetParentFolderName(WScript.ScriptFullName) & "\bin\Release\net8.0-windows\MinecraftChatOverlay.exe"

If fso.FileExists(exePath) Then
    shell.Run """" & exePath & """", 0, False
Else
    MsgBox "未找到程序文件：" & vbCrLf & exePath & vbCrLf & vbCrLf & "请先编译 Release 版本。", vbExclamation, "MinecraftChatOverlay"
End If
