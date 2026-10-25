# ZTE ZM9011 (VID_19D2&PID_1710) 展锐串口驱动安装脚本
# 必须以【管理员】身份运行 PowerShell 后执行本脚本
# 依赖同目录下的 sprdvcom.inf / amd64\sprdvcom.sys

$ErrorActionPreference = 'Continue'
$inf = Join-Path $PSScriptRoot 'sprdvcom.inf'

Write-Host "== 1. Env check ==" -ForegroundColor Cyan
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host ("Administrator : {0}" -f $admin)
if (-not $admin) {
    Write-Host "FAIL: run this script in an elevated PowerShell." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $inf)) {
    Write-Host "FAIL: INF not found: $inf" -ForegroundColor Red
    exit 1
}

$sb = try { (Confirm-SecureBootUEFI) } catch { "Unknown($($_.Exception.Message))" }
Write-Host ("SecureBoot    : {0}" -f $sb)
$ts = (bcdedit /enum "{current}" | Select-String -Pattern 'testsigning|nointegritychecks' | Out-String).Trim()
Write-Host ("BootFlags     : {0}" -f ($(if ($ts) { $ts } else { 'none (driver signature enforcement is ON)' })))

Write-Host "`n== 2. Present ZM9011 interfaces ==" -ForegroundColor Cyan
Get-PnpDevice -PresentOnly |
    Where-Object { $_.InstanceId -like '*VID_19D2&PID_1710&MI_*' } |
    Select-Object InstanceId, Status, FriendlyName |
    Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host "== 3. Add driver package ==" -ForegroundColor Cyan
$out = & pnputil.exe /add-driver $inf /install 2>&1 | Out-String
Write-Host $out

if ($out -match '0xE000024B|0x800F024B|0xE0000247|签名|signature') {
    Write-Host @"
--------------------------------------------------------------------
INFERED FAILURE: the driver package signature is rejected.
This is expected: the INF was modified, so the original WHQL catalog
(sprdvcom.cat) no longer matches.

Required action (one of):
  A. Boot with driver signature enforcement disabled, then run again:
     Settings > System > Recovery > Advanced startup > Restart now
     > Troubleshoot > Advanced options > Startup Settings > Restart
     > press 7 (Disable driver signature enforcement)
     NOTE: if Secure Boot is ON, option 7 is hidden - disable Secure Boot
           in BIOS first, otherwise this path cannot work.
  B. Use the device-side hardware ID override (see README.md), which
     keeps the original signed INF untouched.
--------------------------------------------------------------------
"@ -ForegroundColor Yellow
    exit 2
}

Write-Host "`n== 4. Rescan hardware ==" -ForegroundColor Cyan
pnputil.exe /scan-devices | Out-Host
Start-Sleep -Seconds 3

Write-Host "`n== 5. Result ==" -ForegroundColor Cyan
Get-PnpDevice -PresentOnly |
    Where-Object { $_.InstanceId -like '*VID_19D2&PID_1710&MI_*' } |
    Select-Object Status, Class, FriendlyName, InstanceId |
    Format-Table -AutoSize | Out-String -Width 220 | Write-Host

# 注意：Win32_SerialPort 在新版 Windows 11 上常返回空集合，这里改用 PnP 设备类枚举
Get-PnpDevice -Class Ports -PresentOnly -ErrorAction SilentlyContinue |
    Select-Object Status, FriendlyName, InstanceId |
    Format-Table -AutoSize | Out-String -Width 220 | Write-Host

$comList = @(
    Get-PnpDevice -Class Ports -PresentOnly -ErrorAction SilentlyContinue |
        ForEach-Object { if ($_.FriendlyName -match '\((COM\d+)\)') { $Matches[1] } } |
        Sort-Object
)
Write-Host ("Present COM ports: {0}" -f ($(if ($comList) { $comList -join ', ' } else { '<none>' })))

Write-Host "Next: run test_at.ps1 to find which COM port answers AT."
