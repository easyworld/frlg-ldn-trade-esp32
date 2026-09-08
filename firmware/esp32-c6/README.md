# ESP32-C6 UART LDN 固件

独立的 ESP32-C6 工程，固定 ESP-IDF v6.1，通过 UART 与 Windows 上位机通信。
源码位于 `main/`，C6 构建、烧录、原始帧适配和链接审计工具位于 `tools/`。
SDK 安装与环境激活共用 `../tools/setup.ps1`、`../tools/environment.ps1`。

## 构建与烧录

在仓库根目录运行：

```powershell
.\firmware\tools\setup.ps1
.\firmware\esp32-c6\tools\probe.ps1 -Action doctor
.\firmware\esp32-c6\tools\probe.ps1 -Action build
.\firmware\esp32-c6\tools\probe.ps1 -Action flash -Port COM6
```

将串口号替换为实际设备端口。默认配置为 C6、UART、16 MB Flash、`serial` 模式；
Flash 容量可通过 `-FlashSize 4MB`、`8MB` 或 `16MB` 指定。
烧录前关闭占用端口的上位机连接和串口监视器。

构建目录位于本工程内，按配置命名为 `build-c6-<模式>-<接口和容量>-<信道>/`。
默认配置的应用固件为 `build-c6-serial-uart16-1/ldn_wifi_probe.bin`；
完整烧录文件和地址见同目录的 `flash_args`。

## 状态指示灯

板载 WS2812 RGB 灯（GPIO8）以呼吸方式显示当前状态，颜色和快慢随状态变化：

| 颜色 | 状态 |
| --- | --- |
| 白 | 启动中 |
| 蓝（慢） | 待机，等待上位机下发房间配置 |
| 橙（快） | 正在加入房间 |
| 绿 | 房间已认证，可以交易 |
| 红（快） | 加入失败（关联超时或密钥校验未通过），约 3 秒后自动回到待机 |

灯的驱动用官方 `espressif/led_strip` 组件，首次编译会自动联网拉取。
可在 menuconfig 中关闭或修改 `LDN_PROBE_STATUS_LED` / `LDN_PROBE_STATUS_LED_GPIO`；
若灯初始化失败，固件会跳过指示灯继续正常工作。

## 通信与调试

完整交易桥接使用 `-Mode serial -Console uart`，通过板载 USB 转串口或外接
3.3V USB 串口适配器连接电脑。外接时交叉连接 TX/RX 并共地。
C6 的原生 USB Serial/JTAG 配置仅用于无线诊断。
`public`、`discovery`、`send` 模式不提供完整交易桥接。

此工程包含针对固定 C6 驱动的原始帧发送适配和硬件发送跟踪。
构建脚本检查 SDK 版本、驱动库哈希以及最终链接结果；这些实现不应直接用于其他芯片。
上位机使用方式见[项目说明](../../README.md)，协议见[串口通信协议](../../docs/SERIAL_PROTOCOL.md)。
