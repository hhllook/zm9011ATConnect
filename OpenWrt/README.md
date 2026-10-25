# ZM9011 OpenWrt 版（zm9011d）

在 OpenWrt 路由器上直接运行 ZM9011 模组调试/实时短信服务。手机或电脑只需打开浏览器访问，不依赖 Windows 桌面程序。

目标设备：**ARM64（Cortex-A53 / 高通 IPQ6000）**，OpenWrt 24.10。
单文件静态二进制，无运行时依赖；Web 前端已内嵌，无需 Node/nginx。

---

## 已实现功能（MVP）

- 串口 AT 队列、URC 分发、自动重连
- AT 口自动探测（`device 'auto'`）
- 实时短信：`AT+CNMI=2,1,0,0,0` → `+CMTI` → `AT+CMGR` → PDU 解码
- 中文/UCS2、GSM7 短信解码
- 验证码识别（密码/验证码/校验码/动态码等）
- 手机式会话界面：收件箱、验证码卡片、复制密码、查看原文、回复
- 短信本地持久化（`/etc/zm9011/messages.json`）
- 设备信息、网络与信号查询
- 实时系统/AT 日志（SSE 推送）
- 设置页 + AT 调试控制台
- HTTP Basic Auth（可选）
- 响应式布局：电脑双栏、手机单栏（点会话进入聊天，返回键回列表）

尚未包含（后续可加）：FOTA 升级页、锁频段/锁小区/数据拨号等完整 AT 页面、短信 webhook/MQTT 通知、LuCI 集成、ipk 打包。

---

## 目录结构

```
OpenWrt/
├── build.ps1                 Windows 交叉编译脚本
├── build.sh                  Linux/macOS/WSL 交叉编译脚本
├── files/
│   ├── etc/config/zm9011     UCI 配置
│   └── etc/init.d/zm9011     procd 启动脚本
└── zm9011d/                  Go 源码
    ├── cmd/zm9011d/
    ├── internal/at/          串口 AT 客户端
    ├── internal/sms/         PDU 解码、验证码、短信服务
    ├── internal/serial/      Linux 串口（raw 8N1）
    ├── internal/store/       本地 JSON 存储
    ├── internal/api/         HTTP API + SSE
    └── internal/webui/web/   内嵌 Web 前端
```

---

## 编译

需要 Go 1.23+（仅在电脑上编译，路由器不需要 Go）。

### Windows

```powershell
cd OpenWrt
.\build.ps1
# 如果 go 不在 PATH：
.\build.ps1 -Go "C:\Program Files\Go\bin\go.exe"
```

### Linux / macOS / WSL

```bash
cd OpenWrt
chmod +x build.sh
./build.sh
```

产物：

- `zm9011d/dist/zm9011d-linux-arm64`（IPQ6000 使用这个）
- `zm9011d/dist/zm9011d-linux-armv7`（兼容旧设备）

二进制约 6–7 MB，静态链接，可直接在 OpenWrt（musl）运行。

---

## 安装包（推荐）

`build.ps1` / `build.sh` 会自动生成两个可直接导入路由器的包：

| 文件 | 适用系统 | 说明 |
|---|---|---|
| `zm9011d/dist/zm9011d-0.1.0-aarch64-openwrt.tar.gz` | **apk / opkg 均可** | 通用离线安装包，内含 `install.sh`，最稳妥 |
| `zm9011d/dist/zm9011d_0.1.0-1_aarch64_cortex-a53.ipk` | opkg（23.05 及保留 opkg 的 24.10 固件） | 可直接 `opkg install`，也会出现在 LuCI 软件包列表 |

**OpenWrt 24.10 默认使用 `apk`，不能直接安装 `.ipk`。** 先确认：

```sh
which apk opkg
cat /etc/apk/arch 2>/dev/null || opkg print-architecture
```

### 方式 A：通用离线安装包（推荐，apk/opkg 都适用）

```bash
scp zm9011d/dist/zm9011d-0.1.0-aarch64-openwrt.tar.gz root@192.168.1.1:/tmp/

ssh root@192.168.1.1 '
  mkdir -p /tmp/zm9011d-install
  tar -xzf /tmp/zm9011d-0.1.0-aarch64-openwrt.tar.gz -C /tmp/zm9011d-install
  sh /tmp/zm9011d-install/install.sh
'
```

安装脚本会：

- 复制二进制到 `/usr/bin/zm9011d`
- 首次安装时写入 `/etc/config/zm9011`（升级时保留已有配置）
- 安装 procd 启动脚本并启动服务
- 打印访问地址

### 方式 B：ipk（opkg 固件）

```bash
scp zm9011d/dist/zm9011d_0.1.0-1_aarch64_cortex-a53.ipk root@192.168.1.1:/tmp/
ssh root@192.168.1.1 'opkg install /tmp/zm9011d_0.1.0-1_aarch64_cortex-a53.ipk'
```

ipk 的 postinst 会自动 enable/start；配置 `/etc/config/zm9011` 标记为 conffile，升级不会覆盖。

如果提示架构不匹配：

```sh
opkg print-architecture
# 用实际架构重新生成：
cd OpenWrt/zm9011d
go run ./tools/mkpackage -binary dist/zm9011d-linux-arm64 \
  -config ../files/etc/config/zm9011 -init ../files/etc/init.d/zm9011 \
  -version 0.1.0 -arch <你的架构> -outdir dist
```

### 方式 C：原生 apk（24.10 / LuCI 上传）

如果你需要标准 `.apk` 包（例如通过 LuCI 上传安装），用 OpenWrt SDK 编译 `OpenWrt/package/zm9011d`：

1. 在 Linux / WSL 下载与固件匹配的 SDK（IPQ6000 一般是 `mediatek/filogic` 24.10.x）。
2. 把 `OpenWrt/package/zm9011d` 整个目录复制到 SDK 的 `package/` 下。
3. 编译：

   ```bash
   make package/zm9011d/compile V=s
   ```

4. 产物在 SDK 的 `bin/packages/<架构>/base/`（或 `packages/`）下，例如：

   ```bash
   apk add --allow-untrusted /tmp/zm9011d-0.1.0-r1.apk
   # 或 opkg install /tmp/zm9011d_0.1.0-1_<架构>.ipk
   ```

`package/zm9011d/files/zm9011d` 是预编译的 aarch64 二进制；目标架构不同时替换它再编译。

---

## 部署到路由器（手动方式）

以下命令在电脑上执行（把 IP 换成路由器 LAN 地址）。

```bash
# 1. 上传文件
scp zm9011d/dist/zm9011d-linux-arm64 root@192.168.1.1:/usr/bin/zm9011d
scp files/etc/config/zm9011           root@192.168.1.1:/etc/config/zm9011
scp files/etc/init.d/zm9011           root@192.168.1.1:/etc/init.d/zm9011

# 2. 设置权限并启动
ssh root@192.168.1.1 '
  chmod +x /usr/bin/zm9011d /etc/init.d/zm9011
  mkdir -p /etc/zm9011
  /etc/init.d/zm9011 enable
  /etc/init.d/zm9011 start
'
```

如果路由器缺少 USB 串口驱动：

```sh
opkg update
opkg install kmod-usb-serial kmod-usb-serial-option
```

访问：

```
http://192.168.1.1:8080
```

---

## 找到 AT 口 / 处理与上网的冲突（重点）

Windows 上看到的 `ZTE ZM9011 Port MI_05 (COM3)` 说明模组通过 USB 暴露了多个接口。OpenWrt 下通常会生成 `/dev/ttyUSB0` ~ `/dev/ttyUSB3`，AT 口可能是其中之一。

先看设备与驱动：

```sh
ls -l /dev/ttyUSB* /dev/ttyACM*
dmesg | grep -iE 'ttyUSB|ttyACM|ZM9011|option'
```

默认配置是：

```uci
option device 'auto'
```

启动时守护进程会依次打开 `/dev/ttyUSB0..7`、`/dev/ttyACM0..3`，发送 `AT` 并等待 `OK`，自动选中 AT 口。选中的设备会显示在网页顶部和「设置与调试」页，也会写入 `logread`。

如果自动探测失败，可以手动指定，例如：

```uci
option device '/dev/ttyUSB2'
```

修改后：

```sh
/etc/init.d/zm9011 restart
```

### 模组同时用于路由器上网

这是本方案最需要注意的地方。请检查拨号方式：

```sh
ps | grep -E 'pppd|uqmi|mbim|comgt|ModemManager'
uci show network
```

- 如果模组使用 **QMI / MBIM** 数据通道（常见 `wwan0`/`usb0`），AT 口通常是独立的 `ttyUSB`，可以正常共存。
- 如果拨号脚本（如 `comgt` + `pppd`）也在使用同一个 AT 口，两边会争抢串口，可能出现：
  - 上网偶发断开/重拨
  - 短信读取超时
  - AT 命令返回错乱

处理方式（按优先级）：

1. **换用独立的 AT 口**：多接口 ZTE 模组通常有专门的 AT 口，确认后写死 `option device`。
2. **不要停用拨号进程**：路由器需要上网时不要停 `pppd/uqmi`。
3. 如果确实只有一个 AT 口且被拨号脚本占用，需要把短信 AT 操作合并进拨号脚本的锁机制，或改用模组的第二个 AT 口；这属于定制开发。

**不要**把 `device` 指向 `wwan0`、`usb0`、`eth*` 等数据接口。

---

## 配置项（`/etc/config/zm9011`）

| 选项 | 默认 | 说明 |
|---|---|---|
| `device` | `auto` | AT 串口，`auto` 自动探测，或 `/dev/ttyUSB2` |
| `baud` | `115200` | AT 口波特率 |
| `listen` | `0.0.0.0` | HTTP 监听地址 |
| `port` | `8080` | HTTP 端口 |
| `password` | 空 | 非空时启用 HTTP Basic Auth（用户名任意） |
| `delete_after_read` | `1` | 读取并保存成功后删除模组内短信 |
| `data_dir` | `/etc/zm9011` | 短信 JSON 存储目录 |

修改后重启：

```sh
/etc/init.d/zm9011 restart
```

---

## 安全建议

- 仅内网使用；不要把 8080 端口映射到公网。
- 远程访问建议用 WireGuard / Tailscale / OpenVPN。
- 建议设置 `option password 'your-password'`；Basic Auth 走 HTTP 明文，公网必须配合 VPN 或 HTTPS 反向代理。
- 守护进程以 root 运行（需要访问串口）；仅在内网使用。
- 如需限制来源，可在防火墙只允许 LAN 网段访问 8080。

如果 LAN 默认策略无法访问 8080，可加一条防火墙规则：

```sh
uci add firewall rule
uci set firewall.@rule[-1].name='Allow-zm9011'
uci set firewall.@rule[-1].src='lan'
uci set firewall.@rule[-1].proto='tcp'
uci set firewall.@rule[-1].dest_port='8080'
uci set firewall.@rule[-1].target='ACCEPT'
uci commit firewall
/etc/init.d/firewall reload
```

---

## 故障排查

```sh
# 服务状态
/etc/init.d/zm9011 status
ps | grep zm9011d
netstat -lntp | grep 8080

# 日志（warn/error 会镜像到 stderr）
logread -f | grep -i zm9011d
```

网页「系统日志」页会显示：

- 串口打开/断开/自动重连
- 每一条 Tx/Rx AT 内容
- `+CMTI` 上报
- 短信解析/保存错误

常见问题：

| 现象 | 排查 |
|---|---|
| 网页打不开 | `ps`/`netstat`；防火墙；`listen` 是否为 0.0.0.0 |
| 显示未连接 | `ls /dev/ttyUSB*`；驱动是否安装；`device` 是否正确 |
| 自动探测失败 | 手动指定 AT 口；确认没有其它程序占用同一个 tty |
| 收到短信但解析失败 | 网页日志查看 `AT+CMGR` 原始响应；确认模组支持 PDU |
| 上网与短信互相影响 | AT 口与拨号进程冲突，参考上一节 |
| 短信没有实时上报 | 日志确认 `AT+CNMI=2,1,0,0,0` 是否返回 OK；`AT+CMGF=0` 是否成功 |

---

## 卸载 / 升级

升级（通用安装包）：重新解压并执行 `install.sh` 即可，`/etc/config/zm9011` 不会被覆盖。

卸载（通用安装包）：

```sh
/etc/init.d/zm9011 stop
/etc/init.d/zm9011 disable
rm -f /usr/bin/zm9011d /etc/init.d/zm9011
# 如需同时删除配置和短信：
rm -rf /etc/config/zm9011 /etc/zm9011
```

opkg：

```sh
opkg remove zm9011d
```

apk（SDK 编译的原生包）：

```sh
apk del zm9011d
```

升级前建议备份 `/etc/zm9011/messages.json`。

---

## 本地预览（不需要模组）

Windows 上可以直接用演示模式看界面：

```powershell
cd zm9011d
go build -o dist/zm9011d-demo.exe ./cmd/zm9011d
.\dist\zm9011d-demo.exe -listen 127.0.0.1 -port 18080 -demo -data-dir "$env:TEMP\zm9011d-demo"
```

然后打开 `http://127.0.0.1:18080/`。演示模式会写入两条示例短信，不访问串口。

手机端可用 `?sender=号码` 深链直接打开某个会话，例如：

```
http://192.168.1.1:8080/?sender=106939502038450
```

---

## HTTP API 摘要

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/state` | 串口连接、实时接收、SIM 卡槽、版本 |
| GET | `/api/messages` | 全部短信 |
| POST | `/api/messages/read` | `{"ids":["..."]}` 标记已读 |
| POST | `/api/messages/delete` | `{"ids":["..."]}` 删除 |
| POST | `/api/messages/clear` | 清空全部 |
| POST | `/api/sms/send` | `{"sender":"10086","text":"..."}` 发送短信 |
| GET | `/api/device` | ATI/版本/IMEI/IMSI/SIM/信号 |
| GET | `/api/network` | 注册/运营商/小区/信号 |
| GET | `/api/logs` | 最近日志 |
| GET | `/api/events` | SSE 日志与短信推送 |
| POST | `/api/at` | `{"command":"AT+CGMR"}` 原始 AT |
| GET | `/api/config` | 运行配置（不含密码） |

启用密码时使用 `curl -u admin:密码`。

---

## 后续可以继续做

- FOTA 升级包上传与 `+FOTAPUSH` 分包推送
- 锁频段/锁小区/数据拨号/SIM 卡管理页面
- 多会话搜索、短信导出
- 新短信 webhook / MQTT / 邮件通知
- LuCI 菜单集成
- 打成 `.ipk`
- HTTPS / 登录页
