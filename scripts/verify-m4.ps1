<#
.SYNOPSIS
    End-to-end verifier for milestone M4 (pause / resume / restart recovery).

.DESCRIPTION
    Exercises the M4 surface against the live API on http://127.0.0.1:5099:
      (a) negative transitions: pause/resume on unknown (404) and terminal (409) jobs
      (b) pause a live chunked download -> Paused + partial .part files on disk
      (c) restart the API -> the Paused job survives (startup must not auto-resume it)
      (d) resume it -> byte-exact completion (SHA-256 vs a single-connection baseline)
      (e) force-kill mid-download -> restart -> the interrupted job auto-resumes to completion

    HTTP is driven with curl.exe so status codes are read without PowerShell 5.1
    Invoke-WebRequest exception quirks. Exits non-zero if any check fails.
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

# Candidates are probed at runtime and the first reachable Range-capable one wins,
# so a transient mirror outage does not make the whole suite fail.
# Set $env:M4_RANGE_URL to force a specific target first (e.g. a local throttled
# Range server) - useful when the public internet is flaky or when .NET cannot
# reach a mirror that curl can (the probe below uses curl, not HttpClient).
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
    $apiOut = Join-Path $env:TEMP 'm4-api-out.log'
    $apiErr = Join-Path $env:TEMP 'm4-api-err.log'
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
    $out = Join-Path $env:TEMP ("m4-http-" + [guid]::NewGuid().ToString('N') + ".out")
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

function New-DownloadJob([string]$Url) {
    $bodyFile = Join-Path $env:TEMP ("m4-body-" + [guid]::NewGuid().ToString('N') + ".json")
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

$baseHash = $null

Info '================ M4 VERIFICATION ================'

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
    Info '--- (a) NEGATIVE TRANSITIONS ---'
    $randomId = [guid]::NewGuid().ToString()
    $r = Invoke-Curl -Method POST -Path "/downloads/$randomId/pause"
    if ($r.Code -eq 404 -and $r.Body.error) {
        Pass "pause unknown id -> 404 {error}"
    } else {
        Fail "pause unknown id -> $($r.Code) body=$($r.Raw)"
    }

    $r = Invoke-Curl -Method POST -Path "/downloads/$randomId/resume"
    if ($r.Code -eq 404 -and $r.Body.error) {
        Pass "resume unknown id -> 404 {error}"
    } else {
        Fail "resume unknown id -> $($r.Code) body=$($r.Raw)"
    }

    $c = New-DownloadJob $fallbackUrl
    if ($c.Code -eq 201) {
        $completedId = $c.Body.id
        Info "setup completed job: $completedId"
    } else {
        Fail "could not create setup job: $($c.Code) $($c.Raw)"
    }
    $cj = Wait-ForStatus $completedId @('Completed', 'Failed') 120
    if ($cj -and $cj.status -eq 'Completed') {
        Pass 'setup: fallback job Completed'
    } else {
        Fail "setup: fallback job status=$($cj.status) error=$($cj.error)"
    }

    $r = Invoke-Curl -Method POST -Path "/downloads/$completedId/pause"
    if ($r.Code -eq 409 -and $r.Body.error) {
        Pass "pause Completed job -> 409 {error}"
    } else {
        Fail "pause Completed job -> $($r.Code) body=$($r.Raw)"
    }

    $r = Invoke-Curl -Method POST -Path "/downloads/$completedId/resume"
    if ($r.Code -eq 409 -and $r.Body.error) {
        Pass "resume Completed (not-paused) job -> 409 {error}"
    } else {
        Fail "resume Completed job -> $($r.Code) body=$($r.Raw)"
    }

    Info ''
    Info '--- (b) PAUSE A LIVE CHUNKED DOWNLOAD ---'
    $p = New-DownloadJob $rangeUrl
    if ($p.Code -eq 201) {
        $pauseId = $p.Body.id
        Pass "created 100Mb chunked job: $pauseId"
    } else {
        Fail "could not create chunked job: $($p.Code) $($p.Raw)"
        throw 'cannot continue without a job'
    }

    $downloading = Wait-ForStatus $pauseId @('Downloading') 60
    if ($downloading) {
        Pass 'job reached Downloading'
    } else {
        Fail 'job never reached Downloading'
    }

    $progressed = Wait-ForBytes $pauseId 1048576 120
    if ($progressed -and $progressed.status -eq 'Downloading' -and [long]$progressed.bytesDownloaded -ge 1048576) {
        Pass "job progressed to $($progressed.bytesDownloaded) bytes while Downloading"
    } else {
        Fail "job did not progress enough before pause (status=$($progressed.status) bytes=$($progressed.bytesDownloaded))"
    }

    $pr = Invoke-Curl -Method POST -Path "/downloads/$pauseId/pause"
    if ($pr.Code -eq 200 -and $pr.Body.status -eq 'Paused') {
        Pass 'pause -> 200 status=Paused'
    } else {
        Fail "pause -> $($pr.Code) status=$($pr.Body.status) body=$($pr.Raw)"
    }

    Start-Sleep -Milliseconds 1000
    $pj = (Get-Job $pauseId).Body
    if ($pj.status -eq 'Paused') {
        Pass 'status remains Paused after the cancellation settles'
    } else {
        Fail "status drifted to $($pj.status) after pause"
    }

    $parts = Get-PartStats $pauseId
    if ($parts.Count -gt 0 -and $parts.Bytes -gt 0) {
        Pass "partial .part files on disk: count=$($parts.Count) bytes=$($parts.Bytes)"
    } else {
        Fail "expected partial .part files, found count=$($parts.Count) bytes=$($parts.Bytes)"
    }
    if ($parts.Bytes -lt $rangeBytes) {
        Pass "partial bytes ($($parts.Bytes)) < full size ($rangeBytes)"
    } else {
        Fail "partial bytes ($($parts.Bytes)) not smaller than full size"
    }

    Info ''
    Info '--- (c) RESTART: PAUSED JOB SURVIVES ---'
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue }
    Stop-ApiByPort
    Start-Sleep -Milliseconds 600
    $api = Start-Api
    Pass 'API restarted'

    $aj = (Get-Job $pauseId).Body
    if ($aj -and $aj.status -eq 'Paused') {
        Pass 'Paused job still Paused after restart (startup did not auto-resume it)'
    } else {
        Fail "paused job status after restart = $($aj.status)"
    }
    $parts2 = Get-PartStats $pauseId
    if ($parts2.Bytes -ge $parts.Bytes) {
        Pass "partial parts preserved across restart (bytes=$($parts2.Bytes))"
    } else {
        Fail "partial parts lost across restart (before=$($parts.Bytes) after=$($parts2.Bytes))"
    }

    Info ''
    Info '--- (d) RESUME -> COMPLETION (integrity) ---'
    $baseline = Join-Path $env:TEMP 'm4-baseline.bin'
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

        $rr = Invoke-Curl -Method POST -Path "/downloads/$pauseId/resume"
        if ($rr.Code -eq 200 -and $rr.Body.status -eq 'Queued') {
            Pass 'resume -> 200 status=Queued'
        } else {
            Fail "resume -> $($rr.Code) status=$($rr.Body.status) body=$($rr.Raw)"
        }

        $final = Wait-ForStatus $pauseId @('Completed', 'Failed') 600
        if ($final -and $final.status -eq 'Completed') {
            Pass 'resumed job Completed'
        } else {
            Fail "resumed job status=$($final.status) error=$($final.error)"
        }

        if ($final -and $final.bytesDownloaded -eq $rangeBytes -and $final.totalBytes -eq $rangeBytes) {
            Pass "resumed job bytes $($final.bytesDownloaded)/$($final.totalBytes)"
        } else {
            Fail "resumed job bytes=$($final.bytesDownloaded) total=$($final.totalBytes) expected=$rangeBytes"
        }

        if ($final -and $final.filePath -and (Test-Path $final.filePath)) {
            $finalLen = (Get-Item $final.filePath).Length
            $finalHash = (Get-FileHash $final.filePath -Algorithm SHA256).Hash
            if ($finalLen -eq $rangeBytes) {
                Pass "final file length=$finalLen"
            } else {
                Fail "final file length=$finalLen expected=$rangeBytes"
            }
            if ($finalHash -eq $baseHash) {
                Pass "final sha256 matches single-connection baseline"
            } else {
                Fail "sha256 mismatch: resumed=$finalHash baseline=$baseHash"
            }
        } else {
            Fail "final file not found: $($final.filePath)"
        }

        $parts3 = Get-PartStats $pauseId
        if ($parts3.Count -eq 0) {
            Pass 'part files cleaned up after completion'
        } else {
            Fail "part files remain after completion: count=$($parts3.Count)"
        }
    }

    Info ''
    Info '--- (e) INTERRUPTED DOWNLOAD -> AUTO-RESUME ON RESTART ---'
    $k = New-DownloadJob $rangeUrl
    if ($k.Code -eq 201) {
        $killId = $k.Body.id
        Pass "created 100Mb job: $killId"
    } else {
        Fail "could not create job: $($k.Code) $($k.Raw)"
    }

    $kd = Wait-ForStatus $killId @('Downloading') 60
    if ($kd) {
        Pass 'job reached Downloading'
    } else {
        Fail 'job never reached Downloading'
    }
    $kp = Wait-ForBytes $killId 1048576 120
    if ($kp -and $kp.status -eq 'Downloading' -and [long]$kp.bytesDownloaded -ge 1048576) {
        Pass "job progressed to $($kp.bytesDownloaded) bytes before the kill"
    } else {
        Fail "job did not progress before the kill (status=$($kp.status) bytes=$($kp.bytesDownloaded))"
    }

    $interruptedStatus = (Get-Job $killId).Body.status
    if ($interruptedStatus -eq 'Downloading') {
        Pass "status persisted as Downloading before forced kill"
    } else {
        Fail "expected Downloading before kill, saw $interruptedStatus"
    }

    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue }
    Stop-ApiByPort
    Start-Sleep -Milliseconds 800
    $api = Start-Api
    Pass 'API restarted after forced kill'

    $rj = Wait-ForStatus $killId @('Completed', 'Failed') 600
    if ($rj -and $rj.status -eq 'Completed') {
        Pass 'interrupted job auto-resumed and Completed'
    } else {
        Fail "interrupted job status=$($rj.status) error=$($rj.error)"
    }

    if ($baseHash -and $rj -and $rj.filePath -and (Test-Path $rj.filePath)) {
        $kHash = (Get-FileHash $rj.filePath -Algorithm SHA256).Hash
        if ($kHash -eq $baseHash) {
            Pass 'interrupted job sha256 matches single-connection baseline'
        } else {
            Fail "interrupted sha256 mismatch: $kHash baseline=$baseHash"
        }
    }
} finally {
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
