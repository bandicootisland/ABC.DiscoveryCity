$recent = curl -s "http://localhost:5022/api/search/recent?limit=3"
Write-Host "Recent results JSON (first 500 chars):"
Write-Host ($recent.Substring(0, [Math]::Min(500, $recent.Length)))
Write-Host ""

# Parse and check fields
$data = $recent | ConvertFrom-Json
foreach ($d in $data) {
    Write-Host "---"
    Write-Host "FileName:  $($d.FileName)"
    Write-Host "FilePath:  $($d.FilePath)"
    Write-Host "ThumbPath: $($d.ThumbnailPath)"
    Write-Host "FullImg:   $($d.FullImagePath)"
}
