$pdfPath = 'S:\EpsteinFiles\DepartmentofJustice\DOJ_Disclosures\DataSet 11\PDFs\EFTA02291542.pdf'
$encoded = [Uri]::EscapeDataString($pdfPath)
$url = "http://localhost:5022/api/images/view?path=$encoded"

Write-Host "Testing: $url"
Write-Host "File exists locally: $(Test-Path $pdfPath)"

try {
    $response = Invoke-WebRequest $url -UseBasicParsing
    Write-Host "SUCCESS: Status=$($response.StatusCode) ContentLength=$($response.RawContentLength) ContentType=$($response.Headers['Content-Type'])"
} catch {
    $status = $_.Exception.Response.StatusCode
    Write-Host "FAILED: Status=$status Message=$($_.Exception.Message)"
}
