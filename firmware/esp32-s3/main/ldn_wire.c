#include "sdkconfig.h"
#if CONFIG_LDN_PROBE_CONTROL_PORT
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "s3_transport.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "ldn_wire.h"

#define MAX_FRAME 4096
static bool active;
static SemaphoreHandle_t tx_lock;
static uint32_t session, request;
static uint8_t input[MAX_FRAME + 32];
static size_t used;
static bool overflow;

static uint32_t crc32(const uint8_t *p, size_t n)
{
    uint32_t crc = UINT32_MAX;
    while (n--) {
        crc ^= *p++;
        for (int i = 0; i < 8; ++i) crc = (crc >> 1) ^ (0xedb88320U & (0U - (crc & 1)));
    }
    return ~crc;
}
static uint32_t get32(const uint8_t *p)
{ return p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }
static void put32(uint8_t *p, uint32_t n)
{ for (int i = 0; i < 4; ++i) p[i] = (uint8_t)(n >> (8 * i)); }

static void send_frame(uint8_t kind, const uint8_t *payload, size_t length)
{
    static uint8_t frame[MAX_FRAME], encoded[MAX_FRAME + 32];
    if (length > MAX_FRAME - 16) return;
    xSemaphoreTake(tx_lock, portMAX_DELAY);
    frame[0] = 1; frame[1] = kind;
    put32(frame + 2, request); put32(frame + 6, session);
    frame[10] = length & 255; frame[11] = length >> 8;
    memcpy(frame + 12, payload, length);
    put32(frame + 12 + length, crc32(frame, 12 + length));
    size_t out = 1, code_at = 0;
    uint8_t code = 1;
    for (size_t i = 0; i < length + 16; ++i) {
        if (!frame[i]) { encoded[code_at] = code; code_at = out++; code = 1; }
        else {
            encoded[out++] = frame[i];
            if (++code == 255) { encoded[code_at] = code; code_at = out++; code = 1; }
        }
    }
    encoded[code_at] = code; encoded[out++] = 0;
    s3_transport_write(encoded, out);
    xSemaphoreGive(tx_lock);
}

bool ldn_wire_active(void) { return active; }
void ldn_wire_session(uint32_t value) { session = value; }

void ldn_wire_raw(const char *text)
{
    /* Driver logs ride the event channel as frames so they cannot corrupt COBS framing. */
    if (!active) { printf("%s\n", text); return; }
    send_frame(3, (const uint8_t *)text, strlen(text));
}

/* Keep driver logs observable over the wire: each line ships as one event frame. */
static int wire_log_vprintf(const char *format, va_list args)
{
    char line[512];
    if (!active) return vprintf(format, args);
    int n = vsnprintf(line, sizeof(line), format, args);
    if (n < 0) return n;
    if (n >= sizeof(line)) n = sizeof(line) - 1;
    while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = 0;
    if (n > 0 && strncmp(line, "LDN_", 4) != 0) send_frame(3, (const uint8_t *)line, n);
    return n;
}

void ldn_wire_enable(void)
{
    if (tx_lock == NULL) {
        tx_lock = xSemaphoreCreateMutex();
        configASSERT(tx_lock != NULL);
    }
    active = true;
    /* A newly opened host starts at session zero and negotiates a fresh BEGIN. */
    session = 0;
    esp_log_set_vprintf(wire_log_vprintf);
    const uint8_t zero = 0;
    s3_transport_write(&zero, 1);
    ldn_wire_printf("LDN_HELLO 1 %s dynamic-session,scan,auth,udp 1472\n", CONFIG_IDF_TARGET);
}

int ldn_wire_printf(const char *format, ...)
{
    va_list ap; va_start(ap, format);
    if (!active) { int n = vprintf(format, ap); va_end(ap); return n; }
    static char line[MAX_FRAME - 16];
    int n = vsnprintf(line, sizeof(line), format, ap);
    va_end(ap);
    if (n < 0 || n >= sizeof(line)) return -1;
    while (n && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = 0;
    if (!strncmp(line, "LDN_DATAGRAM ", 13)) {
        unsigned a, b, c, d; int offset = 0;
        if (sscanf(line + 13, "%u.%u.%u.%u %n", &a, &b, &c, &d, &offset) != 4) return -1;
        static uint8_t data[1476];
        data[0] = a; data[1] = b; data[2] = c; data[3] = d;
        const char *hex = line + 13 + offset;
        size_t count = strlen(hex) / 2;
        if (count > 1472) return -1;
        for (size_t i = 0; i < count; ++i) {
            char value[3] = {hex[2 * i], hex[2 * i + 1], 0};
            data[4 + i] = strtoul(value, NULL, 16);
        }
        send_frame(5, data, count + 4);
    } else send_frame(request ? 2 : 3, (uint8_t *)line, n);
    return n;
}

void ldn_wire_feed(uint8_t value, void (*dispatch)(const char *))
{
    if (value) {
        if (used < sizeof(input)) input[used++] = value; else overflow = true;
        return;
    }
    if (!used || overflow) { used = 0; overflow = false; return; }
    static uint8_t frame[MAX_FRAME];
    size_t read = 0, out = 0;
    while (read < used) {
        uint8_t code = input[read++];
        size_t needed = code - 1 + (code != 255 && read + code - 1 < used ? 1 : 0);
        if (!code || read + code - 1 > used || out + needed > sizeof(frame)) { used = 0; return; }
        for (int i = 1; i < code; ++i) frame[out++] = input[read++];
        if (code != 255 && read < used) frame[out++] = 0;
    }
    used = 0;
    if (out < 16 || frame[0] != 1 || out != (size_t)(frame[10] | frame[11] << 8) + 16 ||
        get32(frame + out - 4) != crc32(frame, out - 4)) return;
    request = get32(frame + 2);
    const uint32_t incoming_session = get32(frame + 6);
    const size_t length = out - 16;
    static char command[3016];
    if (frame[1] == 1 && request && length < sizeof(command) && !memchr(frame + 12, 0, length) &&
        !memchr(frame + 12, '\r', length) && !memchr(frame + 12, '\n', length)) {
        memcpy(command, frame + 12, length); command[length] = 0;
    } else if (frame[1] == 4 && length >= 4 && length <= 1476) {
        int start = snprintf(command, sizeof(command), "LDN_UDP %u.%u.%u.%u ", frame[12], frame[13], frame[14], frame[15]);
        static const char hex[] = "0123456789abcdef";
        for (size_t i = 4; i < length; ++i) {
            command[start++] = hex[frame[12 + i] >> 4]; command[start++] = hex[frame[12 + i] & 15];
        }
        command[start] = 0;
    } else { request = 0; return; }
    if (incoming_session != session && strcmp(command, "LDN_HELLO") && strncmp(command, "LDN_BEGIN ", 10))
        ldn_wire_printf("LDN_ERROR STALE_SESSION\n");
    else dispatch(command);
    if (request) ldn_wire_printf("LDN_DONE\n");
    request = 0;
}
#endif
