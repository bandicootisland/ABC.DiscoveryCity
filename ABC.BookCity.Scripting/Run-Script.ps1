param(
    [Parameter(Mandatory=$true)]
    [string]$ScriptFile
)

$ScriptPath = Resolve-Path $ScriptFile
$ScriptName = [System.IO.Path]::GetFileNameWithoutExtension($ScriptPath)
$TempDir = Join-Path $PSScriptRoot ".run_$ScriptName"

Write-Host "Preparing to run $ScriptName..." -ForegroundColor Cyan

if (Test-Path $TempDir) {
    Remove-Item $TempDir -Recurse -Force
}

New-Item -ItemType Directory -Path $TempDir | Out-Null

# Create a new console app
dotnet new console -o $TempDir --no-restore | Out-Null

# Replace Program.cs with our script
Copy-Item $ScriptPath -Destination (Join-Path $TempDir "Program.cs") -Force

# Run it
Push-Location $TempDir
try {
    dotnet run
}
finally {
    Pop-Location
    # Optional: Cleanup
    # Remove-Item $TempDir -Recurse -Force
}
