# Starts the EdgeSpan virtual mouse and keyboard on an Android device.
# The window stays open while the server runs. Close it to stop injection.
param(
    [string]$Address = ""
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$adb = Join-Path $here "platform-tools\adb.exe"
if (-not (Test-Path $adb)) { $adb = "adb" }
$dex = Join-Path $here "edgestpan-server.dex"
if (-not (Test-Path $dex)) {
    Write-Error "edgestpan-server.dex was not found next to this script."
}

if ($Address) {
    & $adb connect $Address
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $adb push $dex /data/local/tmp/edgestpan-server.dex
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "EdgeSpan injection server is running. Leave this window open."
& $adb shell "CLASSPATH=/data/local/tmp/edgestpan-server.dex app_process / com.edgestpan.server.ServerMain"
exit $LASTEXITCODE
