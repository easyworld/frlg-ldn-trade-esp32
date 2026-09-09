# ESP32-S3 USB LDN 固件

以 [C3 工程](../esp32-c3/README.md) 为蓝本的独立 ESP32-S3 实验固件，固定 ESP-IDF v6.1，
通过 UART0（板载 USB 转串口，如 CH340）与 Windows 上位机通信，波特率由 `LDN_BAUD` 真实切换。
C6 的扩展功能（WS2812 状态灯、软件 CCMP 原始帧发送、发送跟踪）未包含，仅保留核心交易桥接。

## 构建与烧录

在仓库根目录运行：

```powershell
.\firmware\esp32-s3\tools\probe.ps1 -Action build
.\firmware\esp32-s3\tools\probe.ps1 -Action flash -Port COM5
```

默认配置为 S3、UART0 控制台（115200，连接时切换 921600）、16 MB Flash。串口号替换为实际设备端口，
烧录前关闭占用端口的上位机连接和串口监视器。构建目录位于本工程 `build/`。

## 与 C3 工程的差异

- 目标芯片与驱动库哈希锁定：`main/CMakeLists.txt` 校验 S3 的 `libnet80211.a`。
- 传输层源文件命名为 `s3_transport.c`，就绪与统计行输出 `LDN_S3_READY`、`LDN_S3_STATS`。
- 其余协议实现（WPA 钩子、密钥安装、LDN 以太网帧收发、COBS 串口协议）与 C3 相同。

## 状态

S3 使用与 C3 相同的 IDF v6.1 私有 WPA 回调表与密钥安装接口（`ldn_private_wifi.h`
在两个工程间逐字节相同）。已在实板（16 MB Flash、UART0 桥接）完成真实入房与一笔完整交易。

与 C3 的关键行为差异（`ldn_probe.c` 内有注释）：

- S3 驱动在 `esp_wifi_auth_done_internal()` 报告握手完成之后才收尾站点上下文
  （STA_CONNECTED 事件、CAM 条目分配），因此必须**先授权、后注密**，与 C3/C6 顺序相反。
- S3 的 CAM 密钥存储带混淆，`esp_wifi_get_sta_key_internal` 回读返回的是干扰字节，
  不能作为注入失败判据；认证加密流量的往返才是真实校验。
- 驱动日志通过 `ldn_wire_raw` 以事件帧输出（`esp_log_set_vprintf` 钩子），
  便于用 `--join` 诊断模式观察入网过程，且不破坏 COBS 帧结构。
