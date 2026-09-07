#include "sdkconfig.h"
#include "ldn_wire.h"
#define printf ldn_wire_printf

#if CONFIG_LDN_PROBE_PRIVATE_RAW_TX
#include <inttypes.h>
#include <stdio.h>
#include <string.h>
#include "esp_attr.h"
#include "esp_memory_utils.h"
#include "freertos/FreeRTOS.h"
#include "ldn_control.h"
#include "ldn_tx_trace.h"

extern int __real_hal_mac_tx_set_ppdu(void *queue, void *context);
static uint8_t s_host[6], s_mac[6];
static bool s_ready;
static unsigned s_samples, s_invalid;
static portMUX_TYPE s_lock = portMUX_INITIALIZER_UNLOCKED;

static bool IRAM_ATTR readable(const void *pointer, size_t length)
{
    const uintptr_t address = (uintptr_t)pointer;
    return length && address <= UINTPTR_MAX - length &&
        esp_ptr_in_dram(pointer) && esp_ptr_in_dram((const void *)(address + length - 1));
}

static uint32_t IRAM_ATTR word(const void *pointer, size_t offset)
{
    uint32_t value;
    memcpy(&value, (const uint8_t *)pointer + offset, sizeof(value));
    return value;
}

static void IRAM_ATTR snapshot(void *queue)
{
    /* Pinned C6 ABI: queue->ebuf; ebuf has DMA head/tail at 4/8 and TX desc at 52.
       Wi-Fi DMA uses 14-bit size/length fields, unlike the public SLC lldesc. */
    if (!readable(queue, 4)) return;
    const uint8_t *ebuf = (const void *)word(queue, 0);
    if (!readable(ebuf, 56)) return;
    const uint8_t *dma = (const void *)word(ebuf, 4);
    const uint8_t *tail = (const void *)word(ebuf, 8);
    const uint8_t *desc = (const void *)word(ebuf, 52);
    if (!readable(dma, 12) || !readable(desc, 20)) return;
    const uint8_t *data = (const void *)word(dma, 4);
    const uint16_t eflags = (uint16_t)word(ebuf, 36);
    const unsigned prefix = (eflags & 0x2000) ? 8 : 0;
    if (!readable(data, prefix + 24)) return;
    const uint8_t *header = data + prefix;
    if (memcmp(header + 4, s_host, 6) || memcmp(header + 10, s_mac, 6)) return;
    if (header[0] != 0x00 && header[0] != 0x20 && header[0] != 0xb0 &&
        header[0] != 0x08 && header[0] != 0x88) return;

    ldn_hw_metadata_t m = {0};
    m.flags = word(desc, 0);
    m.control = word(desc, 16);
    m.rate = desc[12];
    const uint32_t lengths = word(ebuf, 20);
    m.header = lengths & 0xffff;
    m.body = lengths >> 16;
    if (prefix) memcpy(m.prefix, data, sizeof(m.prefix));
    const unsigned cipher = (m.control >> 8) & 15;
    const unsigned trailer = cipher == 0 ? 4 : cipher == 3 ? 12 : 0;
    const unsigned total = (unsigned)m.header + m.body;
    if (!trailer || total < prefix + trailer + 24 || total > 1550) {
        ++s_invalid;
        return;
    }
    const unsigned length = total - prefix - trailer;
    if (length > 1500 || s_samples >= 16) return;
    /* Copy only initialized MAC/IV/payload bytes, excluding hardware MIC/FCS space. */
    static uint8_t frame[1500];
    unsigned copied = 0, skip = prefix, dma_total = 0;
    for (unsigned i = 0; i < 8; ++i) {
        if (!readable(dma, 12)) break;
        const unsigned segment = (word(dma, 0) >> 14) & 0x3fff;
        data = (const void *)word(dma, 4);
        dma_total += segment;
        if (segment < skip) break;
        unsigned take = segment - skip;
        if (take > length - copied) take = length - copied;
        if (take && !readable(data + skip, take)) break;
        if (take) memcpy(frame + copied, data + skip, take);
        copied += take;
        skip = 0;
        if (dma == tail) {
            if (copied == length && dma_total == total) {
                m.dma_length = dma_total;
                ++s_samples;
                ldn_control_hardware(frame, length, &m);
                return;
            }
            break;
        }
        dma = (const void *)word(dma, 8);
    }
    ++s_invalid;
}

int IRAM_ATTR __wrap_hal_mac_tx_set_ppdu(void *queue, void *context)
{
    const int result = __real_hal_mac_tx_set_ppdu(queue, context);
    portENTER_CRITICAL_SAFE(&s_lock);
    if (s_ready) snapshot(queue);
    portEXIT_CRITICAL_SAFE(&s_lock);
    return result;
}

void ldn_tx_trace_init(const uint8_t host[6], const uint8_t mac[6])
{
    portENTER_CRITICAL_SAFE(&s_lock);
    memcpy(s_host, host, 6);
    memcpy(s_mac, mac, 6);
    s_ready = true;
    portEXIT_CRITICAL_SAFE(&s_lock);
}

void ldn_tx_trace_status(void)
{
    portENTER_CRITICAL_SAFE(&s_lock);
    const unsigned samples = s_samples, invalid = s_invalid;
    portEXIT_CRITICAL_SAFE(&s_lock);
    printf("LDN_HW_STATS samples=%u invalid=%u\n", samples, invalid);
}
#endif
