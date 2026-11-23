Unicode true
RequestExecutionLevel user
!include "MUI2.nsh"

!ifndef PRODUCT_VERSION
!define PRODUCT_VERSION "1.0.0"
!endif
!define PRODUCT_NAME "CHENMU Browser"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\CHENMU Browser"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "dist/CHENMU-Browser-Setup-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\CHENMU Browser"
InstallDirRegKey HKCU "Software\CHENMU Browser" "InstallDir"
SetCompressor /SOLID lzma
ShowInstDetails nevershow
ShowUninstDetails nevershow

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "CHENMU Browser（必需）" SEC_MAIN
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" /x "CHENMU-Browser-Setup-x64.exe" "dist/*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\CHENMU Browser"
  CreateShortcut "$SMPROGRAMS\CHENMU Browser\CHENMU Browser.lnk" "$INSTDIR\CHENMU Browser.exe"
  CreateShortcut "$SMPROGRAMS\CHENMU Browser\卸载 CHENMU Browser.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\CHENMU Browser" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "CHENMU Browser"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\CHENMU Browser.exe,0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section /o "创建桌面快捷方式" SEC_DESKTOP
  SetShellVarContext current
  CreateShortcut "$DESKTOP\CHENMU Browser.lnk" "$INSTDIR\CHENMU Browser.exe"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN} "为当前 Windows 用户安装 CHENMU Browser，不需要管理员权限。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "在桌面创建 CHENMU Browser 快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  SetShellVarContext current
  Delete "$DESKTOP\CHENMU Browser.lnk"
  Delete "$SMPROGRAMS\CHENMU Browser\CHENMU Browser.lnk"
  Delete "$SMPROGRAMS\CHENMU Browser\卸载 CHENMU Browser.lnk"
  RMDir "$SMPROGRAMS\CHENMU Browser"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\CHENMU Browser"
  Delete "$INSTDIR\CHENMU Browser.exe"
  Delete "$INSTDIR\CHENMU Browser.exe.config"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Core.dll"
  Delete "$INSTDIR\Microsoft.Web.WebView2.WinForms.dll"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Wpf.dll"
  Delete "$INSTDIR\WebView2Loader.dll"
  Delete "$INSTDIR\Assets\chenmu-logo.png"
  Delete "$INSTDIR\Assets\domain-blocklist.txt"
  RMDir "$INSTDIR\Assets"
  Delete "$INSTDIR\runtimes\win-x64\native\WebView2Loader.dll"
  RMDir "$INSTDIR\runtimes\win-x64\native"
  RMDir "$INSTDIR\runtimes\win-x64"
  RMDir "$INSTDIR\runtimes"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
