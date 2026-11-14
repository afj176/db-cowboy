$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path $PSScriptRoot -Parent
$guard=Join-Path $repositoryRoot 'scripts/Assert-ReleasePayload.ps1'
$fixtureRoot=Join-Path $repositoryRoot ('artifacts/release-payload-checks/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$checks=0
function Test-RejectedPayload([string]$RelativePath,[bool]$Directory=$false){
    $caseRoot=Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
    $path=Join-Path $caseRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    if($Directory){New-Item -ItemType Directory -Path $path | Out-Null}
    else{Set-Content -LiteralPath $path -Value '{}'}
    $rejected=$false
    try{& $guard -Payload $caseRoot | Out-Null}
    catch{if($_.Exception.Message -notlike 'Release payload contains profile data*'){throw};$rejected=$true}
    if(-not $rejected){throw "Contaminated payload accepted: $RelativePath"}
    Write-Output "PASS rejects $RelativePath"
    $script:checks++
}
try{
    $clean=Join-Path $fixtureRoot 'clean'
    New-Item -ItemType Directory -Path $clean | Out-Null
    Set-Content -LiteralPath (Join-Path $clean 'DbCowboy.runtimeconfig.json') -Value '{}'
    Set-Content -LiteralPath (Join-Path $clean 'portable.flag') -Value ''
    Set-Content -LiteralPath (Join-Path $clean 'DbCowboy.deps.json') -Value '{}'
    New-Item -ItemType Directory -Path (Join-Path $clean 'Assets/data') | Out-Null
    & $guard -Payload $clean
    $checks++
    Test-RejectedPayload 'connections.json'
    Test-RejectedPayload 'nested/CONNECTIONS.JSON'
    Test-RejectedPayload 'connections.json.tmp'
    Test-RejectedPayload 'workspace.json'
    Test-RejectedPayload 'nested/workspace.json.tmp'
    Test-RejectedPayload 'history.json'
    Test-RejectedPayload 'connections.json.unreadable-20260101-120000.bak'
    Test-RejectedPayload 'crash.txt'
    Test-RejectedPayload 'data' $true
    Test-RejectedPayload 'ui-qa-data' $true
    Test-RejectedPayload 'WebView2Loader.dll'
    Test-RejectedPayload 'Microsoft.Web.WebView2.Core.dll'
    Test-RejectedPayload 'onnxruntime.dll'
    Test-RejectedPayload 'DirectML.dll'
    Test-RejectedPayload 'Microsoft.ML.OnnxRuntime.dll'
    Test-RejectedPayload 'Microsoft.Windows.AI.Text.dll'
    Test-RejectedPayload 'workloads.json'
    Write-Output "$checks release payload checks passed"
}
finally{
    # The fixtures are throwaway; leave nothing behind, and drop the shared folder once it is empty.
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    $parent=Split-Path $fixtureRoot -Parent
    if((Test-Path -LiteralPath $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force)){Remove-Item -LiteralPath $parent -Force}
}
