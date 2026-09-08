#pragma once
#include <stddef.h>
#include <stdint.h>
void c3_transport_init(void);
int c3_transport_read(void *buffer, size_t length);
void c3_transport_write(const void *buffer, size_t length);
uint32_t c3_transport_dropped(void);
