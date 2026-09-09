; ErbaiLiveTool（二白直播工具）Inno Setup 安装器配方
; Stage B（决策 #14 修订，2026-08-27）：FDD + Inno Setup 安装器
;   - 框架依赖（FDD，.NET 8 Desktop Runtime 由安装器自动部署，App 不内置运行时）
;   - WinAppSDK 随应用自带（WindowsAppSDKSelfContained=true，故无需部署 WindowsAppRuntime）
;   - 前置组件（.NET 8 Desktop Runtime / ASP.NET Core Runtime 8.0 / VC++ v14）
;     缺失时在线拉取并静默安装（参考 FufuLauncher setup.iss 配方；ASP.NET Core 为
;     Erbai.Web 的 Microsoft.AspNetCore.App 框架依赖，FufuLauncher 无 Kestrel 故不需要）
;   - 安装到 %LOCALAPPDATA%\Programs\（用户级安装目录，决策 #14）
;   - 随安装器创建插件目录 Plugins/ + 样例模板（Stage A 遗留 ④）
; 构建：tools/build-installer.ps1（ISCC /DSrcDir=<publish 绝对路径> /DAppVersion=<版本>）

#define AppName "ErbaiLiveTool"
#define AppDisplayName "二白直播工具"
#define AppVersion "0.1.0"
#define AppVersionNum "0.1.0"
#define AppPublisher "wxycs"
#define AppExe "ErbaiLiveTool.exe"
#define AppId "{{D1B2E44F-9A7C-4F3B-B8DE-1C6A5E209843}"
; 应用发布产物目录（dotnet publish 输出；build-installer.ps1 以 /D 覆盖为绝对路径）
#ifndef SrcDir
  #define SrcDir "..\publish"
#endif
#define OutDir "Output"

[Setup]
AppId={#AppId}
AppName={#AppDisplayName}（{#AppName}）
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 {#AppPublisher}
VersionInfoVersion={#AppVersionNum}
VersionInfoProductVersion={#AppVersionNum}
VersionInfoDescription={#AppDisplayName} 安装程序
UninstallDisplayName={#AppDisplayName}
; 安装向导图标（logo 2026-09-08）：Assets\AppLogo.ico 随仓库分发，与主程序同源
SetupIconFile=Assets\AppLogo.ico

; 用户级安装（决策 #14：安装到 %LOCALAPPDATA%\Programs\，可合规免系统目录写权限）
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
DisableDirPage=no
DisableReadyPage=no
ShowLanguageDialog=no
AllowNoIcons=yes

; x64 专属（WASDK 自包含 + 抖音抓包器均为 x64）；整体提权（运行时子安装器继承提权令牌，
; 与主程序 requireAdministrator 一致，参考 FufuLauncher）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763

OutputDir={#OutDir}
OutputBaseFilename={#AppName}_Setup_v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

WizardStyle=modern
WizardSizePercent=110
CloseApplications=force
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
; 官方社区中文翻译（Inno Setup 6.5.0+，维护者 Zhenghan Yang（Kira））
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
DownloadingDotNet=正在获取必要组件 (Microsoft .NET 8.0 桌面运行时)...
InstallingDotNet=正在部署必要组件 (Microsoft .NET 8.0 桌面运行时)，请稍候...
InstallFailed=必要组件 (.NET 8.0 桌面运行时) 部署未成功。此组件为运行该程序所必需，请稍后手动安装。主程序将继续安装。
DownloadFailed=无法获取必要组件 (.NET 8.0 桌面运行时)。请检查网络连接状态，或稍后手动完成安装。主程序将继续安装。

DownloadingAspNetCore=正在获取必要组件 (ASP.NET Core Runtime 8.0)...
InstallingAspNetCore=正在部署必要组件 (ASP.NET Core Runtime 8.0)，请稍候...
InstallFailedAspNetCore=必要组件 (ASP.NET Core Runtime 8.0) 部署未成功。此组件为内嵌服务器所必需，请稍后手动安装。主程序将继续安装。
DownloadFailedAspNetCore=无法获取必要组件 (ASP.NET Core Runtime 8.0)。请检查网络连接状态，或稍后手动完成安装。主程序将继续安装。

DownloadingVC=正在获取必要组件 (Visual C++ v14 Redistributable)...
InstallingVC=正在部署必要组件 (Visual C++ v14 Redistributable)，请稍候...
InstallFailedVC=必要组件 (Visual C++ v14 Redistributable) 部署未成功。请稍后手动安装环境，主程序将继续安装。
DownloadFailedVC=无法获取必要组件 (Visual C++ v14 Redistributable)。请检查网络连接状态，或稍后手动完成安装。

RuntimeExecFailed=必要组件安装程序无法执行。请稍后手动安装环境，主程序将继续安装。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式";   GroupDescription: "附加快捷方式:"; Check: not WizardSilent
Name: "startmenu";   Description: "创建开始菜单快捷方式"; GroupDescription: "附加快捷方式:"; Check: not WizardSilent

[Files]
; 应用全部发布产物（publish 目录，单文件 FDD + 连接器多文件 + Grabber/Profiles/Bridge 子目录）
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,obj\*"

; 插件目录模板（Stage A 遗留 ④：插件目录说明随安装器创建；
; 不再附带 hello-sample 目录——无 DLL 的样例目录在插件页显示「加载失败」且易被误操作，
; config.ini 样例改以文本形式并入 Plugins\README.txt）
Source: "plugins-template\README.txt";               DestDir: "{app}\Plugins"; Flags: ignoreversion

[Dirs]
; 插件根目录：不存在 = 宿主零副作用；卸载时仅当目录仍为空才删除（保留用户自行安装的第三方插件）
Name: "{app}\Plugins"

[Icons]
Name: "{group}\{#AppDisplayName}";         Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{group}\卸载 {#AppDisplayName}";    Filename: "{uninstallexe}"; Tasks: startmenu
Name: "{autodesktop}\{#AppDisplayName}";   Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; runascurrentuser：以安装器自身的提权令牌启动主程序。缺省 runasoriginaluser 会用
; 原始用户的非提升令牌 CreateProcess，对 requireAdministrator 的主程序报错 740
; （ERROR_ELEVATION_REQUIRED），「立即启动」必失败。与上方 [Code] Exec 前置组件
; （继承提权令牌）同一原理。桌面/开始菜单快捷方式走 Explorer ShellExecute，
; 自带 UAC 提升提示，不受影响。
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppDisplayName}"; Flags: nowait postinstall skipifsilent runascurrentuser

[InstallDelete]
; 旧版（v0.1.0 早期）安装器曾附带 hello-sample 样例目录——覆盖安装时清理
; （用户机器上可能残留 Enabled=true 的失败样例，插件页会显示「加载失败」）
Type: filesandordirs; Name: "{app}\Plugins\hello-sample"

[UninstallDelete]
; 卸载删除安装器自带的模板，保留用户数据（config.json / song_request.db / logs 不在安装清单，
; Inno 卸载不删；插件目录非空时保留用户插件）
Type: files;          Name: "{app}\Plugins\README.txt"
Type: dirifempty;     Name: "{app}\Plugins"
Type: dirifempty;     Name: "{app}"

[Code]

var
  DownloadPage: TDownloadWizardPage;

function IsDotNet8DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if FindFirst(ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if RegGetValueNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.0.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function IsAspNetCore8RuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if FindFirst(ExpandConstant('{pf64}\dotnet\shared\Microsoft.AspNetCore.App\8.0.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if RegGetValueNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.0.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function IsVCRedistInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) then
  begin
    if Installed = 1 then
      Result := True;
  end;
end;

procedure DeployPrerequisite(const AUrl, AFileName, ADownloadMsg, AInstallMsg, AFailMsg, ADownloadFailMsg: string;
  const AArgs: string);
var
  ResultCode: Integer;
begin
  DownloadPage.Clear;
  DownloadPage.Add(AUrl, AFileName, '');
  try
    DownloadPage.SetText(ExpandConstant(ADownloadMsg), '');
    DownloadPage.Download;
    DownloadPage.SetText(ExpandConstant(AInstallMsg), '');

    if Exec(ExpandConstant('{tmp}\' + AFileName), AArgs, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      if (ResultCode <> 0) and (ResultCode <> 1641) and (ResultCode <> 3010) then
      begin
        MsgBox(ExpandConstant(AFailMsg), mbError, MB_OK);
      end;
    end
    else
    begin
      MsgBox(ExpandConstant('{cm:RuntimeExecFailed}'), mbError, MB_OK);
    end;
  except
    if not DownloadPage.AbortedByUser then
      MsgBox(ExpandConstant(ADownloadFailMsg), mbError, MB_OK);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = wpReady then
  begin
    if (not IsDotNet8DesktopRuntimeInstalled()) or (not IsAspNetCore8RuntimeInstalled()) or
       (not IsVCRedistInstalled()) then
    begin
      DownloadPage.Show;
      try
        if not IsDotNet8DesktopRuntimeInstalled() then
          DeployPrerequisite('https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
            'dotnet-desktop-runtime.exe', '{cm:DownloadingDotNet}', '{cm:InstallingDotNet}',
            '{cm:InstallFailed}', '{cm:DownloadFailed}', '/install /passive /norestart');

        if not IsAspNetCore8RuntimeInstalled() then
          DeployPrerequisite('https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe',
            'aspnetcore-runtime.exe', '{cm:DownloadingAspNetCore}', '{cm:InstallingAspNetCore}',
            '{cm:InstallFailedAspNetCore}', '{cm:DownloadFailedAspNetCore}', '/install /passive /norestart');

        if not IsVCRedistInstalled() then
          DeployPrerequisite('https://aka.ms/vs/17/release/vc_redist.x64.exe',
            'vc_redist.x64.exe', '{cm:DownloadingVC}', '{cm:InstallingVC}',
            '{cm:InstallFailedVC}', '{cm:DownloadFailedVC}', '/install /passive /norestart');
      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;

const
  HWND_TOPMOST = -1;
  HWND_NOTOPMOST = -2;
  SWP_NOSIZE = $1;
  SWP_NOMOVE = $2;
  SWP_NOACTIVATE = $10;

{ 以下 Win32 API 非内置支持函数，经 external 声明引入 }
procedure SetWindowPos(Wnd: HWND; WndInsertAfter: HWND; X, Y, cx, cy: Integer; Flags: UINT);
  external 'SetWindowPos@user32.dll stdcall';
procedure SetForegroundWindow(Wnd: HWND);
  external 'SetForegroundWindow@user32.dll stdcall';
function GetForegroundWindow: HWND;
  external 'GetForegroundWindow@user32.dll stdcall';
function GetWindowThreadProcessId(Wnd: HWND; ProcessId: DWORD): DWORD;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function AttachThreadInput(IdAttach: DWORD; IdAttachTo: DWORD; Attach: DWORD): DWORD;
  external 'AttachThreadInput@user32.dll stdcall';
function GetCurrentThreadId: DWORD;
  external 'GetCurrentThreadId@kernel32.dll stdcall';

procedure ForceWindowToFront(Wnd: HWND);
var
  ForegroundWnd: HWND;
  ForeThreadId: DWORD;
  CurThreadId: DWORD;
begin
  { 提权 consent/UAC 弹窗会让安装器失焦且 Windows 不把前台权还给向导：
    打开时窗口可能在别的窗口之下。TOPMOST→NOTOPMOST 提 Z 序 + SetForegroundWindow
    （借 AttachThreadInput 规避前台锁定），双保险确保向导可见。 }
  SetWindowPos(Wnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE);
  SetWindowPos(Wnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE or SWP_NOSIZE or SWP_NOACTIVATE);
  ForegroundWnd := GetForegroundWindow;
  ForeThreadId := GetWindowThreadProcessId(ForegroundWnd, 0);
  CurThreadId := GetCurrentThreadId;
  if ForeThreadId <> CurThreadId then
    AttachThreadInput(CurThreadId, ForeThreadId, 1);
  SetForegroundWindow(Wnd);
  if ForeThreadId <> CurThreadId then
    AttachThreadInput(CurThreadId, ForeThreadId, 0);
end;

procedure ApplyCustomFontToControl(C: TControl);
var
  I: Integer;
  F: string;
begin
  F := 'Microsoft YaHei UI';
  if C is TLabel              then TLabel(C).Font.Name := F
  else if C is TNewStaticText then TNewStaticText(C).Font.Name := F
  else if C is TNewCheckListBox then TNewCheckListBox(C).Font.Name := F
  else if C is TNewListBox    then TNewListBox(C).Font.Name := F
  else if C is TNewMemo       then TNewMemo(C).Font.Name := F
  else if C is TNewEdit       then TNewEdit(C).Font.Name := F
  else if C is TNewComboBox   then TNewComboBox(C).Font.Name := F
  else if C is TNewCheckBox   then TNewCheckBox(C).Font.Name := F
  else if C is TNewRadioButton then TNewRadioButton(C).Font.Name := F
  else if C is TNewButton     then TNewButton(C).Font.Name := F
  else if C is TButton        then TButton(C).Font.Name := F
  else if C is TNewProgressBar then
  begin
    { 进度条保留默认字体（与 FufuLauncher 相同；空 then 分支显式化，避免疑似空语句争议） }
  end
  else if C is TForm          then TForm(C).Font.Name := F;

  if C is TWinControl then
    for I := 0 to TWinControl(C).ControlCount - 1 do
      ApplyCustomFontToControl(TWinControl(C).Controls[I]);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  ApplyCustomFontToControl(WizardForm);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { 向导窗口已显示后再置前（InitializeWizard 阶段窗口尚未 Show，置前无效） }
  if CurPageID = wpWelcome then
    ForceWindowToFront(WizardForm.Handle);
end;

procedure InitializeUninstallProgressForm;
begin
  ApplyCustomFontToControl(UninstallProgressForm);
end;
