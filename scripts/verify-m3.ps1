#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $repo 'LoveDownload.sln'
$apiDll = Join-Path $repo 'src\LoveDownload.Api\bin\Debug\net9.0\LoveDownload.Api.dll'
$baseUrl = 'http://127.0.0.1:5099'
$rangeUrl = 'https://proof.ovh.net/files/10Mb.dat'
$rangeBytes = 10485760
$fallbackUrl = "$baseUrl/downloads"

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

function New-DownloadJob([string]$Url) {
    $body = @{ url = $Url } | ConvertTo-Json -Compress
    $created = Invoke-RestMethod -Method Post -Uri "$baseUrl/downloads" -ContentType 'application/json' -Body $body
    return $created.id
}

function Wait-DownloadJob([string]$Id, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $job = Invoke-RestMethod -Uri "$baseUrl/downloads/$Id"
        if ($job.status -eq 'Completed' -or $job.status -eq 'Failed') {
            return $job
        }
        Start-Sleep -Milliseconds 500
    }
    throw "job $Id did not finish within $TimeoutSeconds seconds"
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
            $results += [pscustomobject]@{ Name = $nic.Name; Description = $nic.Description; IP = $address.IPAddressToString }
        }
    }
    return $results
}

Info '================ M3 VERIFICATION ================'

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

$interfaces = @(Get-UsableInterfaces)
Info '--- Usable network interfaces (selector rules: Up, non-loopback, Ethernet/Wireless/Ppp, IPv4 unicast, non-link-local) ---'
if ($interfaces.Count -eq 0) {
    Info '(none detected)'
    Fail 'no usable interfaces detected on this machine'
} else {
    $interfaces | Format-Table Name, Description, IP -AutoSize | Out-String | Write-Host
    Pass "detected $($interfaces.Count) usable interface address(es)"
}

$selected = $interfaces | Select-Object -First 1
$allowEnv = $null
if ($selected) {
    $allowEnv = "Engine__InterfaceAllowList__0=$($selected.Name)"
    Info "selecting interface via config env: $allowEnv"
}

Stop-ApiByPort
Start-Sleep -Milliseconds 500

$apiOut = Join-Path $env:TEMP 'm3-api-out.log'
$apiErr = Join-Path $env:TEMP 'm3-api-err.log'
Remove-Item $apiOut, $apiErr -Force -ErrorAction SilentlyContinue

if ($selected) {
    $env:Engine__InterfaceAllowList__0 = $selected.Name
}
$api = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) -PassThru -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $probe = Invoke-WebRequest -UseBasicParsing "$baseUrl/downloads" -TimeoutSec 2
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $ready) {
        Fail 'API did not become ready on 5099'
        Get-Content $apiErr -ErrorAction SilentlyContinue | Write-Host
        throw 'API not ready'
    }
    Pass "API ready on $baseUrl (GET /downloads -> $($probe.StatusCode), schema rebuilt)"

    Info ''
    Info '--- (a) CHUNKED INTEGRITY ---'
    try {
        $baseline = Join-Path $env:TEMP 'm3-baseline.bin'
        Remove-Item $baseline -Force -ErrorAction SilentlyContinue
        & curl.exe -s --fail --max-time 180 -o $baseline $rangeUrl
        if ($LASTEXITCODE -ne 0) {
            Fail "baseline single-connection curl download failed ($rangeUrl)"
        } else {
            $baseHash = (Get-FileHash $baseline -Algorithm SHA256).Hash
            $baseLen = (Get-Item $baseline).Length
            Info "baseline (curl, single connection): $baseLen bytes sha256=$baseHash"

            $jobId = New-DownloadJob $rangeUrl
            Info "chunked job: $jobId"
            $job = Wait-DownloadJob $jobId 300

            if ($job.status -ne 'Completed') {
                Fail "chunked job status=$($job.status) error=$($job.error)"
            } else {
                if ($job.bytesDownloaded -eq $rangeBytes -and $job.totalBytes -eq $rangeBytes) {
                    Pass "job reported $($job.bytesDownloaded)/$($job.totalBytes) bytes"
                } else {
                    Fail "job bytes=$($job.bytesDownloaded) total=$($job.totalBytes) expected=$rangeBytes"
                }

                if ($job.connectionsUsed -gt 1) {
                    Pass "connectionsUsed=$($job.connectionsUsed) (>1 => chunked, not single-connection)"
                } else {
                    Fail "connectionsUsed=$($job.connectionsUsed) (expected >1)"
                }

                if ($job.interfacesUsed) {
                    Pass "interfacesUsed=$($job.interfacesUsed)"
                } else {
                    Fail 'interfacesUsed empty (expected a bound local endpoint)'
                }

                if (Test-Path $job.filePath) {
                    $finalLen = (Get-Item $job.filePath).Length
                    $finalHash = (Get-FileHash $job.filePath -Algorithm SHA256).Hash
                    if ($finalLen -eq $rangeBytes) {
                        Pass "final file length=$finalLen"
                    } else {
                        Fail "final file length=$finalLen expected=$rangeBytes"
                    }
                    if ($finalHash -eq $baseHash) {
                        Pass "final sha256 matches single-connection baseline ($finalHash)"
                    } else {
                        Fail "sha256 mismatch: chunked=$finalHash baseline=$baseHash"
                    }
                } else {
                    Fail "file not found: $($job.filePath)"
                }
            }
        }
    } catch {
        Fail "check (a) threw: $($_.Exception.Message)"
    }

    Info ''
    Info '--- (b) FALLBACK (no Range / unknown length) ---'
    try {
        $headers = & curl.exe -s -D - -o NUL --max-time 20 $fallbackUrl
        $rangedHeaders = & curl.exe -s -D - -o NUL --max-time 20 -r 0-99 $fallbackUrl
        $advertisesRanges = [bool]($headers | Select-String -Pattern 'accept-ranges' -Quiet)
        $returns206 = [bool]($rangedHeaders | Select-String -Pattern 'HTTP/[0-9.]+ 206' -Quiet)

        if (-not $advertisesRanges -and -not $returns206) {
            Pass "fallback target ($fallbackUrl) advertises no Range support"
        } else {
            Fail "fallback target unexpectedly supports Range (accept-ranges=$advertisesRanges, 206=$returns206)"
        }

        $jobId = New-DownloadJob $fallbackUrl
        Info "fallback job: $jobId"
        $job = Wait-DownloadJob $jobId 120

        if ($job.status -eq 'Completed' -and $job.bytesDownloaded -gt 0) {
            Pass "fallback job completed: status=$($job.status) bytes=$($job.bytesDownloaded)"
        } else {
            Fail "fallback job status=$($job.status) bytes=$($job.bytesDownloaded) error=$($job.error)"
        }

        if ($job.connectionsUsed -eq 1) {
            Pass "fallback used the single-connection path (connectionsUsed=1)"
        } else {
            Fail "fallback connectionsUsed=$($job.connectionsUsed) (expected 1)"
        }

        if ($job.filePath -and (Test-Path $job.filePath)) {
            $content = Get-Content $job.filePath -Raw -ErrorAction SilentlyContinue
            if ($content -and $content.TrimStart().StartsWith('[')) {
                Pass 'fallback file content is the API JSON payload'
            } else {
                Fail 'fallback file content unexpected'
            }
        } else {
            Fail "fallback file not found: $($job.filePath)"
        }
    } catch {
        Fail "check (b) threw: $($_.Exception.Message)"
    }

    Info ''
    Info '--- (c) INTERFACE BINDING ---'
    try {
        if (-not $selected) {
            Fail 'cannot run binding check: no usable interface detected'
        } else {
            Info "selected interface: name='$($selected.Name)' ip=$($selected.IP)"
            $jobId = New-DownloadJob $rangeUrl
            Info "chunked job (bound): $jobId"
            $job = Wait-DownloadJob $jobId 300

            if ($job.status -eq 'Completed' -and $job.error -eq $null) {
                Pass "bound chunked job completed without exception (connectionsUsed=$($job.connectionsUsed))"
            } else {
                Fail "bound chunked job status=$($job.status) error=$($job.error)"
            }

            if ($job.interfacesUsed -and $job.interfacesUsed -like "*$($selected.Name)*") {
                Pass "local endpoint(s) actually used per connection: $($job.interfacesUsed)"
            } else {
                Fail "interfacesUsed='$($job.interfacesUsed)' does not include selected interface '$($selected.Name)'"
            }

            if ($job.interfacesUsed -and $job.interfacesUsed -like "*$($selected.IP)*") {
                Pass "bound local IP $($selected.IP) confirmed"
            } else {
                Fail "bound local IP $($selected.IP) not found in '$($job.interfacesUsed)'"
            }
        }
    } catch {
        Fail "check (c) threw: $($_.Exception.Message)"
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
