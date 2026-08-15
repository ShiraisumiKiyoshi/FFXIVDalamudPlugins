$r = Invoke-WebRequest -Uri 'http://localhost:8848/FreeAction/icon.png' -UseBasicParsing
Write-Host "Status: $($r.StatusCode)"
Write-Host "Content-Type: $($r.Headers['Content-Type'])"
Write-Host "Size: $($r.RawContentLength) bytes"
