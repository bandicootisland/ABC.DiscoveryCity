$pdfPath = 'S:\EpsteinFiles\DepartmentofJustice\DOJ_Disclosures\DataSet 11\PDFs\EFTA02291542.pdf'
Write-Host "File size: $((Get-Item $pdfPath).Length / 1MB) MB"

$encoded = [Uri]::EscapeDataString($pdfPath)
$url = "http://localhost:5022/api/images/view?path=$encoded"

# Use HEAD-like approach - just check status
try {
    $response = Invoke-WebRequest $url -UseBasicParsing -Method Head
    Write-Host "HEAD: Status=$($response.StatusCode)"
} catch {
    Write-Host "HEAD failed: $($_.Exception.Message)"
}

# Try with small range
try {
    $response = Invoke-WebRequest $url -UseBasicParsing -Headers @{Range='bytes=0-1023'}
    Write-Host "RANGE: Status=$($response.StatusCode) len=$($response.RawContentLength)"
} catch {
    Write-Host "RANGE failed, trying GET..."
    try {
        $response = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10
        Write-Host "GET: Status=$($response.StatusCode) len=$($response.RawContentLength)"
    } catch {
        Write-Host "GET failed: $($_.Exception.Response.StatusCode) $($_.Exception.Message)"
    }
}
