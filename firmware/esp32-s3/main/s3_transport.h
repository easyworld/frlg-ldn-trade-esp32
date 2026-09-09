#pragma once

#include <stddef.h>
#include <stdint.h>

void s3_transport_init(void);
int s3_transport_read(void *buffer, size_t length);
void s3_transport_write(const void *buffer, size_t length);
void s3_transport_set_baud(int baud);
uint32_t s3_transport_dropped(void);
