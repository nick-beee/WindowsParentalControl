# Test only (task 7.2 and 7.3 of request-identity-from-launch): as Kid, sends requests without a valid Request Token.
# Output: C:\Users\Public\kid-request-attack.txt
$out = 'C:\Users\Public\kid-request-attack.txt'
function Try-It($label, [scriptblock]$call) {
    try { $r = & $call; "$label -> OK $r" | Add-Content $out }
    catch { $s = $_.Exception.Response.StatusCode.value__; $body = ''; try { $body = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch {}; "$label -> $s $body" | Add-Content $out }
}
$body = '{"requestType":"extension","minutes":15,"reason":"x","username":"Kid2"}'
Try-It 'POST other user, no token' { Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/request -ContentType application/json -Body $body }
Try-It 'POST fake token' { Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5050/api/request -ContentType application/json -Body $body -Headers @{ Cookie = 'pc_request_token=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' } }
Try-It 'GET /request?user=Kid2' { (Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5050/request?user=Kid2').StatusCode }
Try-It 'GET /request' { (Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5050/request').StatusCode }
