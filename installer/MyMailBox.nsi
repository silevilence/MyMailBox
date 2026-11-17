Unicode True
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "StrFunc.nsh"
!include "nsDialogs.nsh"
${StrStr}

!ifndef APP_VERSION
!error "APP_VERSION is required"
!endif
!ifndef PUBLISH_DIR
!error "PUBLISH_DIR is required"
!endif
!ifndef OUTPUT_FILE
!error "OUTPUT_FILE is required"
!endif
!ifndef UNINSTALL_FILES
!error "UNINSTALL_FILES is required"
!endif

Name "MyMailBox"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\MyMailBox"
InstallDirRegKey HKCU "Software\MyMailBox" "InstallDirectory"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
BrandingText "MyMailBox ${APP_VERSION} · Windows x64"

!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\MyMailBox.Desktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 MyMailBox"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_WELCOME
UninstPage custom un.DataPage un.DataPageLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Var DeleteData
Var DeleteDataCheckbox
Var DotnetPath
Var RuntimeList

Function .onInit
  ${IfNot} ${IsNativeAMD64}
    MessageBox MB_ICONSTOP "此安装包仅支持 Windows x64。"
    SetErrorLevel 1633
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 32
  ReadRegStr $DotnetPath HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64" "InstallLocation"
  ${If} $DotnetPath == ""
    StrCpy $DotnetPath "$PROGRAMFILES64\dotnet"
  ${EndIf}
  IfFileExists "$DotnetPath\dotnet.exe" 0 missingRuntime
  nsExec::ExecToStack '"$DotnetPath\dotnet.exe" --list-runtimes'
  Pop $0
  Pop $RuntimeList
  ${If} $0 != "0"
    Goto missingRuntime
  ${EndIf}
  ${StrStr} $1 $RuntimeList "Microsoft.NETCore.App 10."
  ${If} $1 == ""
    Goto missingRuntime
  ${EndIf}
  Return
missingRuntime:
  IfSilent silentMissing
  MessageBox MB_ICONEXCLAMATION|MB_YESNO "需要 .NET 10 x64 Runtime。x86 Runtime 或仅有 SDK 注册信息不能替代运行时。安装包不包含运行时，也不会自动下载。$\r$\n$\r$\n是否打开微软安装页面？" IDNO silentMissing
  ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0/runtime"
silentMissing:
  SetErrorLevel 1638
  Abort
FunctionEnd

Function .onVerifyInstDir
  GetFullPathName $0 "$INSTDIR"
  GetFullPathName $1 "$LOCALAPPDATA\MyMailBox"
  ${If} $0 == $1
    Abort
  ${EndIf}
  ${If} $0 == "$PROGRAMFILES64"
    Abort
  ${EndIf}
  ${If} $0 == "$LOCALAPPDATA"
    Abort
  ${EndIf}
  ${If} $0 == "$WINDIR"
    Abort
  ${EndIf}
FunctionEnd

Section "MyMailBox" SEC_MAIN
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\MyMailBox"
  CreateShortcut "$SMPROGRAMS\MyMailBox\MyMailBox.lnk" "$INSTDIR\MyMailBox.Desktop.exe"
  CreateShortcut "$SMPROGRAMS\MyMailBox\卸载 MyMailBox.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\MyMailBox" "InstallDirectory" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "DisplayName" "MyMailBox"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "Publisher" "MyMailBox"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox" "NoRepair" 1
  ; WebView2 is optional for plain-text use. Guide without downloading or bundling it.
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == ""
    DetailPrint "HTML 阅读需要 Microsoft Edge WebView2 Runtime；缺少时可使用纯文本。"
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  StrCpy $DeleteData 0
FunctionEnd
Function un.DataPage
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 44u "默认保留邮件、独立归档、配置和插件数据。卸载程序只删除安装包拥有的程序文件。$\r$\n自定义工作空间和导出的备份永远不会由此选项删除。"
  Pop $1
  ${NSD_CreateCheckbox} 0 54u 100% 36u "同时删除当前 Windows 用户的默认工作空间：$LOCALAPPDATA\MyMailBox（不可恢复）"
  Pop $DeleteDataCheckbox
  nsDialogs::Show
FunctionEnd
Function un.DataPageLeave
  ${NSD_GetState} $DeleteDataCheckbox $DeleteData
  ${If} $DeleteData == ${BST_CHECKED}
    MessageBox MB_ICONEXCLAMATION|MB_YESNO|MB_DEFBUTTON2 "确认永久删除默认工作空间中的邮件、归档、配置和插件数据？请先完成备份。" IDYES confirmed
    StrCpy $DeleteData 0
confirmed:
  ${EndIf}
FunctionEnd
Section "Uninstall"
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\MyMailBox\MyMailBox.lnk"
  Delete "$SMPROGRAMS\MyMailBox\卸载 MyMailBox.lnk"
  RMDir "$SMPROGRAMS\MyMailBox"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MyMailBox"
  DeleteRegKey HKCU "Software\MyMailBox"
  ${If} $DeleteData == ${BST_CHECKED}
    GetFullPathName $0 "$LOCALAPPDATA\MyMailBox"
    ${If} $0 == "$LOCALAPPDATA\MyMailBox"
      RMDir /r "$0"
    ${EndIf}
  ${EndIf}
SectionEnd

