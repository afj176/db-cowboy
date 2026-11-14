param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference='Stop'
$payloadRoot=(Get-Item -LiteralPath $Payload).FullName
if(-not (Test-Path -LiteralPath $payloadRoot -PathType Container)){throw 'Release payload must be a directory.'}

# Both the portable ZIP and installer must be built from application files only.
# Never let one person's connections, queries or history become another user's defaults.
$profileFiles=@('connections.json','workspace.json','history.json')
$forbidden=@(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force | Where-Object {
    # No WebView in this app: its libraries must never ship.
    $_.Name -like '*WebView2*' -or
    # The profile files the app writes (Store.cs, WorkspaceState.cs), their temporary and set-aside copies, and the crash note.
    (-not $_.PSIsContainer -and ($_.Name -in $profileFiles -or $_.Name -in @($profileFiles | ForEach-Object {"$_.tmp"}) -or $_.Name -like '*.json.unreadable-*.bak' -or $_.Name -eq 'crash.txt')) -or
    # The profile folders of a portable copy and of an --integration-ui run.
    ($_.PSIsContainer -and $_.Parent.FullName -eq $payloadRoot -and $_.Name -in @('data','ui-qa-data')) -or
    # Windows AI and ML components of the full Windows App SDK package; the app references only the components it uses.
    (-not $_.PSIsContainer -and ($_.Name -like 'onnxruntime*' -or $_.Name -eq 'DirectML.dll' -or $_.Name -like 'Microsoft.ML.*' -or $_.Name -like 'Microsoft.Windows.AI.*' -or $_.Name -like 'workloads*.json'))
})
if($forbidden.Count){
    $paths=($forbidden | ForEach-Object {$_.FullName.Substring($payloadRoot.TrimEnd([IO.Path]::DirectorySeparatorChar).Length+1)}) -join ', '
    throw "Release payload contains profile data or components that must not ship: $paths"
}
Write-Output 'PASS release payload contains no profile data and no excluded components'
