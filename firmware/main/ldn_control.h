#pragma once

#include <stdbool.h>
#include "esp_netif.h"

typedef struct {
    uint32_t flags, control, prefix[2];
    uint16_t header, body, dma_length;
    uint8_t rate;
} ldn_hw_metadata_t;

void ldn_control_init(esp_netif_t *netif, const unsigned char host[6]);
void ldn_control_link(bool connected);
void ldn_control_poll(void);
void ldn_control_sniff(const unsigned char *frame, size_t length);
void ldn_control_hardware(const uint8_t *frame, size_t length, const ldn_hw_metadata_t *metadata);
