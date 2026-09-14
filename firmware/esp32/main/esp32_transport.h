#pragma once

#include <stddef.h>
#include <stdint.h>

void esp32_transport_init(void);
int esp32_transport_read(void *buffer, size_t length);
void esp32_transport_write(const void *buffer, size_t length);
void esp32_transport_set_baud(int baud);
uint32_t esp32_transport_dropped(void);
