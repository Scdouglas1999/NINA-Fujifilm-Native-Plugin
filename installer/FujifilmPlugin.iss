; Inno Setup installer for the Fujifilm Native Camera Plugin for N.I.N.A.
;
; Build after a Release build has produced:
;   src\NINA.Plugins.Fujifilm\bin\x64\Release\net8.0-windows\publish
;
; Example:
;   ISCC.exe installer\FujifilmPlugin.iss

#ifndef MyAppVersion
#define MyAppVersion "3.0.2.0"
#endif

#define MyAppName "Fujifilm Native Camera Plugin for N.I.N.A."
#define MyAppPublisher "Scdouglas"
#define MyAppURL "https://github.com/Scdouglas1999/NINA-Fujifilm-Native-Plugin"
#define MyAppId "{{6E2B5A81-7C8E-4C09-9F2A-4F6A0BC6BB1E}"
#define PublishDir "..\src\NINA.Plugins.Fujifilm\bin\x64\Release\net8.0-windows\publish"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\NINA\Plugins\3.0.0\Fujifilm
DisableDirPage=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=NINA.Fujifilm.Plugin-{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\NINA.Plugins.Fujifilm.dll
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver].%n%nThis plugin provides native camera support for Fujifilm X-series and GFX cameras in N.I.N.A. (Nighttime Imaging 'N' Astronomy).%n%nThis release includes X-T2 legacy/experimental support, GFX100RF configuration, and SDK/runtime hardening.%n%nIMPORTANT: Please close N.I.N.A. before continuing.

[Files]
; Copy the verified plugin publish layout exactly. The Fujifilm SDK runtime files
; must live beside NINA.Plugins.Fujifilm.dll, not in a nested runtime folder.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MsgBox('Installation complete!' + #13#10 + #13#10 +
           'The Fujifilm plugin has been installed to:' + #13#10 +
           ExpandConstant('{app}') + #13#10 + #13#10 +
           'Please restart N.I.N.A. to load the updated plugin.',
           mbInformation, MB_OK);
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
