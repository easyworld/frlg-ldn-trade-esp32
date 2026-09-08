#pragma once
#include <stdbool.h>
#include "esp_netif.h"
void ldn_control_init(esp_netif_t *netif, const unsigned char host[6]);
void ldn_control_link(bool connected);
void ldn_control_poll(void);
void ldn_control_sniff(const unsigned char *frame, size_t length);
void ldn_control_target(const unsigned char host[6]);
