# ESP32 UART LDN 固件

这是经典 ESP32（Xtensa ESP32，不是 C3/C6/S3）的独立 LDN 桥接工程，固定使用
ESP-IDF v6.1，通过 UART0 与 Windows 上位机通信。常见开发板的 USB 转串口接口在电脑上
会显示为 COM 端口；本次连接使用 COM9。

> **硬件要求：** 使用 **ESP32-WROOM-32E**。旧款 **ESP32-WROOM-32** 存在已知实板兼容性问题，
> 无法可靠完成交换，不受此固件支持。两者名称相近，请以模组屏蔽罩上的完整型号为准。

可直接下载 [经典 ESP32 完整固件](../release/frlg-trade-esp32-uart-4mb-full.bin)，以 DIO / 80 MHz / 4 MB
配置从 `0x0` 烧录；校验值和详细命令见[发布说明](../release/README.md#经典-esp32)。

## 构建、识别与烧录

在仓库根目录执行：

```powershell
.\firmware\esp32\tools\probe.ps1 -Action identify -Port COM9
.\firmware\esp32\tools\probe.ps1 -Action build
.\firmware\esp32\tools\probe.ps1 -Action flash -Port COM9
```

烧录前关闭上位机和其他串口监视器。默认配置为经典 ESP32、UART0、4 MB Flash、DIO、
80 MHz；外接 USB-UART 时交叉连接 TX/RX 并共地。上电后设备先使用 115200 波特率，
上位机会在握手期间切换到 921600。

完整实现复用 C3/S3 的 COBS/CRC 串口协议、LDN 扫描、关联、CCMP 密钥安装、认证帧和
UDP Pia/RFU 桥接。经典 ESP32 的驱动需要先调用 `esp_wifi_auth_done_internal()` 再写入
CCMP 密钥，固件会在密钥安装完成后才报告 `LDN_LINK 1`，避免主机提前发送认证帧。
密钥只在本轮连接的 RAM 中使用，不写入 Flash。

构建需要固定的 ESP-IDF 提交 `fff9895c82d744c7237be8847347bdd1b07c6643`；缺少 SDK
或经典 ESP32 工具链时运行 `.\firmware\tools\setup.ps1`，然后重新执行上述命令。
