#ifndef ApplicationId
#define ApplicationId "DbCowboy.Postgres"
#endif
[Setup]
AppId={#ApplicationId}
AppName=DB Cowboy
AppMutex=Local\DbCowboy.Postgres.App
AppVersion={#AppVersion}
AppPublisher=DB Cowboy
DefaultDirName={localappdata}\Programs\DbCowboy
DefaultGroupName=DB Cowboy
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableFinishedPage=no
WizardStyle=modern
SetupIconFile=..\src\DbCowboy\Assets\DbCowboy.ico
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizard-small.bmp
OutputDir={#OutputPath}
OutputBaseFilename=DbCowboy-{#AppVersion}-Setup-x64
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\DbCowboy.exe
[Messages]
WelcomeLabel1=Meet your new Postgres sidekick.
WelcomeLabel2=DB Cowboy brings SQL, tables, relationships and extensions into one native Windows workspace for PostgreSQL.%n%nMeet Rowdy. Let's get you set up.
FinishedHeadingLabel=You're ready to explore.
FinishedLabel=DB Cowboy is installed. Open DB Cowboy from the Windows Start menu to begin.
[Files]
; Connections, queries and history belong to the user's profile, never to an installer payload.
; Unanchored filename exclusions also protect nested folders (Inno Excludes). The profile entries mirror
; scripts/Assert-ReleasePayload.ps1, which already rejects a payload that contains any of these.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.flag,data\*,ui-qa-data\*,connections.json,connections.json.tmp,workspace.json,workspace.json.tmp,history.json,history.json.tmp,*.json.unreadable-*.bak,crash.txt"
[InstallDelete]
; Files earlier releases installed that this one no longer ships: the WebView2 libraries, Windows AI /
; ONNX Runtime / DirectML files the full Windows App SDK package brought in, and retired assets. Setup
; only adds and replaces files, so without these entries an upgrade would leave them in place. Only
; paths inside {app}; the user's profile lives elsewhere and is never touched.
Type: filesandordirs; Name: "{app}\NpuDetect"
Type: filesandordirs; Name: "{app}\docs"
Type: filesandordirs; Name: "{app}\native-handoff"
Type: files; Name: "{app}\Assets\huggingface.svg"
Type: files; Name: "{app}\DbCowboy.Code.Core.dll"
Type: files; Name: "{app}\DbCowboy.Code.Core.pdb"
Type: files; Name: "{app}\DbCowboy.NetworkHook.dll"
Type: files; Name: "{app}\DbCowboy.NetworkHook.pdb"
Type: files; Name: "{app}\DbCowboy.pdb"
Type: files; Name: "{app}\dbcw.dll"
Type: files; Name: "{app}\design-qa.md"
Type: files; Name: "{app}\DirectML.dll"
Type: files; Name: "{app}\Microsoft.Graphics.Imaging.dll"
Type: files; Name: "{app}\Microsoft.Graphics.Imaging.Projection.dll"
Type: files; Name: "{app}\Microsoft.Graphics.Imaging.winmd"
Type: files; Name: "{app}\Microsoft.Graphics.ImagingInternal.ImageObjectRemover.winmd"
Type: files; Name: "{app}\Microsoft.Graphics.ImagingInternal.winmd"
Type: files; Name: "{app}\Microsoft.Graphics.Internal.Imaging.winmd"
Type: files; Name: "{app}\Microsoft.ML.OnnxRuntime.dll"
Type: files; Name: "{app}\Microsoft.Web.WebView2.Core.dll"
Type: files; Name: "{app}\Microsoft.Web.WebView2.Core.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.AICapabilities.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentModerationInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentSafety.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentSafety.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.ContentSafety.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.Foundation.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Foundation.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.FoundationInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.GenerativeInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.Imaging.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Imaging.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Imaging.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.MachineLearning.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.MachineLearning.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.MachineLearning.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Text.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Text.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.AI.Text.winmd"
Type: files; Name: "{app}\Microsoft.Windows.AI.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Internal.Vision.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Private.Workloads.SessionManager.winmd"
Type: files; Name: "{app}\Microsoft.Windows.PrivateCommon.winmd"
Type: files; Name: "{app}\Microsoft.Windows.SemanticSearch.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Vision.winmd"
Type: files; Name: "{app}\Microsoft.Windows.VisionInternal.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Widgets.dll"
Type: files; Name: "{app}\Microsoft.Windows.Widgets.Projection.dll"
Type: files; Name: "{app}\Microsoft.Windows.Widgets.winmd"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.dll"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.Resources.dll"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.Resources_ec.dll"
Type: files; Name: "{app}\Microsoft.Windows.Workloads.winmd"
Type: files; Name: "{app}\Microsoft.WindowsAppRuntime.Insights.Resource.dll"
Type: files; Name: "{app}\onnxruntime.dll"
Type: files; Name: "{app}\onnxruntime_providers_shared.dll"
Type: files; Name: "{app}\PLAN.md"
Type: files; Name: "{app}\SessionHandleIPCProxyStub.dll"
Type: files; Name: "{app}\System.Numerics.Tensors.dll"
Type: files; Name: "{app}\WebView2Loader.dll"
Type: files; Name: "{app}\WindowsAppRuntime.DeploymentExtensions.OneCore.dll"
Type: files; Name: "{app}\WindowsAppRuntime.png"
Type: files; Name: "{app}\WindowsAppSdk.AppxDeploymentExtensions.Desktop-EventLog-Instrumentation.dll"
Type: files; Name: "{app}\WindowsAppSdk.AppxDeploymentExtensions.Desktop.dll"
Type: files; Name: "{app}\workloads.365.json"
Type: files; Name: "{app}\workloads.j32.json"
Type: files; Name: "{app}\workloads.json"
Type: files; Name: "{app}\workloads.lnl.json"
Type: files; Name: "{app}\workloads.qnn.json"
Type: files; Name: "{app}\workloads.stx.json"
; Folders that releases up to 0.35 installed and this version no longer ships. Only paths inside {app};
; the user's profile (connections, workspace, history) lives elsewhere and is never touched.
Type: filesandordirs; Name: "{app}\agent"
Type: filesandordirs; Name: "{app}\model-runtime"
; Kept on purpose:
; - {app}\lab-worker: [UninstallRun] below runs it once more to remove the scheduled monitors 0.35 and
;   earlier could create. Deleting it on upgrade would leave those schedules behind for good.
; - {app}\sandbox-setup: its "remove-powershell-logging" command is the only way to undo the machine-wide
;   PowerShell logging change an earlier version could make (it needs administrator rights, which this
;   per-user Setup does not have). The removal shortcut stays only where that tool is still present.
; The shortcut that applied that change is no longer offered.
Type: files; Name: "{group}\Optional PowerShell Auto setup.lnk"
Type: files; Name: "{app}\Assets\01-mesa.png"
Type: files; Name: "{app}\Assets\02-flint.png"
Type: files; Name: "{app}\Assets\03-juniper.png"
Type: files; Name: "{app}\Assets\04-pip.png"
Type: files; Name: "{app}\Assets\05-bramble.png"
[Icons]
Name: "{group}\DB Cowboy"; Filename: "{app}\DbCowboy.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Rowdy.ico"; AppUserModelID: "DbCowboy.Postgres"
Name: "{group}\Uninstall DB Cowboy"; Filename: "{uninstallexe}"
Name: "{group}\Remove PowerShell Auto logging setup"; Filename: "{app}\sandbox-setup\DbCowboy.SandboxSetup.exe"; Parameters: "remove-powershell-logging"; WorkingDir: "{app}\sandbox-setup"; Check: SandboxSetupPresent
[UninstallRun]
; The monitor worker is no longer shipped. An install upgraded from 0.35 or earlier still has it, and
; uninstall uses it once more to remove any scheduled monitors that older version created.
Filename: "{app}\lab-worker\DbCowboy.LabWorker.exe"; Parameters: "--remove-owned-schedules"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "DbCowboyLocalMonitors"
; There is no post-install "Launch DB Cowboy" step: Inno Setup 6.7.3 post-install launches inherit
; Setup's RedirectionGuard. Launch the app from its Start menu shortcut instead.

[Code]
{ True only on an upgrade from a version that installed the PowerShell logging tool. }
function SandboxSetupPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\sandbox-setup\DbCowboy.SandboxSetup.exe'));
end;

function InitializeSetup(): Boolean;
begin
  { Earlier builds do not create AppMutex. Require them to close as well. }
  Result := FindWindowByWindowName('DB Cowboy') = 0;
  if not Result then
  begin
    Log('Upgrade blocked: a DB Cowboy window is still running.');
    if not WizardSilent then
      MsgBox('Close all DB Cowboy windows before installing this update. Your saved work and settings will be kept.', mbError, MB_OK);
  end;
end;
