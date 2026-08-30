; ==========================================================================
;  STORM REMOTE CONTROL — Inno Setup Installer Script
;  Version : 0.6.1
;  Author  : Storm Software
;  Created : 2026-06-04
; ==========================================================================

#define MyAppName        "STORM REMOTE CONTROL"
#define MyAppVersion     "0.6.4"
#define MyAppPublisher   "Storm Software"
#define MyAppURL         "https://stormsoftware.dev"
#define MyAppExeName     "STORM REMOTE CONTROL.exe"
#define MyAppMutex       "StormRemoteControl_SingleInstance_Mutex"

; Paths are relative to this .iss file's location
#define ProjectRoot      "..\StormRemoteControl\StormRemoteControl"
#define PublishDir       ProjectRoot + "\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#define AppIcon          ProjectRoot + "\Assets\AppIcon.ico"

[Setup]
; Basic identity
AppId={{E8A3F2D1-7C4B-4E9A-B6D0-3F8C1A2E5D74}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
VersionInfoCopyright=Copyright (c) 2026 {#MyAppPublisher}

; Install directories
DefaultDirName={autopf}\StormRemoteControl
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output
OutputDir=E:\STORM REMOTE CONTROL\installer_output
OutputBaseFilename=StormRemoteControl_Setup_{#MyAppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\StormRemoteControl.exe
UninstallDisplayName={#MyAppName}

; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Wizard appearance — modern style
WizardStyle=modern
WizardSizePercent=110,110

; Privileges & platform
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 21H2 (build 19044) minimum
MinVersion=10.0.19044

; Misc
AllowNoIcons=yes
CloseApplications=yes
RestartApplications=no
AppMutex={#MyAppMutex}
SetupMutex=StormRemoteControlSetup_Mutex
ShowLanguageDialog=auto
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
SetupLogging=yes

; Uninstaller
UninstallFilesDir={app}
CreateUninstallRegKey=not IsPortableMode
Uninstallable=not IsPortableMode

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; ═══ Mode selection page ═══
russian.ModePageTitle=Выбор режима
russian.ModePageDescription=Выберите режим установки STORM REMOTE CONTROL
russian.ModeQuestion=Как вы хотите использовать программу?
russian.ModeInstall=Установка в систему (рекомендуется)
russian.ModeInstallDesc=• Устанавливает в Program Files%n• Создаёт ярлыки и запись в реестре%n• Поддержка автозапуска при старте Windows%n• Правила брандмауэра настраиваются автоматически%n• Удаление через «Параметры → Приложения»
russian.ModePortable=Портативная версия (без установки)
russian.ModePortableDesc=• Извлекает файлы в указанную папку%n• Никаких записей в реестре%n• Можно запускать с USB-накопителя%n• Для удаления просто удалите папку

english.ModePageTitle=Installation Mode
english.ModePageDescription=Choose how to install STORM REMOTE CONTROL
english.ModeQuestion=How would you like to use the application?
english.ModeInstall=Install to system (recommended)
english.ModeInstallDesc=• Installs to Program Files%n• Creates shortcuts and registry entries%n• Supports auto-start with Windows%n• Firewall rules configured automatically%n• Uninstall via Settings → Apps
english.ModePortable=Portable version (no installation)
english.ModePortableDesc=• Extracts files to the specified folder%n• No registry entries%n• Can be run from a USB drive%n• To remove, simply delete the folder

; ═══ Tasks ═══
russian.TaskAutostart=Запускать при старте Windows
russian.TaskAutostartGroup=Автозапуск:
english.TaskAutostart=Start with Windows
english.TaskAutostartGroup=Auto-start:

; ═══ Windows version check ═══
russian.OldWindowsError=STORM REMOTE CONTROL требует Windows 10 версии 21H2 (сборка 19044) или новее.%n%nПожалуйста, обновите Windows и попробуйте снова.
english.OldWindowsError=STORM REMOTE CONTROL requires Windows 10 version 21H2 (build 19044) or later.%n%nPlease update Windows and try again.

; ═══ Shortcuts ═══
russian.ShortcutLaunch=Запуск {#MyAppName}
russian.ShortcutUninstall=Удалить {#MyAppName}
english.ShortcutLaunch=Launch {#MyAppName}
english.ShortcutUninstall=Uninstall {#MyAppName}

; ═══ Tray context menu (future) ═══
russian.TrayShow=Развернуть
russian.TrayExit=Выход
english.TrayShow=Show
english.TrayExit=Exit

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; Check: not IsPortableMode
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: (not IsAdminInstallMode) and (not IsPortableMode)
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:TaskAutostartGroup}"; Check: not IsPortableMode

[Files]
; Main application files — recursively copy entire publish folder
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; App icon for shortcuts (copied separately so it's always available)
Source: "{#AppIcon}"; DestDir: "{app}"; DestName: "AppIcon.ico"; Flags: ignoreversion

; Portable marker file
Source: "portable.marker"; DestDir: "{app}"; DestName: "portable.dat"; Flags: ignoreversion; Check: IsPortableMode

[Icons]
; Start Menu shortcuts
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AppIcon.ico"; Comment: "{cm:ShortcutLaunch}"; Check: not IsPortableMode
Name: "{group}\{cm:ShortcutUninstall}"; Filename: "{uninstallexe}"; IconFilename: "{app}\AppIcon.ico"; Comment: "{cm:ShortcutUninstall}"; Check: not IsPortableMode

; Desktop shortcut (optional — user must check the checkbox)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AppIcon.ico"; Comment: "{cm:ShortcutLaunch}"; Tasks: desktopicon; Check: not IsPortableMode

[Registry]
; App Paths registration — allows launching via Win+R / Run dialog
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey; Check: not IsPortableMode
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey; Check: not IsPortableMode

; Application registration in registry
Root: HKLM; Subkey: "SOFTWARE\{#MyAppPublisher}\{#MyAppName}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey; Check: not IsPortableMode
Root: HKLM; Subkey: "SOFTWARE\{#MyAppPublisher}\{#MyAppName}"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey; Check: not IsPortableMode

; Autostart — run at Windows startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "StormRemoteControl"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart; Check: not IsPortableMode

[Run]
; Offer to launch the app after installation
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up any files/dirs the app may have created at runtime
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\cache"
Type: filesandordirs; Name: "{app}\settings"
Type: dirifempty; Name: "{app}"

; Clean up user-local app data
Type: filesandordirs; Name: "{localappdata}\StormRemoteControl"

[UninstallRun]
; Kill the app before uninstalling (prevents locked files)
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
var
  ModePage: TWizardPage;
  InstallModeBtn: TNewRadioButton;
  PortableModeBtn: TNewRadioButton;
  PortableSelected: Boolean;

function IsPortableMode: Boolean;
begin
  Result := PortableSelected;
end;

procedure InitializeWizard;
var
  TitleLabel: TNewStaticText;
  DescInstall, DescPortable: TNewStaticText;
  Separator: TBevel;
begin
  PortableSelected := False;

  // Create mode selection page — appears FIRST (before dir selection)
  ModePage := CreateCustomPage(wpWelcome,
    CustomMessage('ModePageTitle'),
    CustomMessage('ModePageDescription'));

  // Title
  TitleLabel := TNewStaticText.Create(ModePage);
  TitleLabel.Parent := ModePage.Surface;
  TitleLabel.Caption := CustomMessage('ModeQuestion');
  TitleLabel.Font.Size := 10;
  TitleLabel.Font.Style := [fsBold];
  TitleLabel.Left := 0;
  TitleLabel.Top := 8;

  // OPTION 1: Full Install
  InstallModeBtn := TNewRadioButton.Create(ModePage);
  InstallModeBtn.Parent := ModePage.Surface;
  InstallModeBtn.Caption := CustomMessage('ModeInstall');
  InstallModeBtn.Font.Size := 10;
  InstallModeBtn.Font.Style := [fsBold];
  InstallModeBtn.Left := 0;
  InstallModeBtn.Top := 48;
  InstallModeBtn.Width := ModePage.SurfaceWidth;
  InstallModeBtn.Checked := True;

  DescInstall := TNewStaticText.Create(ModePage);
  DescInstall.Parent := ModePage.Surface;
  DescInstall.WordWrap := True;
  DescInstall.Caption := CustomMessage('ModeInstallDesc');
  DescInstall.Left := 20;
  DescInstall.Top := 72;
  DescInstall.Width := ModePage.SurfaceWidth - 40;
  DescInstall.Font.Color := $00888888;

  // Separator
  Separator := TBevel.Create(ModePage);
  Separator.Parent := ModePage.Surface;
  Separator.Left := 0;
  Separator.Top := 162;
  Separator.Width := ModePage.SurfaceWidth;
  Separator.Height := 2;
  Separator.Shape := bsTopLine;

  // OPTION 2: Portable
  PortableModeBtn := TNewRadioButton.Create(ModePage);
  PortableModeBtn.Parent := ModePage.Surface;
  PortableModeBtn.Caption := CustomMessage('ModePortable');
  PortableModeBtn.Font.Size := 10;
  PortableModeBtn.Font.Style := [fsBold];
  PortableModeBtn.Left := 0;
  PortableModeBtn.Top := 176;
  PortableModeBtn.Width := ModePage.SurfaceWidth;

  DescPortable := TNewStaticText.Create(ModePage);
  DescPortable.Parent := ModePage.Surface;
  DescPortable.WordWrap := True;
  DescPortable.Caption := CustomMessage('ModePortableDesc');
  DescPortable.Left := 20;
  DescPortable.Top := 200;
  DescPortable.Width := ModePage.SurfaceWidth - 40;
  DescPortable.Font.Color := $00888888;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = ModePage.ID then
  begin
    PortableSelected := PortableModeBtn.Checked;
    if PortableSelected then
    begin
      WizardForm.DirEdit.Text := ExpandConstant('{userdesktop}\STORM REMOTE CONTROL');
    end
    else
    begin
      WizardForm.DirEdit.Text := ExpandConstant('{autopf}\StormRemoteControl');
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  // Skip Tasks page in portable mode (no shortcuts/autostart)
  if (PageID = wpSelectTasks) and IsPortableMode then
    Result := True;
  // Skip program group page in portable mode
  if (PageID = wpSelectProgramGroup) and IsPortableMode then
    Result := True;
end;

function IsWindowsVersionOrGreater(Major, Minor, Build: Integer): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major > Major) or
            ((Version.Major = Major) and (Version.Minor > Minor)) or
            ((Version.Major = Major) and (Version.Minor = Minor) and (Version.Build >= Build));
end;

function InitializeSetup(): Boolean;
begin
  // Require Windows 10 build 19044 (21H2) or later
  if not IsWindowsVersionOrGreater(10, 0, 19044) then
  begin
    MsgBox(CustomMessage('OldWindowsError'), mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
  Result := True;
end;

// Firewall rules only for full install
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not IsPortableMode) then
  begin
    Exec('netsh', 'advfirewall firewall add rule name="STORM Remote Control" dir=in action=allow program="' + ExpandConstant('{app}\{#MyAppExeName}') + '" enable=yes profile=any', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('netsh', 'advfirewall firewall add rule name="STORM Remote Control" dir=out action=allow program="' + ExpandConstant('{app}\{#MyAppExeName}') + '" enable=yes profile=any', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove firewall rules
    Exec('netsh', 'advfirewall firewall delete rule name="STORM Remote Control"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // Remove leftover empty directories
    RemoveDir(ExpandConstant('{app}'));
  end;
end;
