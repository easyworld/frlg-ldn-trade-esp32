# ESP32-C3 USB LDN 固件

独立的 ESP32-C3 实验工程，参考 C6 的 LDN 协议实现，固定 ESP-IDF v6.1。
默认 4 MB Flash、DIO、80 MHz，通过 C3 原生 USB Serial/JTAG 与 Windows 上位机通信。
此版本不使用 UART0；开发板的 USB 接口必须连接 C3 的原生 USB，而不是 USB 转 UART 芯片。

## 与 C6 的区别

- `main/c3_transport.c` 使用 USB Serial/JTAG 驱动收发二进制帧。上位机设置波特率时仅应答，USB 物理速率不受该值影响。
- `main/ldn_control.c` 单独实现认证帧收发、状态查询和 USB 命令处理。
- 不包含 C6 的原始帧重定位补丁、硬件描述符偏移或发送跟踪钩子。
- WPA 回调与 CCMP 密钥接口对应固定的 C3 驱动，并在构建时检查 C3 无线库 SHA-256。
  该驱动的配对密钥读取接口返回不支持；校验组密钥，并以实际加密认证响应验证配对密钥。
- `LDN_HELLO` 重置协议会话编号，支持关闭串口后直接重新连接。
- FreeRTOS 使用 1 ms tick，使主循环的 2 ms 延时有效，避免忙循环。

配套上位机按对端声明的发送窗口起点初始化 Pia 接收序号，支持房间重连后的非固定起始序号。

## 构建与烧录

在仓库根目录运行，示例串口号替换为实际 C3 端口：

```powershell
.\firmware\esp32-c3\tools\probe.ps1 -Action identify -Port COM5
.\firmware\esp32-c3\tools\probe.ps1 -Action build
.\firmware\esp32-c3\tools\probe.ps1 -Action flash -Port COM5
```

脚本复用 `firmware/tools/environment.ps1` 激活 SDK，固件源码和构建目录均在本目录。
烧录前关闭占用该端口的上位机或串口监视器。现有 C# 上位机可直接选择原生 USB 的 COM 端口。

使用其他烧录工具时选择 ESP32-C3、DIO、80 MHz、4 MB，文件均位于 `build/`：

| 地址 | 文件 |
| --- | --- |
| `0x0` | `bootloader/bootloader.bin` |
| `0x8000` | `partition_table/partition-table.bin` |
| `0x10000` | `ldn_c3_bridge.bin` |

## 实板调试

需要 .NET 10 SDK。诊断程序使用现有 C# 协议库，结束时停止设备会话并释放串口。

```powershell
.\firmware\esp32-c3\tools\diagnose.ps1 -Mode serial -Port COM5
.\firmware\esp32-c3\tools\diagnose.ps1 -Mode auth -Port COM5
.\firmware\esp32-c3\tools\diagnose.ps1 -Mode pia -Port COM5
.\firmware\esp32-c3\tools\diagnose.ps1 -Mode room -Port COM5
```

- `serial`：协议握手、50 次信道切换、非法命令拒绝、恢复及停止。
- `auth`：额外扫描 FireRed Leader 房间，验证广播、关联、密钥安装、认证响应、challenge、成员列表及 UDP 接口配置。
- `pia`：额外进行短时 Pia 加密数据收发，验证双向包计数和解密结果，然后断开；不是完整交换测试。
- `room`：持续运行最多 3 分钟，检查实际进房，记录可靠通信序号和设备状态；可配合 Switch 操作测试交换。通信记录及收到的宝可梦保存在 `local/esp32-c3/runs/`。

`auth`、`pia` 和 `room` 需要 Switch 已创建 Leader 房间，并在当前用户 `.switch/prod.keys` 放置有效密钥。
密钥运行时读取，不嵌入源码或固件。原板备份、房间信息和调试日志应保存在仓库的 `local/esp32-c3/`。

已完成实板烧录校验、原生 USB 通信、连续重连、无线关联、LDN 认证、实际进房及一次完整宝可梦交换验证。
本次完整流程无 Pia 解密失败或串口坏帧；长时间稳定性仍需持续测试。
