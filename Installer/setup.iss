; ═══════════════════════════════════════════════════════════
;  STORM REMOTE CONTROL — Inno Setup Installer Script
;  Version: 0.5.5
; ═══════════════════════════════════════════════════════════

#define MyAppName "STORM REMOTE CONTROL"
#define MyAppVersion "0.5.5"
#define MyAppPublisher "STORM Software"
#define MyAppURL "https://storm-remote.app"
#define MyAppExeName "StormRemoteControl.exe"
#define MyAppId "{{B7A3F1E2-9C4D-4E8B-A5F6-7D2E1B0C3A4F}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=E:\STORM REMOTE CONTROL\installer_output
OutputBaseFilename=StormRemoteControl_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
VersionInfoCopyright=Copyright (c) 2026 STORM Software
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763
; Allow non-admin for portable mode
CreateUninstallRegKey=not IsPortableMode
Uninstallable=not IsPortableMode

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; Check: not IsPortableMode
Name: "autostart"; Description: "Запускать при старте Windows"; GroupDescription: "Дополнительно:"; Check: not IsPortableMode

[Files]
Source: "E:\STORM REMOTE CONTROL\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Portable marker file
Source: "E:\STORM REMOTE CONTROL\installer\portable.marker"; DestDir: "{app}"; DestName: "portable.dat"; Flags: ignoreversion; Check: IsPortableMode

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: not IsPortableMode
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"; Check: not IsPortableMode
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Check: not IsPortableMode

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "StormRemoteControl"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart; Check: not IsPortableMode
Root: HKLM; Subkey: "Software\StormRemoteControl"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey; Check: not IsPortableMode

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\cache"
Type: dirifempty; Name: "{app}"

[UninstallRun]
Filename: "reg"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""StormRemoteControl"" /f"; Flags: runhidden; RunOnceId: "RemoveAutostart"

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
    'Выбор режима',
    'Выберите режим установки STORM REMOTE CONTROL');

  // Title
  TitleLabel := TNewStaticText.Create(ModePage);
  TitleLabel.Parent := ModePage.Surface;
  TitleLabel.Caption := 'Как вы хотите использовать программу?';
  TitleLabel.Font.Size := 10;
  TitleLabel.Font.Style := [fsBold];
  TitleLabel.Left := 0;
  TitleLabel.Top := 8;

  // OPTION 1: Full Install
  InstallModeBtn := TNewRadioButton.Create(ModePage);
  InstallModeBtn.Parent := ModePage.Surface;
  InstallModeBtn.Caption := 'Установка в систему (рекомендуется)';
  InstallModeBtn.Font.Size := 10;
  InstallModeBtn.Font.Style := [fsBold];
  InstallModeBtn.Left := 0;
  InstallModeBtn.Top := 48;
  InstallModeBtn.Width := ModePage.SurfaceWidth;
  InstallModeBtn.Checked := True;

  DescInstall := TNewStaticText.Create(ModePage);
  DescInstall.Parent := ModePage.Surface;
  DescInstall.WordWrap := True;
  DescInstall.Caption :=
    '• Устанавливает в Program Files' + #13#10 +
    '• Создаёт ярлыки и запись в реестре' + #13#10 +
    '• Поддержка автозапуска при старте Windows' + #13#10 +
    '• Правила брандмауэра настраиваются автоматически' + #13#10 +
    '• Удаление через «Параметры → Приложения»';
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
  PortableModeBtn.Caption := 'Портативная версия (без установки)';
  PortableModeBtn.Font.Size := 10;
  PortableModeBtn.Font.Style := [fsBold];
  PortableModeBtn.Left := 0;
  PortableModeBtn.Top := 176;
  PortableModeBtn.Width := ModePage.SurfaceWidth;

  DescPortable := TNewStaticText.Create(ModePage);
  DescPortable.Parent := ModePage.Surface;
  DescPortable.WordWrap := True;
  DescPortable.Caption :=
    '• Извлекает файлы в указанную папку' + #13#10 +
    '• Никаких записей в реестре' + #13#10 +
    '• Можно запускать с USB-накопителя' + #13#10 +
    '• Для удаления просто удалите папку';
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
      // For portable: default to a subfolder on desktop or current dir
      WizardForm.DirEdit.Text := ExpandConstant('{userdesktop}\STORM REMOTE CONTROL');
    end
    else
    begin
      WizardForm.DirEdit.Text := ExpandConstant('{autopf}\STORM REMOTE CONTROL');
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
    Exec('netsh', 'advfirewall firewall delete rule name="STORM Remote Control"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
