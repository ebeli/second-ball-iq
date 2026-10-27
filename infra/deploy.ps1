<#
.SYNOPSIS
  Deploys Second Ball IQ to Azure Container Apps: builds the image in Azure Container Registry (no local Docker needed)
  and runs it as one always-on container with the Foundry settings as environment variables.

.EXAMPLE
  # In the repo root, with the same MATCHLENS_* variables set as for local runs:
  az login
  ./infra/deploy.ps1 -Location swedencentral
  # Run it again after code changes: it rebuilds the image and updates the app in place.

.EXAMPLE
  # Offline demo: no Foundry keys needed, no model costs; moments are written by the fact-built templates.
  az login
  ./infra/deploy.ps1 -Offline
#>
param(
    [string]$ResourceGroup = "rg-second-ball-iq",
    [string]$Location = "swedencentral",
    [string]$Name = "second-ball-iq",
    [string]$ChatDeployment = $env:MATCHLENS_CHAT_DEPLOYMENT,
    # Deploy without any model settings: the app runs on its offline templates (no keys, no model costs).
    [switch]$Offline
)

$ErrorActionPreference = "Stop"

if (-not $Offline) {
    foreach ($v in "MATCHLENS_DECISION_ENDPOINT", "MATCHLENS_DECISION_KEY", "MATCHLENS_DECISION_DEPLOYMENT") {
        if (-not [Environment]::GetEnvironmentVariable($v)) { throw "Set $v first (the same values as for local runs), or pass -Offline." }
    }
    if (-not $ChatDeployment) { throw "Set MATCHLENS_CHAT_DEPLOYMENT or pass -ChatDeployment, or pass -Offline." }
}
if (-not (Test-Path "Dockerfile")) { throw "Run this from the repo root." }

Write-Host "Preparing the Azure CLI..."
# The Container Apps commands are a CLI extension. Updating it can fail behind a company proxy; an installed
# version is good enough, so only stop when it is missing altogether.
az extension add --name containerapp --upgrade --only-show-errors 2>$null | Out-Null
if (-not (az extension show --name containerapp --query version -o tsv 2>$null)) {
    throw "The Azure CLI 'containerapp' extension could not be installed (pip failed). Try: az upgrade; then az extension add --name containerapp. Behind a company proxy, run it from another network or see docs/server.md."
}
az provider register --namespace Microsoft.App --wait | Out-Null
az provider register --namespace Microsoft.OperationalInsights --wait | Out-Null
az provider register --namespace Microsoft.ContainerRegistry --wait | Out-Null

# Registry names must be globally unique: derive one from the subscription so reruns reuse it.
$subscription = az account show --query id -o tsv
$registry = ($Name -replace '[^a-z0-9]', '') + $subscription.Substring(0, 8).ToLower() -replace '[^a-z0-9]', ''
$tag = Get-Date -Format "yyyyMMdd-HHmmss"
$image = "$registry.azurecr.io/$($Name):$tag"

Write-Host "Resource group $ResourceGroup in $Location"
az group create --name $ResourceGroup --location $Location --only-show-errors | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not create the resource group. Is 'az login' done and the right subscription selected?" }

if (-not (az acr show --name $registry --resource-group $ResourceGroup --query name -o tsv 2>$null)) {
    Write-Host "Creating registry $registry"
    az acr create --name $registry --resource-group $ResourceGroup --sku Basic --admin-enabled true --only-show-errors | Out-Null
}

Write-Host "Building $image in Azure (takes a few minutes)..."
az acr build --registry $registry --image "$($Name):$tag" . --only-show-errors
if ($LASTEXITCODE -ne 0) { throw "The image build failed; see the log above." }

$registryUser = az acr credential show --name $registry --query username -o tsv
$registryPassword = az acr credential show --name $registry --query "passwords[0].value" -o tsv

$environment = "$Name-env"
if (-not (az containerapp env show --name $environment --resource-group $ResourceGroup --query name -o tsv 2>$null)) {
    Write-Host "Creating Container Apps environment $environment"
    az containerapp env create --name $environment --resource-group $ResourceGroup --location $Location --only-show-errors | Out-Null
}

[string[]]$envVars = if ($Offline) { @("MATCHLENS_MODE=offline") } else { @(
    "MATCHLENS_DECISION_ENDPOINT=$env:MATCHLENS_DECISION_ENDPOINT",
    "MATCHLENS_DECISION_KEY=secretref:decision-key",
    "MATCHLENS_DECISION_DEPLOYMENT=$env:MATCHLENS_DECISION_DEPLOYMENT",
    "MATCHLENS_CHAT_DEPLOYMENT=$ChatDeployment"
) }
# Spending caps for a public site (see docs/server.md): any MATCHLENS_MAX_* or per-visitor limit set here is passed on;
# unset, the server's defaults apply.
foreach ($v in "MATCHLENS_MAX_CHAT_PER_HOUR", "MATCHLENS_MAX_CHAT_PER_DAY", "MATCHLENS_MAX_DECISION_PER_HOUR", "MATCHLENS_MAX_DECISION_PER_DAY",
               "MATCHLENS_MAX_SPEECH_PER_HOUR", "MATCHLENS_MAX_SPEECH_PER_DAY", "MATCHLENS_MODEL_CALLS_PER_IP_MINUTE", "MATCHLENS_VOICE_CALLS_PER_IP_MINUTE") {
    $value = [Environment]::GetEnvironmentVariable($v)
    if ($value) { $envVars += "$v=$value" }
}
# The operator key: with it set, only someone who opens the studio as /?key=... can start, stop or steer a match.
[string[]]$secrets = if ($Offline) { @() } else { @("decision-key=$env:MATCHLENS_DECISION_KEY") }
if ($env:MATCHLENS_ADMIN_KEY) {
    $secrets += "admin-key=$env:MATCHLENS_ADMIN_KEY"
    $envVars += "MATCHLENS_ADMIN_KEY=secretref:admin-key"
}

# The offline demo has no secrets; splatting an empty list passes nothing.
[string[]]$secretArgs = if ($secrets.Count) { @("--secrets") + $secrets } else { @() }

if (-not (az containerapp show --name $Name --resource-group $ResourceGroup --query name -o tsv 2>$null)) {
    Write-Host "Creating container app $Name"
    # At most one replica: the match, the editor queue and the SignalR connections live in memory.
    # Zero minimum replicas: the app sleeps when nobody uses it (no compute cost) and wakes in a few seconds.
    # Run "az containerapp update -n <Name> -g <ResourceGroup> --min-replicas 1" before a live demo to keep it warm.
    az containerapp create --name $Name --resource-group $ResourceGroup --environment $environment `
        --image $image --registry-server "$registry.azurecr.io" `
        --registry-username $registryUser --registry-password $registryPassword `
        --target-port 8080 --ingress external --min-replicas 0 --max-replicas 1 --cpu 0.5 --memory 1Gi `
        @secretArgs `
        --env-vars @envVars --only-show-errors | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Creating the container app failed." }
}
else {
    Write-Host "Updating container app $Name"
    if ($secrets.Count) {
        az containerapp secret set --name $Name --resource-group $ResourceGroup `
            --secrets @secrets --only-show-errors | Out-Null
    }
    az containerapp update --name $Name --resource-group $ResourceGroup --image $image `
        --set-env-vars @envVars --only-show-errors | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Updating the container app failed." }
}

$fqdn = az containerapp show --name $Name --resource-group $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv
Write-Host ""
Write-Host "Second Ball IQ is live at https://$fqdn   (judges: https://$fqdn/judge)"
Write-Host "Health: https://$fqdn/api/health   MCP: https://$fqdn/mcp"
