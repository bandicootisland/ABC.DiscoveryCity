param(
    [string]$SourceDir = "H:\BookCity\Books\libgenli_db",
    [string]$DestBaseDir = "H:\Developer.BookCity\aa-data-import--temp-dir",
    [string]$ContainerName = "aa-data-import--web"
)

$DestDir = Join-Path $DestBaseDir "libgenli_db"

Write-Host "Checking source directory: $SourceDir" -ForegroundColor Cyan
if (-not (Test-Path $SourceDir)) {
    Write-Error "Source directory not found: $SourceDir"
    exit 1
}

Write-Host "Checking destination directory: $DestDir" -ForegroundColor Cyan
if (-not (Test-Path $DestDir)) {
    Write-Host "Creating destination directory..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
}

Write-Host "Moving RAR files..." -ForegroundColor Cyan
# Move all files from source to dest
Get-ChildItem -Path $SourceDir -Filter "*.rar" | ForEach-Object {
    $destFile = Join-Path $DestDir $_.Name
    if (-not (Test-Path $destFile)) {
        Write-Host "Moving $($_.Name)..."
        Move-Item -Path $_.FullName -Destination $DestDir
    } else {
        Write-Host "Skipping $($_.Name) (already exists)" -ForegroundColor DarkGray
    }
}

# Also move zip if exists (script mentions libgen_new.zip)
Get-ChildItem -Path $SourceDir -Filter "*.zip" | ForEach-Object {
    $destFile = Join-Path $DestDir $_.Name
    if (-not (Test-Path $destFile)) {
        Write-Host "Moving $($_.Name)..."
        Move-Item -Path $_.FullName -Destination $DestDir
    }
}

Write-Host "Files moved. Ready to run import script in Docker." -ForegroundColor Green

# Check if container is running
$containerStatus = docker inspect -f '{{.State.Running}}' $ContainerName 2>$null

if ($containerStatus -eq "true") {
    Write-Host "Container $ContainerName is running. Executing import script..." -ForegroundColor Cyan
    # Run the script inside docker
    docker exec -it $ContainerName /scripts/load_libgenli.sh
} else {
    Write-Warning "Container $ContainerName is NOT running."
    Write-Host "You need to start the DataImports stack."
    Write-Host "1. Stop current stack (if running): docker stop bookcity-kibana bookcity-elasticsearch bookcity-mariadb"
    Write-Host "2. Start DataImports stack:"
    Write-Host "   cd H:\Developer.BookCity\ABC.BookCity\DataImports"
    Write-Host "   docker-compose up -d aa-data-import--web aa-data-import--mariadb"
    Write-Host "3. Run this script again."
}
