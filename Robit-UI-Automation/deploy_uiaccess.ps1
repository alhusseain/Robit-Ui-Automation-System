# Requires -RunAsAdministrator

$workspaceRoot = "d:\Projects\Robit Ui Automation System\Robit-UI-Automation"
$logFile = "$workspaceRoot\deploy_log.txt"

Start-Transcript -Path $logFile -Force

try {
    $targetDir = "C:\Program Files\RobitUiAutomation"
    $exePath = "$workspaceRoot\bin\Release\net48\Robit-UI-Automation.exe"

    # 1. Build the application in Release mode
    Write-Host "Building project in Release mode..." -ForegroundColor Cyan
    dotnet build -c Release "$workspaceRoot\Robit-UI-Automation.csproj"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed!"
        exit 1
    }

    # 2. Check/Create self-signed code-signing certificate
    $certSubject = "CN=RobitUiAutomationUIAccess"
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $certSubject }

    if (-not $cert) {
        Write-Host "Creating local self-signed code signing certificate..." -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject -CertStoreLocation Cert:\LocalMachine\My
        
        # Export and import to Root and Trusted Publisher stores
        $cerFile = "$workspaceRoot\temp_uiaccess.cer"
        Export-Certificate -Cert $cert -FilePath $cerFile | Out-Null
        
        Write-Host "Installing certificate to Trusted Root Certification Authorities..." -ForegroundColor Cyan
        Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        
        Write-Host "Installing certificate to Trusted Publishers..." -ForegroundColor Cyan
        Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
        
        Remove-Item $cerFile
        Write-Host "Certificate generated and trusted successfully." -ForegroundColor Green
    } else {
        Write-Host "Using existing code-signing certificate: $certSubject" -ForegroundColor Green
    }

    # 3. Sign the executable
    Write-Host "Signing executable: $exePath" -ForegroundColor Cyan
    $signResult = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert
    if ($signResult.Status -ne "Valid") {
        Write-Error "Signing failed! Status: $($signResult.Status)"
        exit 1
    }
    Write-Host "Executable signed successfully." -ForegroundColor Green

    # 4. Copy to secure folder
    Write-Host "Deploying to secure system directory: $targetDir" -ForegroundColor Cyan
    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir | Out-Null
    }

    Copy-Item -Path "$workspaceRoot\bin\Release\net48\*" -Destination $targetDir -Recurse -Force
    Write-Host "Deployment completed successfully!" -ForegroundColor Green
}
catch {
    Write-Error "An unexpected error occurred during deployment: $_"
}
finally {
    Stop-Transcript
}
