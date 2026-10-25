# 逐个试探 ZM9011 的 COM 口，找出真正的 AT 口
# 用法:  powershell -ExecutionPolicy Bypass -File .\test_at.ps1
# 无需管理员权限

param(
    [int[]]$BaudRates = @(115200, 9600)
)

# 端口枚举：不能用 Win32_SerialPort —— 该类在新版 Windows 11 上经常返回空集合，
# 会误报 "No COM ports found"。优先从 PnP 设备类 Ports 枚举（可同时拿到设备名），
# 回退到 .NET 的 SerialPort.GetPortNames()。
function Get-ZmSerialPorts {
    $result = @()
    try {
        $pnp = @(Get-PnpDevice -Class Ports -PresentOnly -ErrorAction Stop)
        foreach ($d in $pnp) {
            if ($d.FriendlyName -match '\((COM\d+)\)') {
                $result += [pscustomobject]@{
                    DeviceID   = $Matches[1]
                    Name       = $d.FriendlyName
                    InstanceId = $d.InstanceId
                }
            }
        }
    }
    catch {
        Write-Host ("[Get-ZmSerialPorts] PnP 枚举失败 error={0} stack={1}" -f $_.Exception.Message, $_.ScriptStackTrace) -ForegroundColor DarkYellow
    }

    if (-not $result) {
        # 回退路径：至少能拿到 COM 号，拿不到设备名
        foreach ($n in [System.IO.Ports.SerialPort]::GetPortNames()) {
            $result += [pscustomobject]@{ DeviceID = $n; Name = $n; InstanceId = '' }
        }
    }

    return @($result | Sort-Object { [int]($_.DeviceID -replace 'COM', '') })
}

$ports = Get-ZmSerialPorts

if (-not $ports) {
    Write-Host "No COM ports found. Install the driver first (run install_driver.ps1 as Administrator)." -ForegroundColor Red
    exit 1
}

Write-Host ("Found {0} port(s): {1}" -f $ports.Count, (($ports | ForEach-Object { $_.DeviceID }) -join ', ')) -ForegroundColor Cyan

foreach ($p in $ports) {
    Write-Host ("==== {0}  {1} ====" -f $p.DeviceID, $p.Name) -ForegroundColor Cyan
    foreach ($baud in $BaudRates) {
        $port = New-Object System.IO.Ports.SerialPort
        $port.PortName = $p.DeviceID
        $port.BaudRate = $baud
        $port.Parity = 'None'
        $port.DataBits = 8
        $port.StopBits = 'One'
        $port.ReadTimeout = 1500
        $port.WriteTimeout = 1000
        $port.DtrEnable = $true
        $port.RtsEnable = $true
        try {
            $port.Open()
            $port.DiscardInBuffer()
            $port.Write("AT`r`n")
            Start-Sleep -Milliseconds 600
            $resp = $port.ReadExisting()
            if ([string]::IsNullOrWhiteSpace($resp)) {
                Write-Host ("  {0,6} baud : <no response>" -f $baud) -ForegroundColor DarkGray
            }
            else {
                $clean = ($resp -replace "`r", '\r' -replace "`n", '\n')
                $color = if ($resp -match 'OK|ERROR|\+CME') { 'Green' } else { 'Yellow' }
                Write-Host ("  {0,6} baud : {1}" -f $baud, $clean) -ForegroundColor $color
            }
        }
        catch {
            # 打开失败需带端口、波特率、异常信息与堆栈，便于判断是口被占用还是驱动异常
            Write-Host ("  {0,6} baud : OPEN FAILED - {1}" -f $baud, $_.Exception.Message) -ForegroundColor DarkYellow
            Write-Host ("           [test_at.port={0} name={1} baud={2} error={3} stack={4}]" -f `
                    $p.DeviceID, $p.Name, $baud, $_.Exception.Message, $_.ScriptStackTrace) -ForegroundColor DarkGray
        }
        finally {
            if ($port.IsOpen) { $port.Close() }
            $port.Dispose()
        }
    }
}
