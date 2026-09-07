#pragma once

#include <stdint.h>

void ldn_tx_trace_init(const uint8_t host[6], const uint8_t mac[6]);
void ldn_tx_trace_status(void);
