#pragma once
#include <stdbool.h>
#include "esp_err.h"

const char *ldn_session_ssid(void);
esp_err_t ldn_session_configure(const char *ssid, const char *bssid, const char *key, unsigned channel);
esp_err_t ldn_session_scan(unsigned channel);
void ldn_session_stop(void);
void ldn_control_target(const unsigned char host[6]);
