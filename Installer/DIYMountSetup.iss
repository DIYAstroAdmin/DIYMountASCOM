; Inno Setup script for the DIYAstroMount ASCOM Telescope local server driver.
;
; Build with Installer\build-installer.ps1, or compile this file directly with Inno Setup 6
; (ISCC.exe) after building the Release configuration of DIYMountASCOM.csproj.
;
; The installer:
;   - requires ASCOM Platform 7 or later (the driver implements ITelescopeV4),
;   - copies the local server to Common Files\ASCOM\Telescope\DIYAstroMount,
;   - runs "/regserver" so the driver appears in the ASCOM Chooser, and
;   - runs "/unregserver" on uninstall.
; The ASCOM Platform assemblies the driver references are installed in the GAC by the Platform
; and are deliberately not redistributed.

#define MyAppName        "ASCOM DIYAstroMount Telescope Driver"
#define MyAppPublisher   "DIYAstroMount"
#define MyAppExeName     "ASCOM.DIYAstroMount.exe"
#define MyAppProgId      "ASCOM.DIYAstroMount.Telescope"
#define BuildDir         AddBackslash(SourcePath) + "..\bin\Release\"

#ifndef MyAppVersion
  #define MyAppVersion   GetVersionNumbersString(BuildDir + MyAppExeName)
#endif

[Setup]
; Keep AppId fixed for the life of the product so new versions upgrade the existing install.
AppId={{AF34E89B-A6F5-4ACA-AE17-2CC1A6426E9C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} Setup
DefaultDirName={commoncf32}\ASCOM\Telescope\DIYAstroMount
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=DIYAstroMount_ASCOM_Setup_{#MyAppVersion}
SetupIconFile=..\ASCOM.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=6.1sp1
; The local server may be running (a client is connected); ask the user to close it.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#BuildDir}{#MyAppExeName}";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}{#MyAppExeName}.config"; DestDir: "{app}"; Flags: ignoreversion

[Run]
; Register the driver with COM and the ASCOM Chooser. Setup is already elevated.
Filename: "{app}\{#MyAppExeName}"; Parameters: "/regserver"; Flags: runhidden waituntilterminated; StatusMsg: "Registering the ASCOM driver..."

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "/unregserver"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterDriver"

[Code]
const
  RequiredPlatformMajor = 7;

// Reads the installed ASCOM Platform version ("7.1.3.4851") from the 32-bit registry view.
function GetPlatformVersion(var Version: String): Boolean;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\ASCOM\Platform', 'Platform Version', Version);
end;

function PlatformMajor(const Version: String): Integer;
var
  DotPos: Integer;
begin
  DotPos := Pos('.', Version);
  if DotPos > 0 then
    Result := StrToIntDef(Copy(Version, 1, DotPos - 1), 0)
  else
    Result := StrToIntDef(Version, 0);
end;

function InitializeSetup(): Boolean;
var
  Version: String;
begin
  Result := True;
  if (not GetPlatformVersion(Version)) or (PlatformMajor(Version) < RequiredPlatformMajor) then
  begin
    SuppressibleMsgBox(
      'The ASCOM Platform ' + IntToStr(RequiredPlatformMajor) + ' or later is required for this driver.' + #13#10 + #13#10 +
      'Please install the latest ASCOM Platform from https://ascom-standards.org/ and then run this setup again.',
      mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

// On upgrade, unregister the previously installed server before its files are replaced.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  OldExe: String;
begin
  if CurStep = ssInstall then
  begin
    OldExe := ExpandConstant('{app}\{#MyAppExeName}');
    if FileExists(OldExe) then
      Exec(OldExe, '/unregserver', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
