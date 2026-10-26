; EdgeSpan Windows installer. Build on Linux with: makensis installer/edgestpan.nsi
; Expects publish/win-x64/EdgeSpan.exe plus adb and the server dex beside it.

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 32
Name "EdgeSpan"
OutFile "..\publish\EdgeSpan-Setup.exe"
InstallDir "$LOCALAPPDATA\EdgeSpan"
RequestExecutionLevel user
InstallDirRegKey HKCU "Software\EdgeSpan" "InstallDir"

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

Section "EdgeSpan"
  SetOutPath "$INSTDIR"
  File "..\publish\win-x64\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\EdgeSpan.lnk" "$INSTDIR\EdgeSpan.exe"
  CreateShortcut "$SMPROGRAMS\EdgeSpan.lnk" "$INSTDIR\EdgeSpan.exe"
  WriteRegStr HKCU "Software\EdgeSpan" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "DisplayName" "EdgeSpan"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "DisplayVersion" "0.9.12"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$INSTDIR\*.*"
  Delete "$DESKTOP\EdgeSpan.lnk"
  Delete "$SMPROGRAMS\EdgeSpan.lnk"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan"
  DeleteRegKey HKCU "Software\EdgeSpan"
  RMDir "$INSTDIR"
SectionEnd
