# Stop the adb.exe logcat stream started by this job's scripts (matched by the exact "-T <since>" value).
# Git Bash cannot always kill the native adb.exe behind a background job, so common.sh calls this as a safety net.
param([Parameter(Mandatory = $true)][string]$Since)
$pattern = '*logcat -v epoch -b main,system,crash,events -T ' + $Since + '*'
Get-CimInstance Win32_Process -Filter "name='adb.exe'" |
    Where-Object { $_.CommandLine -like $pattern } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -Confirm:$false; "stopped adb logcat pid $($_.ProcessId)" }
