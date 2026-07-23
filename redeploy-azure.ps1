# -------------------------------------------------------------
#  Aegis ERP - RE-deploy code changes to the SAME Azure app (same URL)
#  Rebuilds and pushes new bits; does NOT create new resources.
#
#  Usage:   .\redeploy-azure.ps1
#           .\redeploy-azure.ps1 -AppName aegis-erp-12345   (force a specific app)
# -------------------------------------------------------------
param(
    [string]$AppName,
    [string]$ResourceGroup = "rg-aegis-erp"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
function Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "`nFAILED: $msg" -ForegroundColor Red; exit 1 }

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Fail "Azure CLI not found. Install:  winget install -e --id Microsoft.AzureCLI"
}
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) { Step "Signing in to Azure..."; az login | Out-Null }

# Resolve which app to redeploy to: -AppName  >  saved file  >  auto-detect
$targetFile = Join-Path $PSScriptRoot ".azure-target.json"
if (-not $AppName -and (Test-Path $targetFile)) {
    $t = Get-Content $targetFile -Raw | ConvertFrom-Json
    $AppName = $t.AppName
    if ($t.ResourceGroup) { $ResourceGroup = $t.ResourceGroup }
}
if (-not $AppName) {
    Step "Finding your web app in '$ResourceGroup'..."
    $list = @(az webapp list -g $ResourceGroup --query "[].name" -o tsv 2>$null) | Where-Object { $_ }
    if ($list.Count -eq 1) { $AppName = $list[0] }
    elseif ($list.Count -eq 0) { Fail "No web app found in '$ResourceGroup'. Run .\deploy-azure.ps1 first." }
    else { Write-Host "Several apps found - re-run with -AppName <name>:" -ForegroundColor Yellow; $list | ForEach-Object { Write-Host "   $_" }; exit 1 }
}
Write-Host "Redeploying to: $AppName" -ForegroundColor Yellow

Step "Building the app (dotnet publish)..."
$pub = Join-Path $PSScriptRoot "publish"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish src/AegisErp.Web -c Release -o $pub
if ($LASTEXITCODE -ne 0) { Fail "Build failed." }

Step "Packaging and uploading the new build..."
$zip = Join-Path $PSScriptRoot "app.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -Force
az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $zip --type zip
if ($LASTEXITCODE -ne 0) { Fail "Upload/deploy failed." }

@{ AppName = $AppName; ResourceGroup = $ResourceGroup } | ConvertTo-Json | Set-Content $targetFile -Encoding utf8
$url = "https://$AppName.azurewebsites.net"
Write-Host "`n=================================================" -ForegroundColor Green
Write-Host " REDEPLOYED:  $url" -ForegroundColor White
Write-Host "=================================================" -ForegroundColor Green
Write-Host "First load after idle can take ~20-30s." -ForegroundColor DarkGray
