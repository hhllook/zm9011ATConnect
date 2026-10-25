# ZM9011 AT Toolkit

Windows 11 下为 **ZTE ZM9011**（Unisoc 平台，`USB\VID_19D2&PID_1710`）启用 AT 串口的完整工具集：

- **driver** — 禁用驱动签名强制后安装修改版 INF 的流程、安装与试探脚本
- **Zm9011** — 为AT指令软件
## 背景

ZM9011 的 MI_02 ~ MI_05 是四个 USB 描述符完全相同的厂商私有口（`Class_ff&SubClass_00&Prot_00`），原厂 `sprdvcom.inf`（UNISOC WHQL 包）不含本机 PID，Windows 全部枚举为 Error。本仓库把四个口映射为串口，装完后试探确认 AT 口。

实测（Windows 11，2026-10）：**MI_02 与 MI_05 为 AT 口**，`ATI` 返回完全相同（设备将 AT 通道同时映射到两个接口）；MI_03 / MI_04 无响应。

## 快速开始

1. BIOS 关闭 Secure Boot；# ZTE ZM9011（Unisoc 平台）AT 口驱动修改包
2. 重启进入：设置 → 系统 → 恢复 → 高级启动 → 立即重新启动
   → 疑难解答 → 高级选项 → 启动设置 → 重新启动 → 按 **7**（禁用驱动程序强制签名）
3. 进入系统后，以**管理员**运行 PowerShell：
   ```
   cd <仓库根目录>\driver
   powershell -ExecutionPolicy Bypass -File .\install_driver.ps1
   ```
   脚本会打印 Secure Boot 状态、设备状态，并用 `pnputil` 安装、重新扫描。
   
4. 安装完成后重启回正常模式，驱动应继续可用（`sprdvcom.sys` 自身签名有效，
   缺失的只是 INF 的 WHQL 目录签名，该签名只在安装阶段被检查）。

5. 管理员 PowerShell：
   ```powershell
   cd <仓库根目录>\driver
   powershell -ExecutionPolicy Bypass -File .\test_at.ps1      # 确认哪些口返回 OK
   ```
6.解压打开软件,选择AT串口,即可at调试指令

<img width="794" height="510" alt="zm9011_general_restore" src="https://github.com/user-attachments/assets/a6daade4-23c0-4832-81c6-142aa26b5e6b" />


