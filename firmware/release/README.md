# 完整烧录固件

本目录提供包含启动引导程序、分区表和应用程序的完整固件，可用于首次烧录。所有完整 BIN 文件均烧录到 `0x0`。

| 文件 | 芯片 | Flash | 上位机通信接口 |
| --- | --- | --- | --- |
| `frlg-trade-esp32c6-uart-16mb-full.bin` | ESP32-C6 | 16 MB | UART（USB 转串口） |
| `frlg-trade-esp32c3-usb-4mb-full.bin` | ESP32-C3 | 4 MB | 原生 USB Serial/JTAG |
| `frlg-trade-esp32s3-uart-16mb-full.bin` | ESP32-S3 | 16 MB | UART0（USB 转串口） |

三款均为 DIO / 80 MHz。请按芯片、容量和接口选择，不可混用。

## ESP32-C6

| 项目 | 配置 |
| --- | --- |
| 芯片 | ESP32-C6 |
| Flash 容量 | 16 MB |
| Flash 模式 / 频率 | DIO / 80 MHz |
| 通信 | UART 串口，serial 完整交易桥接模式 |
| 完整文件烧录地址 | `0x0` |
| 构建日期 | 2026-09-08 |
| 源码版本 | `7f992ba` |

此文件用于上述 C6 配置，不适用于 C3 或其他 Flash 容量的配置。使用板载 USB 转串口接口连接电脑；原生 USB Serial/JTAG 不能代替本固件的 UART 通信接口。

### 烧录

图形烧录工具中选择 ESP32-C6，只添加这个完整 BIN 文件，地址填写 `0x0`，配置选择 DIO、80 MHz、16 MB。

也可以安装 esptool 后，在本目录运行以下命令（将 COM6 替换为实际端口）：

```powershell
python -m esptool --chip esp32c6 --port COM6 --baud 460800 write-flash --flash-mode dio --flash-freq 80m --flash-size 16MB 0x0 frlg-trade-esp32c6-uart-16mb-full.bin
```

烧录前关闭占用串口的上位机和串口监视器。烧录后运行电脑端程序；交换期间电脑程序需要持续运行。运行与密钥配置见[项目说明](../../README.md)。

`SHA256SUMS.txt` 提供文件校验值，可用 `Get-FileHash .\frlg-trade-esp32c6-uart-16mb-full.bin -Algorithm SHA256` 核对。

### 重新生成

在仓库根目录、已配置的 ESP-IDF 环境中执行：

```powershell
.\firmware\esp32-c6\tools\probe.ps1 -Action build
python -m esptool --chip esp32c6 merge-bin --output firmware/release/frlg-trade-esp32c6-uart-16mb-full.bin --flash-mode dio --flash-freq 80m --flash-size 16MB 0x0 firmware/esp32-c6/build-c6-serial-uart16-1/bootloader/bootloader.bin 0x8000 firmware/esp32-c6/build-c6-serial-uart16-1/partition_table/partition-table.bin 0x10000 firmware/esp32-c6/build-c6-serial-uart16-1/ldn_wifi_probe.bin
```

重新发布时同步更新构建日期和源码版本。合并文件仅填充到应用末尾，无需扩充到 16 MB；16 MB 指目标 Flash 配置。

本次已通过构建、私有无线链接审计和合并数据校验，未执行设备烧录或新增实板交易验证。

## ESP32-C3

`frlg-trade-esp32c3-usb-4mb-full.bin` 使用 4 MB Flash、DIO、80 MHz，完整文件烧录地址为 `0x0`。构建日期为 2026-09-08，源码版本为 `7f992ba`。

上位机通信必须使用连接到 C3 原生 USB Serial/JTAG 的 USB 接口，不能使用 USB 转 UART 接口。详情见 [C3 工程说明](../esp32-c3/README.md)。

### 烧录

图形烧录工具中选择 ESP32-C3，添加 C3 完整 BIN 文件，地址填写 `0x0`，配置选择 DIO、80 MHz、4 MB。

也可在本目录执行（将 COM5 替换为实际端口）：

```powershell
python -m esptool --chip esp32c3 --port COM5 --baud 460800 write-flash --flash-mode dio --flash-freq 80m --flash-size 4MB 0x0 frlg-trade-esp32c3-usb-4mb-full.bin
```

### 重新生成

在仓库根目录、已配置的 ESP-IDF 环境中执行：

```powershell
.\firmware\esp32-c3\tools\probe.ps1 -Action build
python -m esptool --chip esp32c3 merge-bin --output firmware/release/frlg-trade-esp32c3-usb-4mb-full.bin --flash-mode dio --flash-freq 80m --flash-size 4MB 0x0 firmware/esp32-c3/build/bootloader/bootloader.bin 0x8000 firmware/esp32-c3/build/partition_table/partition-table.bin 0x10000 firmware/esp32-c3/build/ldn_c3_bridge.bin
```

本次发布执行构建与合并数据校验，未执行设备烧录或新增实板交易验证。已有实板验证记录见 C3 工程说明。

## ESP32-S3

`frlg-trade-esp32s3-uart-16mb-full.bin` 使用 16 MB Flash、DIO、80 MHz，通过 UART0（板载 USB 转串口，如 CH340）与上位机通信。完整文件烧录地址为 `0x0`，包含启动程序、分区表和应用。

- 构建日期：2026-09-09。
- 源码版本：`4936e34`（已合并 PR #2，并保留现有 SDK 路径、修复日志帧缓冲区并发与长日志越界问题）。
- SDK：ESP-IDF v6.1，提交 `fff9895c82d744c7237be8847347bdd1b07c6643`。
- 默认 SDK 路径：`%USERPROFILE%\esp\esp-idf-v6.1`，使用 SDK 自带 `export.ps1` 激活，无需 EIM。
- 通信：初始 115200，连接后通过 `LDN_BAUD` 切换到 921600。使用 USB 转 UART0 接口。

### 烧录

图形烧录工具选择 ESP32-S3，只添加这个完整 BIN 文件，地址填写 `0x0`，配置选择 DIO、80 MHz、16 MB。

也可在本目录执行（将 COM5 替换为实际端口）：

```powershell
python -m esptool --chip esp32s3 --port COM5 --baud 460800 write-flash --flash-mode dio --flash-freq 80m --flash-size 16MB 0x0 frlg-trade-esp32s3-uart-16mb-full.bin
```

### 重新生成

在仓库根目录执行：

```powershell
.\firmware\esp32-s3\tools\probe.ps1 -Action build
. .\firmware\tools\environment.ps1
& $ProbePython -m esptool --chip esp32s3 merge-bin --output firmware/release/frlg-trade-esp32s3-uart-16mb-full.bin --flash-mode dio --flash-freq 80m --flash-size 16MB 0x0 firmware/esp32-s3/build/bootloader/bootloader.bin 0x8000 firmware/esp32-s3/build/partition_table/partition-table.bin 0x10000 firmware/esp32-s3/build/ldn_s3_bridge.bin
```

本次通过完整构建、SDK 提交与 S3 无线驱动库 SHA256 校验、镜像校验和验证，以及完整 BIN 三个组成部分和填充区的逐字节核对。上位机离线测试通过 9,115 项检查。本次未执行实板烧录或交易验证；PR 作者的实板记录见 [S3 工程说明](../esp32-s3/README.md)。重新发布时同步更新源码版本、构建日期和全部校验值。

## 更新全部校验值

在仓库根目录执行，保留三款固件的校验记录：

```powershell
Get-ChildItem firmware/release -Filter '*.bin' | Sort-Object Name | ForEach-Object {
    $firmwareHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$firmwareHash  $($_.Name)"
} | Set-Content firmware/release/SHA256SUMS.txt -Encoding ascii
```
