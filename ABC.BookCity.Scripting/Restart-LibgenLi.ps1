param(
    [string]$ContainerName = "aa-data-import--web"
)

Write-Host "Checking if container $ContainerName is running..." -ForegroundColor Cyan
$containerStatus = docker inspect -f '{{.State.Running}}' $ContainerName 2>$null

if ($containerStatus -eq "true") {
    Write-Host "Container $ContainerName is running. Executing restart_libgenli_import.sh..." -ForegroundColor Cyan
    
    # Ensure the script is executable
    docker exec $ContainerName chmod +x /scripts/restart_libgenli_import.sh
    
    # Run the script inside docker
    docker exec -it $ContainerName /scripts/restart_libgenli_import.sh
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Import restart process finished successfully! Data is now in libgen_new." -ForegroundColor Green
        Write-Host "You can now run Finish-LibgenLi.ps1 to rename and move the data." -ForegroundColor Yellow
    } else {
        Write-Host "Import restart script failed with exit code $LASTEXITCODE" -ForegroundColor Red
    }
} else {
    Write-Error "Container $ContainerName is not running. Please start the docker stack first."
}
