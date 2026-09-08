#include "sdkconfig.h"

#if CONFIG_LDN_PROBE_CONTROL_PORT
#include <inttypes.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "driver/uart.h"
#include "driver/uart_vfs.h"
#include "esp_private/wifi.h"
#include "esp_wifi.h"
#include "esp_timer.h"
#include "esp_attr.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "ldn_control.h"
#include "ldn_udp.h"
#include "ldn_session.h"
#include "ldn_wire.h"
#define printf ldn_wire_printf
#if CONFIG_LDN_PROBE_PRIVATE_RAW_TX
#include "ldn_private_raw.h"
#include "ldn_tx_trace.h"
#endif

#define FRAME_MAX 1514
#define MACSTR "%02x:%02x:%02x:%02x:%02x:%02x"
#define MACARGS(a) (a)[0], (a)[1], (a)[2], (a)[3], (a)[4], (a)[5]

typedef struct {
    uint16_t length;
    uint8_t bytes[FRAME_MAX];
} control_frame_t;

typedef struct {
    int64_t timestamp;
    uint16_t length;
    int8_t tx_status;
    ldn_hw_metadata_t hardware;
    uint8_t bytes[FRAME_MAX];
} capture_frame_t;

static esp_netif_t *s_netif;
static QueueHandle_t s_rx;
static QueueHandle_t s_air_rx_queue;
static uint8_t s_host[6], s_mac[6];
static atomic_bool s_connected;
static atomic_bool s_quiet;
static atomic_uint s_dropped;
static atomic_uint s_tx_ok, s_tx_failed, s_ethernet_rx, s_air_rx;
static atomic_uint s_tx_callbacks;
static uint8_t s_tx_header[32];
static uint16_t s_tx_length;
static portMUX_TYPE s_air_lock = portMUX_INITIALIZER_UNLOCKED;
static atomic_uint s_air_data, s_air_dropped, s_air_ack, s_air_mgmt;
static atomic_uint s_raw_ok, s_raw_failed, s_raw_length;
static int s_pending_baud;
static portMUX_TYPE s_capture_lock = portMUX_INITIALIZER_UNLOCKED;

static void IRAM_ATTR capture_sample(const uint8_t *bytes, size_t length, int tx_status,
                                    const ldn_hw_metadata_t *metadata)
{
    if (s_air_rx_queue == NULL || atomic_load(&s_quiet)) return;
    if (length > FRAME_MAX) {
        atomic_fetch_add(&s_air_dropped, 1);
        return;
    }
    /* The final hardware hook can also run in interrupt context. */
    static capture_frame_t sample;
    portENTER_CRITICAL_SAFE(&s_capture_lock);
    sample.timestamp = esp_timer_get_time();
    sample.length = length;
    sample.tx_status = tx_status;
    if (metadata) sample.hardware = *metadata;
    memcpy(sample.bytes, bytes, length);
    BaseType_t queued;
    if (xPortInIsrContext()) {
        queued = xQueueSendFromISR(s_air_rx_queue, &sample, NULL);
    } else {
        queued = xQueueSend(s_air_rx_queue, &sample, 0);
    }
    if (queued != pdTRUE) {
        atomic_fetch_add(&s_air_dropped, 1);
    }
    portEXIT_CRITICAL_SAFE(&s_capture_lock);
}

static void capture_frame(const uint8_t *bytes, size_t length, int tx_status)
{
    capture_sample(bytes, length, tx_status, NULL);
}

void IRAM_ATTR ldn_control_hardware(const uint8_t *frame, size_t length,
                                   const ldn_hw_metadata_t *metadata)
{
    capture_sample(frame, length, -2, metadata);
}

static void raw_tx_done(const esp_80211_tx_info_t *info)
{
    if (info == NULL || info->data == NULL || info->des_addr == NULL ||
        info->src_addr == NULL || info->ifidx != WIFI_IF_STA ||
        memcmp(info->des_addr, s_host, 6) != 0 || memcmp(info->src_addr, s_mac, 6) != 0) return;
    atomic_fetch_add(info->tx_status == WIFI_SEND_SUCCESS ? &s_raw_ok : &s_raw_failed, 1);
    const unsigned length = atomic_exchange(&s_raw_length, 0);
    if (length >= 48 && length <= 1500 &&
        info->data_len == (uint8_t)(length - 24)) {
        /* This callback's uint8_t data_len truncates long bodies in the pinned SDK. */
        capture_frame(info->data, length, info->tx_status);
    }
}

void ldn_control_sniff(const unsigned char *frame, size_t length)
{
    if (length < 14 || s_air_rx_queue == NULL) return;
    if (frame[0] == 0xd4 && memcmp(frame + 4, s_mac, 6) == 0) {
        atomic_fetch_add(&s_air_ack, 1);
        capture_frame(frame, length - 4, -1);
        return;
    }
    if (length < 28 ||
        memcmp(frame + 10, s_host, 6) != 0) return;
    if ((frame[0] == 0x10 || frame[0] == 0x30 || frame[0] == 0xa0 ||
         frame[0] == 0xb0 || frame[0] == 0xc0) && memcmp(frame + 4, s_mac, 6) == 0) {
        atomic_fetch_add(&s_air_mgmt, 1);
        capture_frame(frame, length - 4, -1);
        return;
    }
    if ((frame[0] & 0x0c) != 0x08) return;
    atomic_fetch_add(&s_air_rx, 1);
    /* Null-data keepalives must not displace authentication or other data. */
    if ((frame[0] != 0x08 && frame[0] != 0x88) ||
        memcmp(frame + 4, s_mac, 6) != 0) return;
    atomic_fetch_add(&s_air_data, 1);
    capture_frame(frame, length - 4, -1);
}

static void tx_done(uint8_t ifidx, uint8_t *data, uint16_t *length, bool success)
{
    atomic_fetch_add(&s_tx_callbacks, 1);
    if (ifidx == WIFI_IF_STA && data && length) {
        atomic_fetch_add(success ? &s_tx_ok : &s_tx_failed, 1);
        if (*length > 900) {
            portENTER_CRITICAL(&s_air_lock);
            s_tx_length = *length;
            memcpy(s_tx_header, data, sizeof(s_tx_header));
            portEXIT_CRITICAL(&s_air_lock);
        }
    }
}

static esp_err_t control_rx(void *buffer, uint16_t length, void *eb)
{
    const uint8_t *bytes = buffer;
    atomic_fetch_add(&s_ethernet_rx, 1);
    if (length >= 14 && bytes[12] == 0x88 && bytes[13] == 0xb7) {
        control_frame_t frame;
        if (length <= FRAME_MAX && memcmp(bytes + 6, s_host, 6) == 0) {
            frame.length = length;
            memcpy(frame.bytes, bytes, length);
            if (xQueueSend(s_rx, &frame, 0) != pdTRUE) {
                atomic_fetch_add(&s_dropped, 1);
            }
        } else {
            atomic_fetch_add(&s_dropped, 1);
        }
        esp_wifi_internal_free_rx_buffer(eb);
        return ESP_OK;
    }
    return esp_netif_receive(s_netif, buffer, length, eb);
}

void ldn_control_init(esp_netif_t *netif, const unsigned char host[6])
{
    s_netif = netif;
    memcpy(s_host, host, 6);
    ldn_udp_init(netif, host);
    ESP_ERROR_CHECK(esp_wifi_get_mac(WIFI_IF_STA, s_mac));
    s_rx = xQueueCreate(4, sizeof(control_frame_t));
    ESP_ERROR_CHECK(s_rx == NULL ? ESP_ERR_NO_MEM : ESP_OK);
    s_air_rx_queue = xQueueCreate(8, sizeof(capture_frame_t));
    ESP_ERROR_CHECK(s_air_rx_queue == NULL ? ESP_ERR_NO_MEM : ESP_OK);
#if CONFIG_LDN_PROBE_PRIVATE_RAW_TX
    ldn_tx_trace_init(s_host, s_mac);
#endif
    ESP_ERROR_CHECK(uart_driver_install(CONFIG_ESP_CONSOLE_UART_NUM, 32768, 4096, 0, NULL, 0));
    uart_vfs_dev_use_driver(CONFIG_ESP_CONSOLE_UART_NUM);
    ESP_ERROR_CHECK(esp_wifi_set_tx_done_cb(tx_done));
    ESP_ERROR_CHECK(esp_wifi_register_80211_tx_cb(raw_tx_done));
#if CONFIG_LDN_PROBE_FRESH_MAC
    printf("LDN_IDENTITY fresh " MACSTR "\n", MACARGS(s_mac));
#else
    printf("LDN_IDENTITY factory " MACSTR "\n", MACARGS(s_mac));
#endif
    printf("LDN_SESSION %s " MACSTR "\n", ldn_session_ssid(), MACARGS(s_host));
    fflush(stdout);
}

void ldn_control_link(bool connected)
{
    if (connected) {
        /* The default netif connected handler has installed its RX callback. */
        ESP_ERROR_CHECK(esp_wifi_internal_reg_rxcb(WIFI_IF_STA, control_rx));
    }
    atomic_store(&s_connected, connected);
}

static int nibble(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static void command(const char *line)
{
    if (!strcmp(line, "LDN_BINARY") || !strcmp(line, "LDN_HELLO")) { ldn_wire_enable(); return; }
    if (!strncmp(line, "LDN_BEGIN ", 10)) {
        char *end; unsigned long id = strtoul(line + 10, &end, 16);
        if (*end || !id || strlen(line + 10) != 8) { printf("LDN_ERROR INVALID_SESSION\n"); return; }
        ldn_session_stop(); ldn_wire_session((uint32_t)id);
        printf("LDN_BEGUN\n"); return;
    }
    if (!strncmp(line, "LDN_SCAN ", 9)) {
        unsigned channel; char extra;
        int result = sscanf(line + 9, "%u %c", &channel, &extra) == 1 ? ldn_session_scan(channel) : ESP_ERR_INVALID_ARG;
        printf("LDN_SCAN_RESULT %d\n", result); return;
    }
    if (!strncmp(line, "LDN_CONFIG ", 11)) {
        unsigned channel; char ssid[33], bssid[18], key[33], extra;
        int result = ESP_ERR_INVALID_ARG;
        if (sscanf(line + 11, "%u %32s %17s %32s %c", &channel, ssid, bssid, key, &extra) == 4)
            result = ldn_session_configure(ssid, bssid, key, channel);
        printf("LDN_CONFIG_RESULT %d\n", result); return;
    }
    if (!strcmp(line, "LDN_STOP")) { ldn_session_stop(); printf("LDN_STOPPED\n"); return; }
    if (strcmp(line, "LDN_QUIET") == 0) {
        atomic_store(&s_quiet, true);
        xQueueReset(s_air_rx_queue);
        printf("LDN_QUIET_OK\n");
        return;
    }
    if (strcmp(line, "LDN_BAUD 921600") == 0 || strcmp(line, "LDN_BAUD 115200") == 0) {
        const int baud = strcmp(line + 9, "921600") == 0 ? 921600 : 115200;
        printf("LDN_BAUD_READY %d\n", baud);
        if (ldn_wire_active()) { s_pending_baud = baud; return; }
        fflush(stdout);
        uart_wait_tx_done(CONFIG_ESP_CONSOLE_UART_NUM, pdMS_TO_TICKS(1000));
        uart_set_baudrate(CONFIG_ESP_CONSOLE_UART_NUM, baud);
        return;
    }
    if (ldn_udp_command(line, atomic_load(&s_connected))) return;
    if (strcmp(line, "LDN_STATUS") == 0) {
        printf("LDN_SESSION %s " MACSTR "\n", ldn_session_ssid(), MACARGS(s_host));
        printf("LDN_LINK %u " MACSTR "\n", atomic_load(&s_connected), MACARGS(s_mac));
        printf("LDN_DROPPED %u\n", atomic_load(&s_dropped));
        printf("LDN_STATS tx_ok=%u tx_failed=%u ethernet_rx=%u air_rx=%u tx_callbacks=%u\n",
               atomic_load(&s_tx_ok), atomic_load(&s_tx_failed),
               atomic_load(&s_ethernet_rx), atomic_load(&s_air_rx), atomic_load(&s_tx_callbacks));
        printf("LDN_RAW_STATS ok=%u failed=%u\n", atomic_load(&s_raw_ok), atomic_load(&s_raw_failed));
        printf("LDN_AIR_STATS data=%u dropped=%u ack=%u mgmt=%u\n",
               atomic_load(&s_air_data), atomic_load(&s_air_dropped),
               atomic_load(&s_air_ack), atomic_load(&s_air_mgmt));
#if CONFIG_LDN_PROBE_PRIVATE_RAW_TX
        ldn_private_raw_status();
        ldn_tx_trace_status();
#endif
        static char air_hex[3201];
        static const char digits[] = "0123456789abcdef";
        uint8_t tx_header[32];
        portENTER_CRITICAL(&s_air_lock);
        const unsigned tx_length = s_tx_length;
        memcpy(tx_header, s_tx_header, sizeof(tx_header));
        portEXIT_CRITICAL(&s_air_lock);
        for (size_t i = 0; i < sizeof(tx_header); ++i) {
            air_hex[2 * i] = digits[tx_header[i] >> 4];
            air_hex[2 * i + 1] = digits[tx_header[i] & 15];
        }
        air_hex[64] = 0;
        if (tx_length) printf("LDN_TX_HEADER %u %s\n", tx_length, air_hex);
        return;
    }
    const bool private_raw = strncmp(line, "LDN_PRIVATE_TX ", 15) == 0;
    const bool raw = private_raw || strncmp(line, "LDN_RAW_TX ", 11) == 0;
    if (!raw && strncmp(line, "LDN_TX ", 7) != 0) { printf("LDN_ERROR UNKNOWN_COMMAND\n"); return; }
    static uint8_t frame[FRAME_MAX];
    const char *hex = line + (private_raw ? 15 : raw ? 11 : 7);
    const size_t offset = raw ? 0 : 14;
    const size_t n = strlen(hex);
    int result = ESP_ERR_INVALID_ARG;
    if (n < 10 || n > 3000 || n % 2) goto done;
    for (size_t i = 0; i < n / 2; ++i) {
        const int hi = nibble(hex[2 * i]), lo = nibble(hex[2 * i + 1]);
        if (hi < 0 || lo < 0) goto done;
        frame[offset + i] = (uint8_t)((hi << 4) | lo);
    }
    if (raw) {
        if (n < 96 || frame[0] != 0x08 || frame[1] != 0x41 ||
            memcmp(frame + 4, s_host, 6) != 0 || memcmp(frame + 10, s_mac, 6) != 0 ||
            memcmp(frame + 16, s_host, 6) != 0) goto done;
        result = ESP_ERR_INVALID_STATE;
        if (!atomic_load(&s_connected)) goto done;
        unsigned expected = 0;
        if (!atomic_compare_exchange_strong(&s_raw_length, &expected, n / 2)) goto done;
        if (private_raw) {
#if CONFIG_LDN_PROBE_PRIVATE_RAW_TX
            result = ldn_private_raw_tx(frame, n / 2, s_host);
#else
            result = ESP_ERR_NOT_SUPPORTED;
#endif
        } else {
            result = esp_wifi_80211_tx(WIFI_IF_STA, frame, n / 2, true);
        }
        if (result != ESP_OK) atomic_store(&s_raw_length, 0);
        goto done;
    }
    static const uint8_t prefix[] = {0x00, 0x22, 0xaa, 0x01, 0x02};
    if (memcmp(frame + 14, prefix, sizeof(prefix)) != 0) goto done;
    result = ESP_ERR_INVALID_STATE;
    if (!atomic_load(&s_connected)) goto done;
    memcpy(frame, s_host, 6);
    memcpy(frame + 6, s_mac, 6);
    frame[12] = 0x88;
    frame[13] = 0xb7;
    result = esp_wifi_internal_tx(WIFI_IF_STA, frame, 14 + n / 2);
done:
    printf("LDN_TX_RESULT %d\n", result);
}

void ldn_control_poll(void)
{
    static bool previous;
    const bool connected = atomic_load(&s_connected);
    if (previous != connected) {
        printf("LDN_LINK %u " MACSTR "\n", connected, MACARGS(s_mac));
        previous = connected;
    }
    static control_frame_t frame;
    static char hex[FRAME_MAX * 2 + 1];
    static const char digits[] = "0123456789abcdef";
    while (xQueueReceive(s_rx, &frame, 0) == pdTRUE) {
        const size_t n = frame.length - 14;
        for (size_t i = 0; i < n; ++i) {
            hex[2 * i] = digits[frame.bytes[14 + i] >> 4];
            hex[2 * i + 1] = digits[frame.bytes[14 + i] & 15];
        }
        hex[2 * n] = 0;
        printf("LDN_RX " MACSTR " %s\n", MACARGS(frame.bytes + 6), hex);
    }
    static capture_frame_t sample;
    for (int i = 0; i < 8 && xQueueReceive(s_air_rx_queue, &sample, 0) == pdTRUE; ++i) {
        for (size_t j = 0; j < sample.length; ++j) {
            hex[2 * j] = digits[sample.bytes[j] >> 4];
            hex[2 * j + 1] = digits[sample.bytes[j] & 15];
        }
        hex[2 * sample.length] = 0;
        if (sample.tx_status == -2) {
            const ldn_hw_metadata_t *m = &sample.hardware;
            printf("LDN_HW_FRAME %" PRIi64 " %08" PRIx32 " %08" PRIx32
                   " %08" PRIx32 " %08" PRIx32 " %u %u %u %u %s\n",
                   sample.timestamp, m->flags, m->control, m->prefix[0], m->prefix[1],
                   m->header, m->body, m->dma_length, m->rate, hex);
        } else if (sample.tx_status < 0) {
            printf("LDN_FRAME %" PRIi64 " %s\n", sample.timestamp, hex);
        } else {
            printf("LDN_TX_FRAME %" PRIi64 " %d %s\n", sample.timestamp, sample.tx_status, hex);
        }
    }
    static char line[3016];
    static size_t used;
    static bool overflow;
    static uint8_t bytes[8192];
    int n = uart_read_bytes(CONFIG_ESP_CONSOLE_UART_NUM, bytes, sizeof(bytes), 0);
    for (int i = 0; i < n; ++i) {
        if (ldn_wire_active()) {
            ldn_wire_feed(bytes[i], command);
            if (s_pending_baud) {
                uart_wait_tx_done(CONFIG_ESP_CONSOLE_UART_NUM, pdMS_TO_TICKS(1000));
                uart_set_baudrate(CONFIG_ESP_CONSOLE_UART_NUM, s_pending_baud); s_pending_baud = 0;
            }
            continue;
        }
        if (bytes[i] == '\r') continue;
        if (bytes[i] == '\n') {
            line[used] = 0;
            if (!overflow) command(line);
            else printf("LDN_TX_RESULT %d\n", ESP_ERR_INVALID_SIZE);
            used = 0;
            overflow = false;
        } else if (used < sizeof(line) - 1) {
            line[used++] = (char)bytes[i];
        } else {
            overflow = true;
        }
    }
    ldn_udp_poll(connected);
    fflush(stdout);
}

void ldn_control_target(const unsigned char host[6])
{
    memcpy(s_host, host, 6);
    esp_wifi_get_mac(WIFI_IF_STA, s_mac);
    ldn_udp_init(s_netif, host);
    xQueueReset(s_rx); xQueueReset(s_air_rx_queue);
    atomic_store(&s_raw_length, 0);
    atomic_store(&s_quiet, true);
    atomic_store(&s_connected, false);
#if CONFIG_LDN_PROBE_PRIVATE_RAW_TX
    ldn_tx_trace_init(s_host, s_mac);
#endif
}
#endif
