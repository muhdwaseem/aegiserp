# -------------------------------------------------------------
#  Aegis ERP - one-shot deploy to Azure App Service (FREE F1 tier)
#  Uses the built-in SQLite database (no separate DB, no cost).
#
#  Prerequisites (one time):
#    1. Install Azure CLI:  winget install -e --id Microsoft.AzureCLI
#    2. Sign in:            az login
#  Then run:                .\deploy-azure.ps1
# -------------------------------------------------------------
param(
    [string]$ResourceGroup = "rg-aegis-erp",
    [string[]]$Regions     = @("eastus2","centralus","westus2","westeurope","uaenorth"),
    [string]$PlanName      = "plan-aegis-erp-free",
    [string]$AppName       = "aegis-erp-$((Get-Random -Minimum 1000 -Maximum 99999))"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "`nFAILED: $msg" -ForegroundColor Red; exit 1 }

# 0. Azure CLI installed and signed in
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Fail "Azure CLI not found. Install it:  winget install -e --id Microsoft.AzureCLI"
}
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) { Step "Signing in to Azure (a browser window opens)..."; az login | Out-Null }

Write-Host "`nApp name: $AppName" -ForegroundColor Yellow

# 1. Resource group (reuse if it already exists; its location is just metadata)
Step "Ensuring resource group '$ResourceGroup'"
if ((az group exists --name $ResourceGroup) -ne "true") {
    az group create --name $ResourceGroup --location $Regions[0] | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Could not create the resource group." }
}

# 2. Free (F1) plan - try each region until one has capacity (this is the quota-limited step)
$planRegion = $null
foreach ($r in $Regions) {
    Step "Creating FREE (F1) plan in '$r'"
    az appservice plan create --name $PlanName --resource-group $ResourceGroup --location $r --sku F1 --is-linux
    if ($LASTEXITCODE -eq 0) { $planRegion = $r; break }
    Write-Host "  '$r' unavailable (quota / capacity). Trying next region..." -ForegroundColor Yellow
}
if (-not $planRegion) {
    Write-Host "`nFAILED: Could not create a plan in any region - your subscription's compute quota is 0." -ForegroundColor Red
    Write-Host "Request a free quota increase, then re-run this script:" -ForegroundColor Yellow
    Write-Host "  Azure Portal -> your Subscription -> 'Usage + quotas'" -ForegroundColor White
    Write-Host "  -> search 'App Service' (or 'Total Regional vCPUs') -> Request increase -> ask for 1." -ForegroundColor White
    exit 1
}
Write-Host "Plan created in '$planRegion'." -ForegroundColor Green

# 3. Web app on .NET 8
Step "Creating the web app on .NET 8"
az webapp create --name $AppName --resource-group $ResourceGroup --plan $PlanName --runtime "DOTNETCORE:8.0"
if ($LASTEXITCODE -ne 0) { Fail "Could not create the web app (name may be taken - just re-run)." }

# 4. Configure: Web Sockets (Blazor Server), HTTPS-only, writable SQLite path
Step "Configuring the app"
az webapp config set --name $AppName --resource-group $ResourceGroup --web-sockets-enabled true | Out-Null
az webapp update --name $AppName --resource-group $ResourceGroup --https-only true | Out-Null
az webapp config appsettings set --name $AppName --resource-group $ResourceGroup --settings Database__Provider=Sqlite "ConnectionStrings__Sqlite=Data Source=/home/aegis_erp.db" | Out-Null

# 5. Build, zip, deploy
Step "Building the app (dotnet publish)..."
$pub = Join-Path $PSScriptRoot "publish"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish src/AegisErp.Web -c Release -o $pub
if ($LASTEXITCODE -ne 0) { Fail "Build failed." }

Step "Packaging and uploading to Azure..."
$zip = Join-Path $PSScriptRoot "app.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -Force
az webapp deploy --resource-group $ResourceGroup --name $AppName --src-path $zip --type zip
if ($LASTEXITCODE -ne 0) { Fail "Upload/deploy failed." }

# 6. Success
@{ AppName = $AppName; ResourceGroup = $ResourceGroup } | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot ".azure-target.json") -Encoding utf8
$url = "https://$AppName.azurewebsites.net"
Write-Host "`n=================================================" -ForegroundColor Green
Write-Host " DEPLOYED. Share this link with your client:" -ForegroundColor Green
Write-Host "   $url" -ForegroundColor White
Write-Host "   Login:  owner@aegiserp.com  /  Aegis#Owner2026" -ForegroundColor White
Write-Host "=================================================" -ForegroundColor Green
Write-Host "First load after idle can take ~20-30s (free tier waking up)." -ForegroundColor DarkGray
