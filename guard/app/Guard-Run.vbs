' Guard-Run.vbs <script.ps1> [args...]
' Runs an NT8 Guard script with NO window. Task Scheduler starts wscript.exe (which has no console),
' and window style 0 keeps PowerShell hidden, so nothing flashes on screen every minute.
' Finds the scripts next to itself, so it works from any install folder.
Dim shell, fso, base, cmd, i
Set fso = CreateObject("Scripting.FileSystemObject")
base = fso.GetParentFolderName(WScript.ScriptFullName)
If WScript.Arguments.Count < 1 Then WScript.Quit 2
cmd = "powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & _
      base & "\" & WScript.Arguments(0) & """"
For i = 1 To WScript.Arguments.Count - 1
    cmd = cmd & " """ & WScript.Arguments(i) & """"
Next
Set shell = CreateObject("WScript.Shell")
' 0 = hidden window, False = do not wait
shell.Run cmd, 0, False
