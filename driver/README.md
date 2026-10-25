# ZTE ZM9011（Unisoc 平台）AT 口驱动修改包


## 1. 设备实测信息（Windows 11，2026-10-24）

父设备：`USB\VID_19D2&PID_1710\749016800217`（Unisoc 平台，设备自报名称 "Unisoc Phone"）

| 接口 | USB 类 | 枚举结果 | 处理 |
|---|---|---|---|
| MI_00 | e0/01/03（RNDIS） | 正常，系统自带驱动 | 不动 |
| MI_02 | ff/00/00（厂商私有） | 无驱动（Error） | 本包接管 |
| MI_03 | ff/00/00（厂商私有） | 无驱动（Error） | 本包接管 |
| MI_04 | ff/00/00（厂商私有） | 无驱动（Error） | 本包接管 |
| MI_05 | ff/00/00（厂商私有） | 无驱动（Error） | 本包接管 |

**关键结论**：四个私有口描述符完全相同（`Class_ff&SubClass_00&Prot_00`，名字都叫 "Unisoc Phone"），
无法从 USB 描述符判断哪个是 AT 口，因此本包把 MI_02~MI_05 全部映射为串口，
装完后用 `test_at.ps1` 逐个发 `AT` 试探确认。

## 2. 本包做了什么

基于 `RviBo.Com.SPD_Driver\Driver_R4.21.3201\DriversForWin10\Drivers\sprdvcom.inf`（UNISOC 官方 WHQL 包）
生成修改版，原始驱动包**未改动**：

1. `[SPRD]`、`[SPRD.NTx86]`、`[SPRD.NTAMD64]` 三个制造商段各注入 4 条硬件 ID：
   `USB\VID_19D2&PID_1710&MI_02` ~ `MI_05`，全部绑定到 `Serial_Inst`（普通串口）。
2. 注释掉 `CatalogFile = sprdvcom.cat` —— **INF 一旦修改，原 WHQL 目录签名必然失效**，
   保留该行会导致安装时直接报签名错误。
3. 注释掉 `ExcludeFromSelect=*` —— 否则修改后的 INF 不会出现在手动选择的驱动列表中。
4. `[Strings]` 增加 `ZTE.MI02`~`ZTE.MI05`，设备管理器里显示为 "ZTE ZM9011 Port MI_0x"。

驱动文件本身（`amd64\sprdvcom.sys`）保持 UNISOC 原厂 EV 签名，未做任何改动。

## 3. 安装路线 A：禁用驱动签名强制（推荐，成功率最高）

前提：本机 Secure Boot **必须关闭**，否则启动设置里不会出现"禁用驱动程序强制签名"选项。

1. 重启进入：设置 → 系统 → 恢复 → 高级启动 → 立即重新启动
   → 疑难解答 → 高级选项 → 启动设置 → 重新启动 → 按 **7**（禁用驱动程序强制签名）
2. 进入系统后，以**管理员**运行 PowerShell：
   ```
   cd <仓库根目录>\driver
   powershell -ExecutionPolicy Bypass -File .\install_driver.ps1
   ```
   脚本会打印 Secure Boot 状态、设备状态，并用 `pnputil` 安装、重新扫描。
   也可以用设备管理器逐个安装：
   右键 "Unisoc Phone"（MI_03）→ 更新驱动程序 → 浏览 → 让我从可用列表中选取
   → **从磁盘安装** → 选择本目录的 `sprdvcom.inf` → 弹"无法验证发布者"时选"始终安装"。
3. 安装完成后重启回正常模式，驱动应继续可用（`sprdvcom.sys` 自身签名有效，
   缺失的只是 INF 的 WHQL 目录签名，该签名只在安装阶段被检查）。

> 注意：如果 Secure Boot 开着且你不方便进 BIOS 关闭，路线 A 走不通。

## 4. 安装路线 B：设备侧硬件 ID 覆盖（不改 INF，保留原签名）

思路：不动 INF/签名，而是给设备节点追加一个原厂 INF 里已存在的硬件 ID，
让 Windows 用**原始已签名**的 `sprdvcom.inf` 去匹配。

- 目标节点：`HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_19D2&PID_1710&MI_03\6&1AE85370&0&0003`
- 在 `HardwareID`（REG_MULTI_SZ）中追加一条原厂条目，例如 `USB\VID_1782&PID_5D03&MI_02`（原厂中该条对应 AT 口）
- 之后禁用/启用该设备或更新驱动。

风险：`Enum` 下的键受 SYSTEM/TrustedInstaller 保护，改权限较麻烦；且设备重新枚举时
PnP 可能把 `HardwareID` 写回真实值，成功率不确定。**仅在路线 A 不可行时尝试。**

## 5. 验证哪个口是 AT

```
powershell -ExecutionPolicy Bypass -File .\test_at.ps1
```

脚本会对每个 COM 口以 115200 和 9600 各发一次 `AT`，返回 `OK` 的就是 AT 口。
`probe_at.ps1` 可进一步对指定口发送 `ATI` / `AT+CGMM` / `AT+CGMR` / `AT+CPIN?` / `AT+CSQ`：

```
powershell -ExecutionPolicy Bypass -File .\probe_at.ps1                      # 默认探测 COM3、COM6
powershell -ExecutionPolicy Bypass -File .\probe_at.ps1 -Ports COM4,COM5
```

### 2026-10-24 实测结果（本机）

| 接口 | COM | 115200 | 9600 | 结论 |
|---|---|---|---|---|
| MI_02 | COM6 | OK | OK | AT 口 |
| MI_03 | COM4 | 无响应 | 无响应 | 非 AT（诊断/日志类） |
| MI_04 | COM5 | 无响应 | 无响应 | 非 AT（诊断/日志类） |
| MI_05 | COM3 | OK | OK | AT 口 |

`ATI` 在 COM3 和 COM6 上返回完全相同的信息，说明设备把 AT 命令通道同时映射到了这两个接口：

```
Manufacturer: ZTE CORPORATION
Model: ZM9011
Revision: BD_RONDSZM9011V1.0.0B01
IMEI: 869224060030117
+GCAP: +CGSM, +DS
```

`AT+CPIN?` 返回 `+CME ERROR: 10`（SIM 未插入），`AT+CSQ` 返回 `+CSQ: 99,99`（无信号），
说明 AT 通道本身工作正常，只是当前没有 SIM 卡。

> **注意**：不要用 `Get-CimInstance Win32_SerialPort` 枚举端口。该 WMI 类在新版 Windows 11
> 上会返回空集合，导致脚本误报 "No COM ports found"。本目录脚本已改用
> `Get-PnpDevice -Class Ports` 枚举，并回退到 `[System.IO.Ports.SerialPort]::GetPortNames()`。
> 另：COM 号按枚举顺序分配，拔插后可能变化，以设备管理器中的 (COMx) 为准。

## 6. 发送 AT 命令

### 6.1 用 at.ps1（推荐）

```
# 单发一条
powershell -ExecutionPolicy Bypass -File .\at.ps1 -Command "AT+CSQ"

# 指定端口（默认 COM3）
powershell -ExecutionPolicy Bypass -File .\at.ps1 -Port COM6 -Command "ATI"

# 交互模式：逐行输入，空行或 exit 退出
powershell -ExecutionPolicy Bypass -File .\at.ps1 -Interactive

# 其它参数
- BaudRate  默认 115200
- TimeoutMs 默认 3000（等待 OK/ERROR 的超时）
```

串口参数固定为 **115200 8N1、无流控、DTR 和 RTS 置高**。DTR/RTS 不置高设备不会回话，
这是 ZM9011 实测出来的必要条件。

### 6.2 在自己的 PowerShell 脚本里调用

```powershell
$sp = New-Object System.IO.Ports.SerialPort 'COM3',115200,'None',8,'One'
$sp.DtrEnable = $true
$sp.RtsEnable = $true
$sp.ReadTimeout = 2000
$sp.Open()
$sp.Write("AT+CSQ`r`n")          # 必须以 CRLF 结尾
Start-Sleep -Milliseconds 500
$resp = $sp.ReadExisting()       # 或按 OK/ERROR 循环读到终结符
$sp.Close(); $sp.Dispose()
```

要点：命令结尾必须是 `\r\n`；发送后需给设备 200~600 ms 响应时间；
读取时建议循环读到 `OK` / `ERROR` / `+CME ERROR` 为止，固定 sleep 容易截断长响应。

### 6.3 用现成的串口工具

PuTTY → Serial：COM3，Speed `115200`，Connection type `Serial`，Data bits 8、Stop bits 1、
Parity None、Flow control None；`Terminal` 里把 "Local echo"、"Local line editing" 关掉，
Implicit CR in every LF 视需要调整。也可用 SecureCRT、sscom、XCOM 等，参数相同。

### 6.4 常用命令

| 命令 | 作用 |
|---|---|
| `AT` | 握手，返回 `OK` |
| `ATI` | 厂商 / 型号 / 版本 / IMEI |
| `AT+CGMM` / `AT+CGMI` / `AT+CGMR` | 型号 / 厂商 / 固件版本 |
| `AT+CGSN` | IMEI |
| `AT+CPIN?` | SIM 状态（`+CME ERROR: 10` = 未插卡） |
| `AT+CSQ` | 信号强度，`99,99` = 无信号 |
| `AT+CREG?` | 网络注册状态 |
| `AT+COPS?` | 当前运营商 |
| `AT+CGDCONT?` | PDP 上下文配置 |

> 本机实测（2026-10-24）：`AT+CSQ` → `+CSQ: 99,99`；`AT+CREG?` → `+CREG: 2,8,...`
> （2 = 正在搜索，8 = 仅紧急服务）；`AT+CPIN?` → `+CME ERROR: 10`。均为**未插 SIM 卡**的正常表现。

## 7. 卸载 / 回滚

```
pnputil /delete-driver oemXX.inf /uninstall /force
```
`oemXX.inf` 用 `pnputil /enum-drivers` 查找 Provider 为 "UNISOC Communications Inc."、
且版本为 4.21.32.134 的项。或在设备管理器中卸载设备并勾选"删除此设备的驱动程序软件"。

原始驱动包路径（未改动）：
`D:\zm9011\RviBo.Com.SPD_Driver\Driver_R4.21.3201\DriversForWin10\`
