<#
.SYNOPSIS
  Live progress for a headless Unity Android build (Tools/build_and_install.ps1): stage, estimated progress,
  elapsed time, and two "is it stuck?" signals: how long since Unity last wrote its log, and which build tools
  are busy right now (shader compiler, IL2CPP/clang, Gradle) with their CPU use.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\watch_build.ps1
  powershell -ExecutionPolicy Bypass -File Tools\watch_build.ps1 -Once    # print one status line and exit
#>
param(
    [string]$Log = (Join-Path (Split-Path -Parent $PSScriptRoot) "Builds\build.log"),
    [string]$Apk = (Join-Path (Split-Path -Parent $PSScriptRoot) "Builds\QuestRealityCapture-live.apk"),
    [int]$PollSeconds = 2,
    [int]$QuietWarnSeconds = 300,
    [switch]$Once
)

# Stages in build order. Percent = where the bar starts; Seconds = typical first-build duration, used only to
# keep the bar moving inside long stages (Unity reports no percentages). Pattern = log text that marks the stage.
$stages = @(
    @{ Name = "Starting Unity / resolving packages"; Percent = 0;   Seconds = 120; Pattern = "COMMAND LINE ARGUMENTS|Resolving packages" },
    @{ Name = "Compiling C# scripts";                Percent = 6;   Seconds = 120; Pattern = "Package Manager\] Done resolving|DisplayProgressbar: Compiling Scripts" },
    @{ Name = "Starting the Android player build";   Percent = 12;  Seconds = 60;  Pattern = "BuildPlayer: start building target" },
    @{ Name = "Compiling shaders (slow the first time)"; Percent = 18; Seconds = 600; Pattern = "ompiling shader|shader variants|DisplayProgressbar: Building scene" },
    @{ Name = "Packaging assets";                    Percent = 50;  Seconds = 120; Pattern = "DisplayProgressbar: Packaging assets|Writing asset files" },
    @{ Name = "IL2CPP: C# -> C++ -> ARM64 (slow the first time)"; Percent = 58; Seconds = 600; Pattern = "il2cpp\.exe|Converting managed assemblies|DisplayProgressbar: .*IL2CPP" },
    @{ Name = "Gradle: assembling the APK";          Percent = 85;  Seconds = 240; Pattern = "Starting a Gradle Daemon|gradlew|DisplayProgressbar: Building Gradle" },
    @{ Name = "Finished";                            Percent = 100; Seconds = 1;   Pattern = "\[CommandLineBuild\] |Exiting batchmode|Aborting batchmode" }
)
# A busy tool is stronger evidence of the stage than log text.
$toolStage = @{ "UnityShaderCompiler" = 3; "il2cpp" = 5; "clang" = 5; "clang++" = 5; "ld.lld" = 5; "java" = 6 }
$toolNames = @("Unity", "UnityShaderCompiler", "il2cpp", "clang", "clang++", "ld.lld", "java", "bee_backend", "dotnet")

$offset = 0L
$stage = 0
$playerStarted = $false
$stageSince = Get-Date
$lastUnityLine = ""
$result = $null
$lastError = $null
$sampledTwice = $false
$prevCpu = @{}
$prevTime = Get-Date
$buildStart = $null

function Read-NewLogText {
    if (-not (Test-Path $Log)) { return $null }
    $fs = [System.IO.File]::Open($Log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($fs.Length -lt $script:offset) {
            # A new build truncated the log: start over.
            $script:offset = 0L; $script:stage = 0; $script:stageSince = Get-Date; $script:result = $null; $script:playerStarted = $false
        }
        [void]$fs.Seek($script:offset, [System.IO.SeekOrigin]::Begin)
        $reader = New-Object System.IO.StreamReader($fs)
        $text = $reader.ReadToEnd()
        $script:offset = $fs.Length
        return $text
    } finally { $fs.Dispose() }
}

function Format-Span([TimeSpan]$span) { "{0:00}:{1:00}" -f [Math]::Floor($span.TotalMinutes), $span.Seconds }

while ($true) {
    $text = Read-NewLogText
    if ($text) {
        if ($text -match $stages[2].Pattern) { $playerStarted = $true }
        for ($i = $stages.Count - 1; $i -gt $stage; $i--) {
            if ($i -ge 3 -and $i -lt $stages.Count - 1 -and -not $playerStarted) { continue }  # import also compiles shaders
            if ($text -match $stages[$i].Pattern) { $stage = $i; $stageSince = Get-Date; Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $stages[$i].Name); break }
        }
        $progressLines = [regex]::Matches($text, "DisplayProgressbar: ([^\r\n]+)")
        if ($progressLines.Count -gt 0) { $lastUnityLine = $progressLines[$progressLines.Count - 1].Groups[1].Value }
        # Compile errors mid-build can be transient (the API updater fixes some packages and recompiles), so only
        # the build's own summary or a batchmode abort counts as the result; the last C# error is kept for context.
        $errors = [regex]::Matches($text, "[^\r\n]*error CS\d+: [^\r\n]+")
        if ($errors.Count -gt 0) { $lastError = $errors[$errors.Count - 1].Value.Trim() }
        $done = [regex]::Match($text, "\[CommandLineBuild\] \w+: \d+ errors[^\r\n]*|Aborting batchmode due to failure:\s*[^\r\n]*")
        if ($done.Success -and -not $result) { $result = $done.Value }
    }

    # Busy build tools and their CPU use since the last poll.
    $now = Get-Date
    $wall = [Math]::Max(0.5, ($now - $prevTime).TotalSeconds)
    $busy = @{}
    $editorRunning = $false
    foreach ($proc in (Get-Process -Name $toolNames -ErrorAction SilentlyContinue)) {
        $path = ""
        try { $path = $proc.Path } catch {}
        if ($proc.ProcessName -eq "Unity") {
            if ($path -notlike "*\Hub\Editor\*") { continue }  # the Hub's own unity.exe helper
            $editorRunning = $true
            if (-not $buildStart) { try { $buildStart = $proc.StartTime } catch {} }
        }
        $cpu = 0.0
        try { $cpu = $proc.TotalProcessorTime.TotalSeconds } catch {}
        $key = "$($proc.Id)"
        $delta = 0.0
        if ($prevCpu.ContainsKey($key)) { $delta = [Math]::Max(0.0, $cpu - $prevCpu[$key]) }
        $prevCpu[$key] = $cpu
        if (-not $busy.ContainsKey($proc.ProcessName)) { $busy[$proc.ProcessName] = @{ Count = 0; Cores = 0.0 } }
        $busy[$proc.ProcessName].Count++
        $busy[$proc.ProcessName].Cores += $delta / $wall
        $toolAllowed = $playerStarted -and ($proc.ProcessName -ne "java" -or $stage -ge 5)  # java also runs for SDK checks
        if ($toolAllowed -and $toolStage.ContainsKey($proc.ProcessName) -and ($delta / $wall) -gt 0.05 -and $toolStage[$proc.ProcessName] -gt $stage) {
            $stage = $toolStage[$proc.ProcessName]; $stageSince = $now
            Write-Host ("[{0:HH:mm:ss}] {1}" -f $now, $stages[$stage].Name)
        }
    }
    $prevTime = $now
    $totalCores = 0.0
    $busyText = ($busy.GetEnumerator() | Sort-Object { -$_.Value.Cores } | ForEach-Object {
        $totalCores += $_.Value.Cores
        $count = ""
        if ($_.Value.Count -gt 1) { $count = " x$($_.Value.Count)" }
        "{0}{1} {2:0.0}" -f $_.Key, $count, $_.Value.Cores
    }) -join ", "
    if (-not $busyText) { $busyText = "none" }

    $logAge = 0
    if (Test-Path $Log) { $logAge = [int]($now - (Get-Item $Log).LastWriteTime).TotalSeconds }
    $elapsed = "--:--"
    if ($buildStart) { $elapsed = Format-Span ($now - $buildStart) }

    $current = $stages[$stage]
    $percent = $current.Percent
    if ($stage -lt $stages.Count - 1) {
        $next = $stages[$stage + 1].Percent
        $fraction = [Math]::Min(0.95, ($now - $stageSince).TotalSeconds / $current.Seconds)
        $percent = [int]($current.Percent + ($next - $current.Percent) * $fraction)
    }

    $health = "alive"
    if ($logAge -gt $QuietWarnSeconds -and $totalCores -lt 0.1) { $health = "POSSIBLY STUCK (log quiet ${logAge}s and no CPU)" }
    elseif ($logAge -gt $QuietWarnSeconds) { $health = "log quiet ${logAge}s but tools are busy: still working" }

    $status = "$($current.Name) | elapsed $elapsed | ~$percent% (estimate)"
    $operation = "log written ${logAge}s ago | CPU cores busy: $([Math]::Round($totalCores, 1)) ($busyText) | $health"
    if ($lastUnityLine) { $operation += " | Unity: $lastUnityLine" }

    if ($Once) {
        if (-not $sampledTwice) { $sampledTwice = $true; Start-Sleep -Seconds 1; continue }  # CPU use needs two samples
        Write-Host $status; Write-Host $operation; if ($result) { Write-Host "Result: $result" }; break
    }
    Write-Progress -Activity "Unity Android build" -Status $status -PercentComplete $percent -CurrentOperation $operation

    if (-not $editorRunning -and ($result -or $stage -eq $stages.Count - 1)) {
        Write-Progress -Activity "Unity Android build" -Completed
        $apkFresh = (Test-Path $Apk) -and $buildStart -and ((Get-Item $Apk).LastWriteTime -gt $buildStart)
        if ($apkFresh) {
            Write-Host ("[{0:HH:mm:ss}] BUILD SUCCEEDED in {1}: {2} ({3:0} MB)" -f $now, $elapsed, $Apk, ((Get-Item $Apk).Length / 1MB)) -ForegroundColor Green
        } else {
            Write-Host ("[{0:HH:mm:ss}] BUILD FAILED after {1}: {2}" -f $now, $elapsed, $result) -ForegroundColor Red
            if ($lastError) { Write-Host "Last compile error: $lastError" }
            Write-Host "Full log: $Log"
        }
        break
    }
    Start-Sleep -Seconds $PollSeconds
}
