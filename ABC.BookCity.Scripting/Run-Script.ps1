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

# Add JsonSerializerIsReflectionEnabledByDefault to csproj to fix .NET 10 reflection issue
$csprojPath = Get-ChildItem -Path $TempDir -Filter "*.csproj" | Select-Object -First 1
$csprojContent = Get-Content $csprojPath.FullName -Raw
$csprojContent = $csprojContent -replace '<Nullable>enable</Nullable>', '<Nullable>enable</Nullable>
    <JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>'
Set-Content -Path $csprojPath.FullName -Value $csprojContent

# Parse directives from the script file
$scriptContent = Get-Content $ScriptPath
foreach ($line in $scriptContent) {
    if ($line -match '^#:package\s+(.+)@(.+)$') {
        $pkg = $matches[1]
        $ver = $matches[2]
        Write-Host "Adding package $pkg version $ver..." -ForegroundColor Gray
        dotnet add $csprojPath.FullName package $pkg --version $ver | Out-Null
    }
    elseif ($line -match '^#:package\s+(.+)$') {
        $pkg = $matches[1]
        Write-Host "Adding package $pkg..." -ForegroundColor Gray
        dotnet add $csprojPath.FullName package $pkg | Out-Null
    }
    elseif ($line -match '^#:project\s+(.+)$') {
        $projRelPath = $matches[1]
        $projPath = Resolve-Path (Join-Path (Split-Path $ScriptPath) $projRelPath)
        Write-Host "Adding project reference $projPath..." -ForegroundColor Gray
        dotnet add $csprojPath.FullName reference $projPath | Out-Null
    }
    elseif ($line -match '^#:property\s+(.+)=(.+)$') {
        $prop = $matches[1]
        $val = $matches[2]
        $csprojContent = Get-Content $csprojPath.FullName -Raw
        $csprojContent = $csprojContent -replace '</PropertyGroup>', "    <$prop>$val</$prop>`n  </PropertyGroup>"
        Set-Content -Path $csprojPath.FullName -Value $csprojContent
    }
}

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
