<#
.SYNOPSIS
  Build the live-streaming capture app headlessly, install it on the headset over (wireless) adb, and launch it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\build_and_install.ps1
  powershell -ExecutionPolicy Bypass -File Tools\build_and_install.ps1 -Device 192.168.0.86:5555 -SkipBuild
#>
param(
    [string]$Unity = "",
    [string]$Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
    [string]$Device = "",
    [string]$UnityVersion = "6000.4.5f1",
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$apk = Join-Path $repo "Builds\QuestRealityCapture-live.apk"
$log = Join-Path $repo "Builds\build.log"
$package = "com.arulrajesh.QuestRealityCaptureLive"  # installs beside the stock com.t34400 app (different signature)

if (-not $SkipBuild) {
    if (-not $Unity) {
        $candidates = @(
            "$env:USERPROFILE\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe",
            "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe",
            "$env:ProgramFiles\Unity $UnityVersion\Editor\Unity.exe"
        )
        $Unity = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $Unity) { throw "Unity $UnityVersion not found. Install it with Android Build Support via Unity Hub, or pass -Unity <path to Unity.exe>." }
    New-Item -ItemType Directory -Force (Split-Path $apk) | Out-Null
    Write-Host "Building $apk with $Unity (log: $log) ..."
    $arguments = @("-batchmode", "-quit", "-projectPath", "`"$repo`"", "-buildTarget", "Android",
                   "-executeMethod", "RealityLog.EditorTools.CommandLineBuild.BuildAndroid",
                   "-outputPath", "`"$apk`"", "-logFile", "`"$log`"")
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0 -or -not (Test-Path $apk)) {
        Get-Content $log -Tail 60
        throw "Unity build failed (exit $($process.ExitCode)). See $log"
    }
}

$target = if ($Device) { @("-s", $Device) } else { @() }
Write-Host "Installing $apk ..."
& $Adb @target install -r $apk
if ($LASTEXITCODE -ne 0) { throw "adb install failed" }
& $Adb @target shell monkey -p $package -c android.intent.category.LAUNCHER 1 | Out-Null
Write-Host "Installed and launched. On the laptop, run: mq3drecon live"
