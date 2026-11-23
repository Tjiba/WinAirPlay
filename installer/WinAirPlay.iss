#ifndef AppId
    #define AppId "WinAirPlay"
#endif

[Setup]
AppId={#AppId}
AppName=WinAirPlay
AppVersion={#AppVersion}
AppPublisher=Timo
AppPublisherURL=https://github.com/Tjiba/WinAirPlay
DefaultDirName={localappdata}\Programs\WinAirPlay
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableWelcomePage=no
WizardStyle=modern dark
SetupIconFile=..\assets\WinAirPlay.ico
UninstallDisplayIcon={app}\WinAirPlay.exe
OutputDir=..\dist
OutputBaseFilename=WinAirPlay-Setup
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=WinAirPlay.exe
RestartApplications=no
SetupMutex={#AppId}.Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Messages]
english.FinishedLabel=WinAirPlay is installed. Open it from the Start menu. Your saved settings are preserved.
french.FinishedLabel=WinAirPlay est installé. Retrouvez-le dans le menu Démarrer. Vos réglages sont conservés.

[Files]
Source: "..\build\publish\WinAirPlay.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppId}"; Filename: "{app}\WinAirPlay.exe"

[InstallDelete]
Type: files; Name: "{app}\Uninstall.ps1"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppId}"; ValueData: """{app}\WinAirPlay.exe"" --startup"; Flags: uninsdeletevalue; Check: HasStartupEntry

[Run]
Filename: "{app}\WinAirPlay.exe"; Description: "{cm:LaunchProgram,WinAirPlay}"; Flags: nowait postinstall skipifsilent

[Code]
function HasStartupEntry: Boolean;
begin
    Result := RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppId}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
    if CurStep <> ssPostInstall then Exit;
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}');
end;

function InitializeUninstall: Boolean;
begin
    Result := True;
    if not CheckForMutexes('Local\{#AppId}.Native.v2') then Exit;
    if ActiveLanguage = 'french' then
        MsgBox('Quittez WinAirPlay depuis son icône près de l''horloge, puis relancez la désinstallation.', mbInformation, MB_OK)
    else
        MsgBox('Quit WinAirPlay from its tray icon, then uninstall again.', mbInformation, MB_OK);
    Result := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
    if CurUninstallStep <> usUninstall then Exit;
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppId}');
end;
