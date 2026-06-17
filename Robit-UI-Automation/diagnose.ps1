param (
    [string]$Name,
    [string]$Id,
    [string]$ProcessId,
    [switch]$List
)

# Set runtime variables
$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($PSScriptRoot)) {
    $PSScriptRoot = Get-Location
}

# 1. Build the project first to make sure everything is up to date
Write-Host "Building Robit-UI-Automation project..." -ForegroundColor Cyan
dotnet build "$PSScriptRoot\Robit-UI-Automation.csproj" -c Debug --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit $LASTEXITCODE
}

$exePath = "$PSScriptRoot\bin\Debug\net48\Robit-UI-Automation.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Could not find built executable at: $exePath"
    exit 1
}

$argsList = @("--diagnose")
if ($Name) { $argsList += @("--name", $Name) }
if ($Id) { $argsList += @("--id", $Id) }
if ($ProcessId) { $argsList += @("--pid", $ProcessId) }
if ($List) { $argsList += @("--list") }

# Run the exe
& $exePath $argsList
