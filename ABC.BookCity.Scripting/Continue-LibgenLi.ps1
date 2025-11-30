param(
    [string]$ContainerName = "aa-data-import--web"
)

Write-Host "Checking if container $ContainerName is running..." -ForegroundColor Cyan
$containerStatus = docker inspect -f '{{.State.Running}}' $ContainerName 2>$null

if ($containerStatus -eq "true") {
    # Kill mysqlcheck if running
    Write-Host "Checking for stuck mysqlcheck process..." -ForegroundColor Yellow
    $pids = docker exec $ContainerName pgrep mysqlcheck
    if ($pids) {
        Write-Host "Killing mysqlcheck process ($pids) to proceed..." -ForegroundColor Red
        docker exec $ContainerName kill $pids
    }

    Write-Host "Executing continue_libgenli_import.sh..." -ForegroundColor Cyan
    
    # Ensure the script is executable
    docker exec $ContainerName chmod +x /scripts/continue_libgenli_import.sh
    
    # Run the script inside docker
    docker exec -it $ContainerName /scripts/continue_libgenli_import.sh
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Import continuation finished successfully!" -ForegroundColor Green
        Write-Host "You can now run Finish-LibgenLi.ps1 to rename and move the data." -ForegroundColor Yellow
    } else {
        Write-Host "Import continuation script failed with exit code $LASTEXITCODE" -ForegroundColor Red
    }
} else {
    Write-Error "Container $ContainerName is not running. Please start the docker stack first."
}
