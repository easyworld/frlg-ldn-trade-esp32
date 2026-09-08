#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

void ldn_wire_enable(void);
bool ldn_wire_active(void);
void ldn_wire_session(uint32_t session);
void ldn_wire_feed(uint8_t byte, void (*dispatch)(const char *));
int ldn_wire_printf(const char *format, ...) __attribute__((format(printf, 1, 2)));
