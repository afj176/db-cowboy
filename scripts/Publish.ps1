param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,[switch]$Installer,[string]$InnoCompiler='')
# Writes a self-contained release payload and zip to artifacts/DbCowboy-<version>-win-x64.
# An existing release folder is never overwritten; pass the next version number.
$ErrorActionPreference='Stop'
$appRoot=Split-Path $PSScriptRoot -Parent
$payload=Join-Path $appRoot "artifacts/DbCowboy-$Version-win-x64"
if(Test-Path -LiteralPath $payload){throw "Release already exists: $payload"}
# DebugType=None keeps DbCowboy.pdb out of the payload.
dotnet publish (Join-Path $appRoot 'src/DbCowboy/DbCowboy.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:DebugType=None -p:Version=$Version -o $payload -m:1
if($LASTEXITCODE -ne 0){throw 'App publish failed'}
& (Join-Path $PSScriptRoot 'Assert-ReleasePayload.ps1') -Payload $payload
Copy-Item -LiteralPath (Join-Path $appRoot 'README.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $appRoot 'LICENSE') -Destination $payload
Copy-Item -LiteralPath (Join-Path $appRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload
if(-not(Test-Path -LiteralPath (Join-Path $payload 'DbCowboy.exe')) -or -not(Test-Path -LiteralPath (Join-Path $payload 'DbCowboy.pri'))){throw 'Executable or WinUI resources missing'}
Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object{[pscustomobject]@{Path=$_.FullName.Substring($payload.Length+1);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Bytes=$_.Length}} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $payload 'payload-manifest.json') -Encoding utf8
Compress-Archive -LiteralPath $payload -DestinationPath "$payload.zip" -CompressionLevel Optimal
if($Installer){
    if(-not(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)){throw 'Provide an existing Inno Setup compiler with -InnoCompiler.'}
    & $InnoCompiler "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DOutputPath=$appRoot/artifacts" (Join-Path $appRoot 'packaging/DbCowboy.iss')
    if($LASTEXITCODE -ne 0){throw 'Installer compile failed'}
}
Write-Output "Published DB Cowboy: $payload"
