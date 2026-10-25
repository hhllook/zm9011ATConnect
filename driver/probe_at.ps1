# 临时脚本：对比两个可应答 AT 的串口，取设备标识信息
# 用法: powershell -ExecutionPolicy Bypass -File .\probe_at.ps1
param(
    [string[]]$Ports = @('COM3', 'COM6'),
    [int]$BaudRate = 115200
)

$atCmds = @('ATI', 'AT+CGMM', 'AT+CGMI', 'AT+CGMR', 'AT+CPIN?', 'AT+CSQ')

foreach ($name in $Ports) {
    $sp = New-Object System.IO.Ports.SerialPort
    $sp.PortName = $name
    $sp.BaudRate = $BaudRate
    $sp.Parity = 'None'
    $sp.DataBits = 8
    $sp.StopBits = 'One'
    $sp.ReadTimeout = 1500
    $sp.WriteTimeout = 1000
    $sp.DtrEnable = $true
    $sp.RtsEnable = $true

    Write-Host ("==== {0} ====" -f $name) -ForegroundColor Cyan
    try {
        $sp.Open()
        foreach ($cmd in $atCmds) {
            $sp.DiscardInBuffer()
            $sp.Write($cmd + "`r`n")
            Start-Sleep -Milliseconds 500
            $resp = $sp.ReadExisting()
            $clean = (($resp -replace "`r", '' -replace "`n", ' | ').Trim())
            Write-Host ("  {0,-10} -> {1}" -f $cmd, $clean)
        }
    }
    catch {
        # 失败时打印端口、波特率、异常信息与堆栈
        Write-Host ("  [probe_at] 串口交互失败 port={0} baud={1} error={2} stack={3}" -f `
                $name, $BaudRate, $_.Exception.Message, $_.ScriptStackTrace) -ForegroundColor Red
    }
    finally {
        if ($sp.IsOpen) { $sp.Close() }
        $sp.Dispose()
    }
}
