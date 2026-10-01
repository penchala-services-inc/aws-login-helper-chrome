# Talks to AWS-Login-Helper-Host-Chrome.exe using the exact same wire protocol Chrome uses for native
# messaging (4-byte little-endian length prefix + UTF-8 JSON), bypassing Chrome entirely.
# This tells us whether the exe itself is broken, or whether the problem is specific to how
# Chrome invokes it.

$exe = "$env:LocalAppData\AWS-Login-Helper-Host-Chrome\NativeHost\AWS-Login-Helper-Host-Chrome.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Could not find $exe" -ForegroundColor Red
    exit 1
}

$json = '{"action":"ping"}'
$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
$len = [BitConverter]::GetBytes($bytes.Length)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$p = New-Object System.Diagnostics.Process
$p.StartInfo = $psi
[void]$p.Start()

$stdin = $p.StandardInput.BaseStream
$stdin.Write($len, 0, 4)
$stdin.Write($bytes, 0, $bytes.Length)
$stdin.Flush()
$stdin.Close()

$stdout = $p.StandardOutput.BaseStream
$respLenBytes = New-Object byte[] 4
$readCount = $stdout.Read($respLenBytes, 0, 4)

if ($readCount -lt 4) {
    Write-Host "Got fewer than 4 bytes back for the length prefix (readCount=$readCount) - the exe likely crashed or wrote nothing." -ForegroundColor Red
} else {
    $respLen = [BitConverter]::ToInt32($respLenBytes, 0)
    Write-Host "Response length prefix: $respLen bytes"
    $respBytes = New-Object byte[] $respLen
    $stdout.Read($respBytes, 0, $respLen) | Out-Null
    $text = [System.Text.Encoding]::UTF8.GetString($respBytes)
    Write-Host "Response JSON: $text" -ForegroundColor Green
}

$exited = $p.WaitForExit(5000)
Write-Host "Process exited within 5s: $exited"
if ($exited) { Write-Host "Exit code: $($p.ExitCode)" }

$stderrText = $p.StandardError.ReadToEnd()
if ($stderrText) {
    Write-Host "----- stderr -----" -ForegroundColor Yellow
    Write-Host $stderrText
}
