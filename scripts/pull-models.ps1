<#
.SYNOPSIS
    Downloads models into the AgentChat Ollama container (see docker-compose.yml).

.EXAMPLE
    ./scripts/pull-models.ps1
    ./scripts/pull-models.ps1 -Models qwen2.5:7b,llama3.1:8b
#>
param(
    # The default agents' models (Ada, Quinn, Gem): small enough to run on CPU.
    [string[]] $Models = @("llama3.2:3b", "qwen2.5:3b", "gemma3:1b"),
    [string] $Container = "agentchat-ollama"
)

# Not "Stop": Windows PowerShell 5.1 turns the progress Ollama writes to stderr into errors.
# Failures are detected from exit codes instead.

$running = docker ps --filter "name=^$Container$" --format "{{.Names}}"
if (-not $running) {
    Write-Host "Container '$Container' is not running. Start it with: docker compose up -d" -ForegroundColor Red
    exit 1
}

foreach ($model in $Models) {
    Write-Host "Pulling $model ..." -ForegroundColor Cyan
    docker exec $Container ollama pull $model
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Failed to pull $model" -ForegroundColor Red
        exit 1
    }
}

Write-Host "`nInstalled models:" -ForegroundColor Cyan
docker exec $Container ollama list
