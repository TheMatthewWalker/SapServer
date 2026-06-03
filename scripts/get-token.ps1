$secret = $env:Auth__JwtSecret
if (-not $secret) {
    $secret = Read-Host "JWT secret (Auth__JwtSecret)"
}

$header  = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256","typ":"JWT"}')).TrimEnd('=').Replace('+','-').Replace('/','_')
$now     = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("{`"iss`":`"sql2005-bridge`",`"aud`":`"sap-server`",`"sub`":`"dev`",`"userId`":1,`"role`":`"admin`",`"iat`":$now,`"exp`":$($now+3600)}")).TrimEnd('=').Replace('+','-').Replace('/','_')

$hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($secret))
$sig  = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$header.$payload"))).TrimEnd('=').Replace('+','-').Replace('/','_')

$token = "$header.$payload.$sig"
Write-Host "`nBearer $token`n" -ForegroundColor Green
$token | Set-Clipboard
Write-Host "(copied to clipboard)" -ForegroundColor DarkGray
