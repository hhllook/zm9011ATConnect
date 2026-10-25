# ZM9011 AT Toolkit

Windows 11 下为 **ZTE ZM9011**（Unisoc 平台，`USB\VID_19D2&PID_1710`）启用 AT 串口的完整工具集：

- **driver** — 禁用驱动签名强制后安装修改版 INF 的流程、安装与试探脚本
- **Zm9011** — 为AT指令软件
## 背景

ZM9011 的 MI_02 ~ MI_05 是四个 USB 描述符完全相同的厂商私有口（`Class_ff&SubClass_00&Prot_00`），原厂 `sprdvcom.inf`（UNISOC WHQL 包）不含本机 PID，Windows 全部枚举为 Error。本仓库把四个口映射为串口，装完后试探确认 AT 口。

实测（Windows 11，2026-10）：**MI_02 与 MI_05 为 AT 口**，`ATI` 返回完全相同（设备将 AT 通道同时映射到两个接口）；MI_03 / MI_04 无响应。

## 快速开始

1. BIOS 关闭 Secure Boot；开机进"启动设置"按 **7**（禁用驱动程序强制签名）
2. 管理员 PowerShell：
   ```powershell
   cd driver
   powershell -ExecutionPolicy Bypass -File .\install_driver.ps1
   powershell -ExecutionPolicy Bypass -File .\test_at.ps1      # 确认哪些口返回 OK
   ```
3.检测设备管理器是否读取到四个串口

4.解压打开软件,即可at调试指令



