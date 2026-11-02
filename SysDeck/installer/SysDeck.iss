#define MyAppName "SysDeck"
#define MyAppVersion "1.9.4"
#define MyAppPublisher "Fenix"
#define MyAppExeName "SysDeck.exe"

[Setup]
AppId={{DAFC493C-D04F-4A9C-8B9A-AED16CD8DF67}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Installation par utilisateur : aucune élévation admin n'est demandée, ni à
; l'installation ni au lancement (le compte Windows courant est un compte
; standard, pas administrateur).
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=SysDeckSetup
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"
Name: "startupicon"; Description: "Lancer SysDeck au démarrage de Windows"; GroupDescription: "Options :"; Flags: unchecked
Name: "cputemp"; Description: "Température du processeur via AMD Ryzen Master SDK (demande une autorisation administrateur)"; GroupDescription: "Options :"

[Files]
Source: "..\bin\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Pont de température CPU : copié dans ProgramData et lancé par une tâche planifiée (voir [Code]).
Source: "..\Tools\CpuTempBridge.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion
Source: "..\Tools\InstallCpuTempTask.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Désinstaller {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SysDeck"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Lancer {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[UninstallRun]
; Supprime la tâche de température (création élevée, donc suppression élevée : une demande UAC).
Filename: "schtasks.exe"; Parameters: "/Delete /TN SysDeck-CpuTempBridge /F"; Flags: shellexec runhidden waituntilterminated; Verb: "runas"; RunOnceId: "DeleteCpuTempTask"

[Code]
const
  RyzenMasterCli = 'C:\Program Files\AMD\RyzenMasterSDK\AMDRyzenMasterCLI\bin-prebuilt\AMDRyzenMasterCLI.exe';

procedure InstallCpuTempTask();
var
  Params: String;
  ResultCode: Integer;
begin
  if not FileExists(RyzenMasterCli) then
    MsgBox('Le SDK AMD Ryzen Master n''est pas installé sur ce PC.' + #13#10 + #13#10 +
      'La tâche de température va quand même être créée, mais la température du processeur ' +
      'ne s''affichera qu''une fois le SDK installé (téléchargeable sur le site d''AMD).',
      mbInformation, MB_OK);

  // Compte de la session (domaine\nom), lu avant l'élévation : élevé, le script pourrait tourner
  // sous un autre compte administrateur.
  Params := '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' +
    ExpandConstant('{app}\Tools\InstallCpuTempTask.ps1') + '" -User "' +
    ExpandConstant('{%USERDOMAIN}\{%USERNAME}') + '" -Source "' +
    ExpandConstant('{app}\Tools\CpuTempBridge.ps1') + '"';

  if not ShellExec('runas', 'powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     or (ResultCode <> 0) then
    MsgBox('La tâche de température du processeur n''a pas pu être créée ' +
      '(autorisation refusée ou erreur).' + #13#10 +
      'SysDeck fonctionne quand même ; relancez l''installation pour réessayer.',
      mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('cputemp') then
    InstallCpuTempTask();
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Ferme toute instance en cours avant de copier les fichiers, pour éviter
  // une erreur de fichier verrouillé si SysDeck tourne déjà.
  Exec('taskkill.exe', '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
