<#
.SYNOPSIS
    End-to-end verifier for milestone M5 (time-window scheduling).

.DESCRIPTION
    Exercises the M5 surface against the live API on http://127.0.0.1:5099:
      (a) GET /schedule returns the schedule view (enabled/start/end/isOpen/nextOpenAt/now)
      (b) PUT /schedule rejects malformed times with 400 {error}
      (c) window CLOSED -> a created job parks as Scheduled and does NOT download
      (d) window OPEN  -> the parked job auto-promotes and completes byte-exact
      (e) window closes mid-download -> job parks as Scheduled with partial .part files
      (f) window reopens -> the parked job resumes to a byte-exact completion

    HTTP is driven with curl.exe so status codes are read without PowerShell 5.1
    Invoke-WebRequest exception quirks. The schedule is always restored to the
    disabled default in the finally block so M3/M4 can run afterwards. Exits
    non-zero if any check fails.

    The Range mirror is probed at runtime; set $env:M4_RANGE_URL to force a target
    first (e.g. the local hermetic Range server) when the public internet is flaky.
#>
#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $repo 'LoveDownload.sln'
$apiDll = Join-Path $repo 'src\LoveDownload.Api\bin\Debug\net9.0\LoveDownload.Api.dll'
$downloadsDir = Join-Path $repo 'downloads'
$baseUrl = 'http://127.0.0.1:5099'
$fallbackUrl = "$baseUrl/downloads"

$mirrorCandidates = @()
if ($env:M4_RANGE_URL) { $mirrorCandidates += $env:M4_RANGE_URL }
$mirrorCandidates += @(
    'https://ash-speed.hetzner.com/100MB.bin',
    'https://proof.ovh.net/files/100Mb.dat',
    'https://speed.hetzner.de/100MB.bin'
)
$rangeUrl = $null
$rangeBytes = 0

$script:PassCount = 0
$script:FailCount = 0

function Pass([string]$Message) {
    Write-Host "PASS: $Message" -ForegroundColor Green
    $script:PassCount++
}

function Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    $script:FailCount++
}

function Info([string]$Message) {
    Write-Host $Message
}

function Stop-ApiByPort {
    $listeners = Get-NetTCPConnection -LocalPort 5099 -State Listen -ErrorAction SilentlyContinue
    foreach ($listener in $listeners) {
        Stop-Process -Id $listener.OwningProcess -Force -ErrorAction SilentlyContinue
    }
}

function Get-UsableInterfaces {
    $types = @('Ethernet', 'GigabitEthernet', 'FastEthernetT', 'FastEthernetFx', 'Ethernet3Megabit', 'Wireless80211', 'Ppp')
    $results = @()
    foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($nic.OperationalStatus -ne 'Up' -or $types -notcontains $nic.NetworkInterfaceType) { continue }
        foreach ($unicast in $nic.GetIPProperties().UnicastAddresses) {
            $address = $unicast.Address
            if ($address.AddressFamily -ne 'InterNetwork') { continue }
            if ([System.Net.IPAddress]::IsLoopback($address)) { continue }
            if ($address.IPAddressToString -like '169.254.*') { continue }
            $results += [pscustomobject]@{ Name = $nic.Name; IP = $address.IPAddressToString }
        }
    }
    return $results
}

function Start-Api {
    $apiOut = Join-Path $env:TEMP 'm5-api-out.log'
    $apiErr = Join-Path $env:TEMP 'm5-api-err.log'
    Remove-Item $apiOut, $apiErr -Force -ErrorAction SilentlyContinue
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) -PassThru -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden
    $ready = $false
    for ($i = 0; $i -lt 80; $i++) {
        try {
            $probe = Invoke-WebRequest -UseBasicParsing "$baseUrl/downloads" -TimeoutSec 2
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) {
        Get-Content $apiErr -ErrorAction SilentlyContinue | Write-Host
        throw 'API did not become ready on 5099'
    }
    return $proc
}

function Invoke-Curl {
    param([string]$Method, [string]$Path, [string]$BodyFile)
    $out = Join-Path $env:TEMP ("m5-http-" + [guid]::NewGuid().ToString('N') + ".out")
    $curlArgs = @('-s', '-o', $out, '-w', '%{http_code}', '--max-time', '180', '-X', $Method)
    if ($BodyFile) {
        $curlArgs += @('-H', 'Content-Type: application/json', '--data-binary', "@$BodyFile")
    }
    $curlArgs += "$baseUrl$Path"
    $code = & curl.exe @curlArgs 2>$null
    if ($code -notmatch '^\d+$') { $code = 0 }
    $raw = $null
    if (Test-Path $out) { $raw = Get-Content $out -Raw -ErrorAction SilentlyContinue }
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    $obj = $null
    if ($raw -and $raw.Trim()) {
        try { $obj = $raw | ConvertFrom-Json } catch { $obj = $null }
    }
    return [pscustomobject]@{ Code = [int]$code; Body = $obj; Raw = $raw }
}

function Set-Schedule {
    param([bool]$Enabled, [string]$Start, [string]$End)
    $bodyFile = Join-Path $env:TEMP ("m5-sched-" + [guid]::NewGuid().ToString('N') + ".json")
    $payload = @{ enabled = $Enabled; start = $Start; end = $End } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($bodyFile, $payload, (New-Object System.Text.UTF8Encoding($false)))
    $r = Invoke-Curl -Method PUT -Path '/schedule' -BodyFile $bodyFile
    Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue
    return $r
}

function Get-Schedule {
    return Invoke-Curl -Method GET -Path '/schedule'
}

function New-DownloadJob([string]$Url) {
    $bodyFile = Join-Path $env:TEMP ("m5-body-" + [guid]::NewGuid().ToString('N') + ".json")
    $payload = @{ url = $Url } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($bodyFile, $payload, (New-Object System.Text.UTF8Encoding($false)))
    $r = Invoke-Curl -Method POST -Path '/downloads' -BodyFile $bodyFile
    Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue
    return $r
}

function Get-Job([string]$Id) {
    return Invoke-Curl -Method GET -Path "/downloads/$Id"
}

function Wait-ForStatus([string]$Id, [string[]]$Statuses, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $r = Get-Job $Id
        if ($r.Body -and ($Statuses -contains $r.Body.status)) { return $r.Body }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Wait-ForBytes([string]$Id, [long]$MinBytes, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $r = Get-Job $Id
        if ($r.Body) {
            if ($r.Body.status -eq 'Completed' -or $r.Body.status -eq 'Failed') { return $r.Body }
            if ([long]$r.Body.bytesDownloaded -ge $MinBytes) { return $r.Body }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Get-PartStats([string]$JobId) {
    $dir = Join-Path $downloadsDir $JobId
    if (-not (Test-Path $dir)) { return [pscustomobject]@{ Count = 0; Bytes = 0 } }
    $parts = @(Get-ChildItem -Path $dir -Filter '*.part' -File -ErrorAction SilentlyContinue)
    $sum = 0
    foreach ($p in $parts) { $sum += $p.Length }
    return [pscustomobject]@{ Count = $parts.Count; Bytes = $sum }
}

# Closed window that cannot contain "now": start = now+60m, end = now+120m.
# Open window that always contains "now": start = now-120m, end = now+120m.
function Get-ClosedWindow {
    $now = Get-Date
    return [pscustomobject]@{
        Start = $now.AddMinutes(60).ToString('HH:mm')
        End   = $now.AddMinutes(120).ToString('HH:mm')
    }
}
function Get-OpenWindow {
    $now = Get-Date
    return [pscustomobject]@{
        Start = $now.AddMinutes(-120).ToString('HH:mm')
        End   = $now.AddMinutes(120).ToString('HH:mm')
    }
}

$baseHash = $null

Info '================ M5 VERIFICATION ================'

Info '--- Building ---'
$buildOut = & dotnet build $sln -c Debug -nologo 2>&1
$buildOut | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -eq 0) {
    Pass 'dotnet build (Debug) succeeded'
} else {
    Fail 'dotnet build failed'
    exit 1
}
$warnLine = ($buildOut | Select-String -Pattern 'Warning\(s\)' | Select-Object -First 1).Line
if ($warnLine -match '^\s*0\s+Warning') {
    Pass 'dotnet build: 0 warnings'
} else {
    Fail "dotnet build warnings: $warnLine"
}

Info '--- Selecting a reachable Range-capable mirror ---'
foreach ($candidate in $mirrorCandidates) {
    $probeHeaders = & curl.exe -sS -D - -o NUL --max-time 20 -4 -r 0-99 $candidate 2>$null
    $rangeLine = $probeHeaders | Select-String -Pattern 'Content-Range:\s*bytes\s+\d+-\d+/(\d+)' | Select-Object -First 1
    if ($rangeLine) {
        $rangeBytes = [long]$rangeLine.Matches[0].Groups[1].Value
        $rangeUrl = $candidate
        break
    }
}
if (-not $rangeUrl -or $rangeBytes -le 0) {
    Fail 'no reachable Range-capable mirror found (network unavailable?)'
    exit 1
} else {
    Pass "range mirror: $rangeUrl ($rangeBytes bytes)"
}

$interfaces = @(Get-UsableInterfaces)
if ($interfaces.Count -eq 0) {
    Info 'no usable interface detected (using default routing)'
    $selected = $null
} else {
    $selected = $interfaces | Select-Object -First 1
    Pass "detected $($interfaces.Count) usable interface address(es) (using '$($selected.Name)')"
}

Stop-ApiByPort
Start-Sleep -Milliseconds 500
if ($selected) { $env:Engine__InterfaceAllowList__0 = $selected.Name }

$api = $null
try {
    $api = Start-Api
    Pass "API ready on $baseUrl"

    Info ''
    Info '--- (a) GET /schedule SHAPE ---'
    $g = Get-Schedule
    $fieldsOk = $g.Code -eq 200 -and $g.Body -ne $null -and
        ($g.Body.PSObject.Properties.Name -contains 'enabled') -and
        ($g.Body.PSObject.Properties.Name -contains 'start') -and
        ($g.Body.PSObject.Properties.Name -contains 'end') -and
        ($g.Body.PSObject.Properties.Name -contains 'isOpen') -and
        ($g.Body.PSObject.Properties.Name -contains 'nextOpenAt') -and
        ($g.Body.PSObject.Properties.Name -contains 'now')
    if ($fieldsOk) {
        Pass "GET /schedule -> 200 with all fields (start=$($g.Body.start) end=$($g.Body.end) isOpen=$($g.Body.isOpen))"
    } else {
        Fail "GET /schedule shape: code=$($g.Code) raw=$($g.Raw)"
    }
    if ($g.Body.start -match '^\d{2}:\d{2}$' -and $g.Body.end -match '^\d{2}:\d{2}$') {
        Pass 'start/end are HH:mm'
    } else {
        Fail "start/end not HH:mm: start=$($g.Body.start) end=$($g.Body.end)"
    }

    Info ''
    Info '--- (b) PUT /schedule REJECTS MALFORMED TIMES ---'
    $r = Set-Schedule -Enabled $true -Start '25:00' -End '05:00'
    if ($r.Code -eq 400 -and $r.Body.error) {
        Pass "PUT start=25:00 -> 400 {error}"
    } else {
        Fail "PUT start=25:00 -> $($r.Code) body=$($r.Raw)"
    }
    $r = Set-Schedule -Enabled $true -Start 'abc' -End '05:00'
    if ($r.Code -eq 400 -and $r.Body.error) {
        Pass "PUT start=abc -> 400 {error}"
    } else {
        Fail "PUT start=abc -> $($r.Code) body=$($r.Raw)"
    }

    Info ''
    Info '--- (c) CLOSED WINDOW -> job parks as Scheduled, no download ---'
    $closed = Get-ClosedWindow
    $sc = Set-Schedule -Enabled $true -Start $closed.Start -End $closed.End
    if ($sc.Code -eq 200 -and $sc.Body.isOpen -eq $false) {
        Pass "closed window set ($($closed.Start)-$($closed.End)) isOpen=false"
    } else {
        Fail "closed window set -> $($sc.Code) isOpen=$($sc.Body.isOpen)"
    }
    if ($sc.Body.nextOpenAt) {
        Pass "nextOpenAt reported for a closed window ($($sc.Body.nextOpenAt))"
    } else {
        Fail 'nextOpenAt missing for a closed window'
    }

    $c = New-DownloadJob $rangeUrl
    if ($c.Code -eq 201 -and $c.Body.status -eq 'Scheduled') {
        $schedId = $c.Body.id
        Pass "job created while closed -> status=Scheduled ($schedId)"
    } else {
        Fail "job while closed -> $($c.Code) status=$($c.Body.status) raw=$($c.Raw)"
        throw 'cannot continue without a Scheduled job'
    }
    Start-Sleep -Seconds 3
    $cj = (Get-Job $schedId).Body
    if ($cj.status -eq 'Scheduled' -and [long]$cj.bytesDownloaded -eq 0) {
        Pass 'job stayed Scheduled with 0 bytes after 3s (no download outside the window)'
    } else {
        Fail "job did not park: status=$($cj.status) bytes=$($cj.bytesDownloaded)"
    }
    $cp = Get-PartStats $schedId
    if ($cp.Count -eq 0) {
        Pass 'no .part files created while the window is closed'
    } else {
        Fail "unexpected .part files while closed: count=$($cp.Count)"
    }

    Info ''
    Info '--- (d) OPEN WINDOW -> parked job auto-promotes and completes ---'
    $open = Get-OpenWindow
    $so = Set-Schedule -Enabled $true -Start $open.Start -End $open.End
    if ($so.Code -eq 200 -and $so.Body.isOpen -eq $true) {
        Pass "open window set ($($open.Start)-$($open.End)) isOpen=true"
    } else {
        Fail "open window set -> $($so.Code) isOpen=$($so.Body.isOpen)"
    }

    $promoted = Wait-ForStatus $schedId @('Downloading') 20
    if ($promoted) {
        Pass 'parked job auto-promoted to Downloading when the window opened'
    } else {
        Fail 'parked job never promoted after the window opened'
    }

    $baseline = Join-Path $env:TEMP 'm5-baseline.bin'
    Remove-Item $baseline -Force -ErrorAction SilentlyContinue
    & curl.exe -s --fail --max-time 600 -o $baseline $rangeUrl
    if ($LASTEXITCODE -ne 0) {
        Fail "baseline single-connection curl download failed ($rangeUrl)"
    } else {
        $baseHash = (Get-FileHash $baseline -Algorithm SHA256).Hash
        $baseLen = (Get-Item $baseline).Length
        Info "baseline (curl, single connection): $baseLen bytes sha256=$baseHash"
        if ($baseLen -eq $rangeBytes) {
            Pass "baseline length $baseLen"
        } else {
            Fail "baseline length $baseLen expected=$rangeBytes"
        }
    }

    $fin = Wait-ForStatus $schedId @('Completed', 'Failed') 600
    if ($fin -and $fin.status -eq 'Completed') {
        Pass 'window-opened job Completed'
    } else {
        Fail "window-opened job status=$($fin.status) error=$($fin.error)"
    }
    if ($baseHash -and $fin -and $fin.filePath -and (Test-Path $fin.filePath)) {
        $h = (Get-FileHash $fin.filePath -Algorithm SHA256).Hash
        $l = (Get-Item $fin.filePath).Length
        if ($l -eq $rangeBytes -and $h -eq $baseHash) {
            Pass "window-opened job byte-exact ($l bytes, sha256 match)"
        } else {
            Fail "window-opened job mismatch: len=$l sha=$h baseline=$baseHash"
        }
    } else {
        Fail "window-opened job file missing: $($fin.filePath)"
    }

    Info ''
    Info '--- (e) WINDOW CLOSES MID-DOWNLOAD -> parks with partial .part files ---'
    $m = New-DownloadJob $rangeUrl
    if ($m.Code -eq 201 -and $m.Body.status -eq 'Queued') {
        $midId = $m.Body.id
        Pass "created job while open -> status=Queued ($midId)"
    } else {
        Fail "job while open -> $($m.Code) status=$($m.Body.status) raw=$($m.Raw)"
        throw 'cannot continue without a Queued job'
    }
    $midDl = Wait-ForStatus $midId @('Downloading') 60
    if ($midDl) { Pass 'mid job reached Downloading' } else { Fail 'mid job never reached Downloading' }
    $midProg = Wait-ForBytes $midId 1048576 120
    if ($midProg -and $midProg.status -eq 'Downloading' -and [long]$midProg.bytesDownloaded -ge 1048576) {
        Pass "mid job progressed to $($midProg.bytesDownloaded) bytes"
    } else {
        Fail "mid job did not progress (status=$($midProg.status) bytes=$($midProg.bytesDownloaded))"
    }

    $closed2 = Get-ClosedWindow
    $sc2 = Set-Schedule -Enabled $true -Start $closed2.Start -End $closed2.End
    if ($sc2.Code -eq 200 -and $sc2.Body.isOpen -eq $false) {
        Pass 'window closed for the mid-download test'
    } else {
        Fail "mid close -> $($sc2.Code) isOpen=$($sc2.Body.isOpen)"
    }

    $parked = Wait-ForStatus $midId @('Scheduled') 20
    if ($parked) {
        Pass 'mid-download job parked as Scheduled when the window closed'
    } else {
        Fail "mid job did not park (status=$((Get-Job $midId).Body.status))"
    }
    Start-Sleep -Milliseconds 800
    $mp = Get-PartStats $midId
    if ($mp.Count -gt 0 -and $mp.Bytes -gt 0 -and $mp.Bytes -lt $rangeBytes) {
        Pass "partial .part files preserved while parked: count=$($mp.Count) bytes=$($mp.Bytes)"
    } else {
        Fail "expected partial .part files while parked: count=$($mp.Count) bytes=$($mp.Bytes) total=$rangeBytes"
    }

    Info ''
    Info '--- (f) WINDOW REOPENS -> parked job resumes to byte-exact completion ---'
    $open2 = Get-OpenWindow
    $so2 = Set-Schedule -Enabled $true -Start $open2.Start -End $open2.End
    if ($so2.Code -eq 200 -and $so2.Body.isOpen -eq $true) {
        Pass 'window reopened'
    } else {
        Fail "reopen -> $($so2.Code) isOpen=$($so2.Body.isOpen)"
    }

    $fin2 = Wait-ForStatus $midId @('Completed', 'Failed') 600
    if ($fin2 -and $fin2.status -eq 'Completed') {
        Pass 'resumed (window-reopened) job Completed'
    } else {
        Fail "resumed job status=$($fin2.status) error=$($fin2.error)"
    }
    if ($baseHash -and $fin2 -and $fin2.filePath -and (Test-Path $fin2.filePath)) {
        $h2 = (Get-FileHash $fin2.filePath -Algorithm SHA256).Hash
        $l2 = (Get-Item $fin2.filePath).Length
        if ($l2 -eq $rangeBytes -and $h2 -eq $baseHash) {
            Pass "resumed job byte-exact ($l2 bytes, sha256 match)"
        } else {
            Fail "resumed job mismatch: len=$l2 sha=$h2 baseline=$baseHash"
        }
    } else {
        Fail "resumed job file missing: $($fin2.filePath)"
    }
    $mp2 = Get-PartStats $midId
    if ($mp2.Count -eq 0) {
        Pass 'part files cleaned up after resumed completion'
    } else {
        Fail "part files remain after resumed completion: count=$($mp2.Count)"
    }
} finally {
    # Always restore the disabled default so M3/M4 are unaffected by a leftover window.
    try { Set-Schedule -Enabled $false -Start '01:00' -End '05:00' | Out-Null } catch { }
    if ($api -and -not $api.HasExited) {
        Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
    }
    Stop-ApiByPort
    Remove-Item Env:\Engine__InterfaceAllowList__0 -ErrorAction SilentlyContinue
}

Info ''
Info '================ SUMMARY ================'
Info "PASS=$($script:PassCount) FAIL=$($script:FailCount)"
if ($script:FailCount -gt 0) {
    exit 1
}
exit 0
