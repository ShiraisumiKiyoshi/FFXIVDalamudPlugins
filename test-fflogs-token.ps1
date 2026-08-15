# FFLogs OAuth Token Test
# Usage: run this script, input your Client ID and Secret when prompted.
# It tests both cn.fflogs.com and www.fflogs.com to find which one your credentials work on.

$clientId = Read-Host "Client ID"
$sec = Read-Host "Client Secret" -AsSecureString
$plainSecret = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))

Write-Host ""
Write-Host ("Client ID length: " + $clientId.Length + ", first 4: " + $clientId.Substring(0, [Math]::Min(4, $clientId.Length)) + "...") -ForegroundColor Cyan
Write-Host ("Secret length: " + $plainSecret.Length) -ForegroundColor Cyan
Write-Host ""

$endpoints = @(
    @{ Name = "cn.fflogs.com";  Url = "https://cn.fflogs.com/oauth/token" },
    @{ Name = "www.fflogs.com"; Url = "https://www.fflogs.com/oauth/token" }
)

foreach ($ep in $endpoints) {
    Write-Host ("========== Testing " + $ep.Name + " ==========") -ForegroundColor Yellow
    try {
        $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($clientId + ":" + $plainSecret)))
        $headers = @{
            "Authorization" = ("Basic " + $auth)
            "Content-Type"  = "application/x-www-form-urlencoded"
            "Accept"        = "application/json"
        }
        $r = Invoke-WebRequest -Uri $ep.Url -Method Post -Headers $headers -Body "grant_type=client_credentials" -UseBasicParsing -ErrorAction Stop
        Write-Host "SUCCESS!" -ForegroundColor Green
        $json = $r.Content | ConvertFrom-Json
        Write-Host ("  access_token (first 20): " + $json.access_token.Substring(0, [Math]::Min(20, $json.access_token.Length)) + "...")
        Write-Host ("  expires_in: " + $json.expires_in + " seconds")
        Write-Host ("  >>> Your credentials work on " + $ep.Name + ". Use this domain in the plugin.") -ForegroundColor Cyan
    } catch {
        Write-Host ("FAIL: " + $_.Exception.Message) -ForegroundColor Red
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            Write-Host ("  Response: " + $_.ErrorDetails.Message) -ForegroundColor Red
        }
        if ($_.Exception.Response) {
            Write-Host ("  HTTP status: " + [int]$_.Exception.Response.StatusCode) -ForegroundColor Red
        }
    }
    Write-Host ""
}

Write-Host "Done." -ForegroundColor Cyan
Write-Host "If both failed:"
Write-Host "  1. Double-check Client ID/Secret at https://www.fflogs.com/api/clients/"
Write-Host "  2. When creating the client, do NOT check any optional boxes (OAuth flows)"
Write-Host "  3. Wait a few minutes for the client to activate, then retry"
Write-Host ""
Read-Host "Press Enter to close"
