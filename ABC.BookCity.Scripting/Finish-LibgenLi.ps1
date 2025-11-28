param(
    [string]$ContainerName = "aa-data-import--web"
)

Write-Host "Checking if container $ContainerName is running..." -ForegroundColor Cyan
$containerStatus = docker inspect -f '{{.State.Running}}' $ContainerName 2>$null

if ($containerStatus -eq "true") {
    Write-Host "Container $ContainerName is running. Executing finish_libgenli_import.sh..." -ForegroundColor Cyan
    
    # Ensure the script is executable (just in case, though usually not needed if mounted rw)
    docker exec $ContainerName chmod +x /scripts/finish_libgenli_import.sh
    
    # Run the script inside docker
    docker exec -it $ContainerName /scripts/finish_libgenli_import.sh
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Import finished successfully!" -ForegroundColor Green
    } else {
        Write-Host "Import script failed with exit code $LASTEXITCODE" -ForegroundColor Red
    }
} else {
    Write-Error "Container $ContainerName is not running. Please start the docker stack first."
}
