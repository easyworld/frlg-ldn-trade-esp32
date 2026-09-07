#include "sdkconfig.h"
#include "ldn_wire.h"
#define printf ldn_wire_printf

#if CONFIG_LDN_PROBE_PRIVATE_RAW_TX
#include <string.h>
#include <inttypes.h>
#include <stdio.h>
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "ldn_private_raw.h"

#if !CONFIG_IDF_TARGET_ESP32C6
#error "Private raw TX is audited only for the pinned ESP32-C6 driver"
#endif

extern int ldn_stock_frame_check(int interface, const void *buffer,
                                 int length, bool system_sequence);
extern int ldn_vendor_80211_tx(int interface, const void *buffer, int length, bool system_sequence);
extern int ieee80211_post_hmac_tx(void *buffer);
static const void *s_authorized_frame;
static int s_authorized_length;
static TaskHandle_t s_authorized_task;
static portMUX_TYPE s_authorized_lock = portMUX_INITIALIZER_UNLOCKED;
static uint32_t s_descriptor_flags, s_descriptor_control, s_descriptor_count;
static uint16_t s_descriptor_header, s_descriptor_body;

int ldn_private_post_hmac_tx(void *buffer)
{
    /* Read-only offsets audited against C6 esf_buf_alloc, raw TX and ppTxPkt. */
    _Static_assert(sizeof(void *) == 4, "C6 driver descriptors contain 32-bit pointers");
    const uint8_t *descriptor;
    uint32_t flags, control;
    uint16_t header, body;
    memcpy(&descriptor, (const uint8_t *)buffer + 52, sizeof(descriptor));
    memcpy(&flags, descriptor, sizeof(flags));
    memcpy(&control, descriptor + 16, sizeof(control));
    memcpy(&header, (const uint8_t *)buffer + 20, sizeof(header));
    memcpy(&body, (const uint8_t *)buffer + 22, sizeof(body));
    portENTER_CRITICAL(&s_authorized_lock);
    s_descriptor_flags = flags;
    s_descriptor_control = control;
    s_descriptor_header = header;
    s_descriptor_body = body;
    ++s_descriptor_count;
    portEXIT_CRITICAL(&s_authorized_lock);
    return ieee80211_post_hmac_tx(buffer);
}

void ldn_private_raw_status(void)
{
    portENTER_CRITICAL(&s_authorized_lock);
    const uint32_t flags = s_descriptor_flags, control = s_descriptor_control;
    const uint32_t count = s_descriptor_count;
    const unsigned header = s_descriptor_header, body = s_descriptor_body;
    portEXIT_CRITICAL(&s_authorized_lock);
    printf("LDN_RAW_DESCRIPTOR count=%" PRIu32 " flags=%08" PRIx32 " control=%08" PRIx32
           " header=%u body=%u security_selector=%u\n",
           count, flags, control, header, body, (unsigned)((control >> 8) & 15));
}

int ldn_private_frame_check(int interface, const void *buffer, int length, bool system_sequence)
{
    portENTER_CRITICAL(&s_authorized_lock);
    const bool authorized = buffer != NULL && buffer == s_authorized_frame &&
        length == s_authorized_length && s_authorized_task == xTaskGetCurrentTaskHandle();
    portEXIT_CRITICAL(&s_authorized_lock);
    if (authorized && interface == WIFI_IF_STA &&
        system_sequence && length >= 48 && length <= 1500) {
        static uint8_t check_frame[1500];
        memcpy(check_frame, buffer, length);
        /* Validate the same frame through stock checks, allowing software CCMP. */
        check_frame[1] &= ~0x40;
        return ldn_stock_frame_check(interface, check_frame, length, true);
    }
    return ldn_stock_frame_check(interface, buffer, length, system_sequence);
}

esp_err_t ldn_private_raw_tx(const uint8_t *frame, size_t length, const uint8_t host[6])
{
    if (frame == NULL || host == NULL || length < 48 || length > 1500 ||
        frame[0] != 0x08 || frame[1] != 0x41 || (frame[22] & 15) != 0 ||
        frame[26] != 0 || frame[27] != 0x20 ||
        memcmp(frame + 4, host, 6) != 0 || memcmp(frame + 16, host, 6) != 0) {
        return ESP_ERR_INVALID_ARG;
    }
    uint8_t mac[6];
    wifi_ap_record_t ap;
    if (esp_wifi_get_mac(WIFI_IF_STA, mac) != ESP_OK ||
        esp_wifi_sta_get_ap_info(&ap) != ESP_OK || memcmp(ap.bssid, host, 6) != 0 ||
        memcmp(frame + 10, mac, 6) != 0) {
        return ESP_ERR_INVALID_STATE;
    }
    portENTER_CRITICAL(&s_authorized_lock);
    if (s_authorized_frame != NULL) {
        portEXIT_CRITICAL(&s_authorized_lock);
        return ESP_ERR_INVALID_STATE;
    }
    s_authorized_frame = frame;
    s_authorized_length = (int)length;
    s_authorized_task = xTaskGetCurrentTaskHandle();
    portEXIT_CRITICAL(&s_authorized_lock);
    const esp_err_t result = ldn_vendor_80211_tx(WIFI_IF_STA, frame, (int)length, true);
    portENTER_CRITICAL(&s_authorized_lock);
    s_authorized_frame = NULL;
    s_authorized_task = NULL;
    portEXIT_CRITICAL(&s_authorized_lock);
    return result;
}
#endif
