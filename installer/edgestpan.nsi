; EdgeSpan Windows installer. Built by scripts/publish-windows.sh, which passes
; -DVERSION from Directory.Build.props: makensis -DVERSION=x.y.z installer/edgestpan.nsi
; Expects publish/win-x64/EdgeSpan.exe plus adb and the server dex beside it.

!ifndef VERSION
  !error "Pass -DVERSION=x.y.z (see scripts/publish-windows.sh)"
!endif

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 32
Name "EdgeSpan"
OutFile "..\publish\EdgeSpan-Setup-${VERSION}.exe"
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
  ; Virtual Display Driver files (installed later from the app, only after the user agrees to the UAC prompt).
  SetOutPath "$INSTDIR\vdd"
  File /nonfatal "..\publish\win-x64\vdd\*.*"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\EdgeSpan.lnk" "$INSTDIR\EdgeSpan.exe"
  CreateShortcut "$SMPROGRAMS\EdgeSpan.lnk" "$INSTDIR\EdgeSpan.exe"
  WriteRegStr HKCU "Software\EdgeSpan" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "DisplayName" "EdgeSpan"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\EdgeSpan" "DisplayVersion" "${VERSION}"
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
