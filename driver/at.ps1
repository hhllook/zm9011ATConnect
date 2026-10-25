# ZM9011 AT 命令单发工具
# 用法:
#   powershell -ExecutionPolicy Bypass -File .\at.ps1 -Command "AT+CSQ"
#   powershell -ExecutionPolicy Bypass -File .\at.ps1 -Port COM6 -Command "ATI"
#   powershell -ExecutionPolicy Bypass -File .\at.ps1 -Interactive
# 默认参数依据实测: 115200 8N1, DTR/RTS 置高, 命令以 CRLF 结尾

param(
    [string]$Port = 'COM3',
    [string]$Command = '',
    [int]$BaudRate = 115200,
    [int]$TimeoutMs = 3000,
    [switch]$Interactive
)

# 打开串口并设置实测可用的参数
function Open-ZmPort {
    $sp = New-Object System.IO.Ports.SerialPort
    $sp.PortName = $Port
    $sp.BaudRate = $BaudRate
    $sp.Parity = 'None'
    $sp.DataBits = 8
    $sp.StopBits = 'One'
    $sp.Handshake = 'None'
    $sp.ReadTimeout = $TimeoutMs
    $sp.WriteTimeout = 1000
    $sp.DtrEnable = $true    # ZM9011 必须置高 DTR/RTS，否则不回话
    $sp.RtsEnable = $true
    try {
        $sp.Open()
    }
    catch {
        Write-Host ("[at.ps1.Open-ZmPort] 串口打开失败 port={0} baud={1} error={2} stack={3}" -f `
                $Port, $BaudRate, $_.Exception.Message, $_.ScriptStackTrace) -ForegroundColor Red
        throw
    }
    return $sp
}

# 发送一条 AT 命令并读取响应，直到 OK / ERROR / 超时
function Send-AtCommand {
    param($SerialPort, [string]$Cmd)

    $SerialPort.DiscardInBuffer()
    $SerialPort.Write($Cmd.Trim() + "`r`n")

    $sb = New-Object System.Text.StringBuilder
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        try {
            $sb.Append($SerialPort.ReadExisting()) | Out-Null
        }
        catch {
            # ReadExisting 一般不抛异常，这里仅兜底记录
            Write-Host ("[at.ps1.Send-AtCommand] 读取失败 port={0} cmd={1} error={2} stack={3}" -f `
                    $SerialPort.PortName, $Cmd, $_.Exception.Message, $_.ScriptStackTrace) -ForegroundColor Red
            break
        }
        # 有明确终结符就提前结束，避免每条命令都等满超时
        if ($sb.ToString() -match '(OK|ERROR|\+CME ERROR|\+CMS ERROR)\s*$') { break }
        Start-Sleep -Milliseconds 50
    }
    return $sb.ToString()
}

function Show-Response {
    param([string]$Raw)
    if ([string]::IsNullOrWhiteSpace($Raw)) {
        Write-Host '  <no response>' -ForegroundColor DarkGray
        return
    }
    # 逐行输出，去掉空行
    foreach ($line in ($Raw -split "`r?`n")) {
        if ($line.Trim()) { Write-Host ('  ' + $line) }
    }
}

$sp = $null
try {
    $sp = Open-ZmPort
    Write-Host ("Connected {0} @ {1} 8N1 (DTR/RTS on)" -f $Port, $BaudRate) -ForegroundColor Cyan

    if ($Interactive) {
        Write-Host "输入 AT 命令，空行或 exit 退出。" -ForegroundColor DarkGray
        while ($true) {
            $line = Read-Host '>'
            if ([string]::IsNullOrWhiteSpace($line) -or $line.Trim() -eq 'exit') { break }
            Show-Response (Send-AtCommand -SerialPort $sp -Cmd $line)
        }
    }
    elseif ($Command) {
        Show-Response (Send-AtCommand -SerialPort $sp -Cmd $Command)
    }
    else {
        Write-Host '需要 -Command "AT..." 或 -Interactive。' -ForegroundColor Yellow
    }
}
finally {
    if ($sp) {
        if ($sp.IsOpen) { $sp.Close() }
        $sp.Dispose()
    }
}
