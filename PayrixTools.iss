#define MyAppName "Payrix Tools"
#define MyAppVersion "1.0"
#define MyAppPublisher "BQE Software"
#define MyAppExeName "PayrixTools.exe"
#define MySourceDir "C:\Projects\Core\PayrixLauncher\publish"

[Setup]
AppId={{A3F8C2D1-4B7E-4F9A-8C3D-2E5F1A0B9C8D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={commonpf32}\PayrixTools
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=C:\Projects\Core\PayrixLauncher\installer
OutputBaseFilename=PayrixToolsSetup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Main application (self-contained single-file, no .NET runtime needed)
Source: "{#MySourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; WebView2 full offline runtime installer — bundled, no internet required
Source: "{#MySourceDir}\MicrosoftEdgeWebview2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: WebView2NotInstalled

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Install WebView2 runtime silently (fully offline — no internet needed)
Filename: "{tmp}\MicrosoftEdgeWebview2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; \
  StatusMsg: "Installing Microsoft Edge WebView2 Runtime (offline)..."; \
  Check: WebView2NotInstalled; Flags: waituntilterminated

; Offer to launch the app after install
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function WebView2NotInstalled: Boolean;
var
  ver: String;
begin
  // Check HKLM first (machine-wide install), then HKCU (per-user install)
  Result := not RegQueryStringValue(HKLM,
    'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv', ver)
    and
    not RegQueryStringValue(HKCU,
    'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv', ver);
end;
