#pragma once

#include "esp_netif.h"

void ldn_udp_init(esp_netif_t *netif, const uint8_t host[6]);
bool ldn_udp_command(const char *line, bool connected);
void ldn_udp_poll(bool connected);
void ldn_udp_stop(void);
