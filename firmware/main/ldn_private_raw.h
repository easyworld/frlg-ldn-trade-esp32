#pragma once

#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"

void ldn_private_raw_status(void);

esp_err_t ldn_private_raw_tx(const uint8_t *frame, size_t length, const uint8_t host[6]);
