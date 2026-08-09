; ============================================================
;  SeeMe installer script (NSIS 3.x, MUI2)
;  Usage : makensis /DAPP_VERSION=x.y.z tools\SeeMe-installer.nsi
;          （版本号由 publish-installer.cmd 从 SeeMe.csproj 自动读取注入；
;           不传 /D 时回退到下方默认值）
;  Input : D:\allll\SeeMeOut\  (dotnet publish single-file output)
;  Output: D:\allll\SeeMe-Setup-${APP_VERSION}.exe
;  Assets: tools\installer-assets\  (see make_installer_assets.py)
;  注意  : 本文件必须为 UTF-8 WITH BOM，否则中文文本编译失败
;          (makensis 依据 BOM 识别编码)
; ============================================================

!define APP_NAME        "SeeMe"
!ifndef APP_VERSION
!define APP_VERSION     "1.0.0"
!endif
!define APP_PUBLISHER   "SeeMe"
!define APP_EXE         "SeeMe.exe"
!define PUBLISH_DIR     "D:\allll\SeeMeOut"
!define INSTALL_DIR     "$PROGRAMFILES64\${APP_NAME}"
!define UNINST_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "D:\allll\SeeMe-Setup-${APP_VERSION}.exe"
InstallDir "${INSTALL_DIR}"
InstallDirRegKey HKLM "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel admin
Unicode true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "WinMessages.nsh"

; ---------- SeeMe theme (accent #6366F1, light bg #F1F5F9) ----------
!define MUI_BGCOLOR "F1F5F9"                        ; page background
!define MUI_TEXTCOLOR "334155"                      ; welcome page text color
!define MUI_WELCOMEFINISHPAGE_BITMAP "installer-assets\welcome.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP_NOSTRETCH

; ---------- 页面文字（分段留白，避免拥挤） ----------
!define MUI_WELCOMEPAGE_TITLE "欢迎使用 SeeMe"
!define MUI_WELCOMEPAGE_TEXT "欢迎使用 SeeMe ${APP_VERSION} 安装向导。$\r$\n$\r$\n本向导将为您安装 SeeMe 文档查看器，支持 Markdown、PDF、Word、Excel 与 PPT 预览。$\r$\n$\r$\n点击「下一步」继续。您可以在下一步自由选择安装位置；建议安装前关闭正在运行的 SeeMe。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "选择安装位置"
!define MUI_DIRECTORYPAGE_TEXT_DESTINATION "SeeMe 将被安装到以下文件夹。$\r$\n$\r$\n点击「浏览」可选择其他位置，安装到任意目录。"
!define MUI_FINISHPAGE_TITLE "安装完成"
!define MUI_FINISHPAGE_TEXT "SeeMe 已成功安装到您的电脑。$\r$\n$\r$\n您可以通过开始菜单或桌面快捷方式启动 SeeMe。$\r$\n$\r$\n点击「完成」退出安装向导。"

!define MUI_ABORTWARNING
!define MUI_ICON "D:\SeeMe\app_icon.ico"
!define MUI_UNICON "D:\SeeMe\app_icon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 SeeMe"

; 欢迎页/完成页 SHOW 回调：放大文字字号以加大行距（MUI2 固定 8pt 导致拥挤）
!define MUI_PAGE_CUSTOMFUNCTION_SHOW "OnWelcomePageShow"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_SHOW "OnFinishPageShow"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

Section "Install" SecMain
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"

  ; Start menu + desktop shortcuts
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  ; Uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Registry (Add/Remove Programs)
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" 78000
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKLM "${UNINST_KEY}"
SectionEnd

; ---------- 页面文字排版回调（放大字号 → 行距随之加大，缓解拥挤） ----------
Function OnWelcomePageShow
  CreateFont $0 "$(^Font)" "11" "400"
  SendMessage $mui.WelcomePage.Text ${WM_SETFONT} $0 1
  CreateFont $1 "$(^Font)" "13" "700"
  SendMessage $mui.WelcomePage.Title ${WM_SETFONT} $1 1
FunctionEnd

Function OnFinishPageShow
  CreateFont $0 "$(^Font)" "11" "400"
  SendMessage $mui.FinishPage.Text ${WM_SETFONT} $0 1
FunctionEnd
