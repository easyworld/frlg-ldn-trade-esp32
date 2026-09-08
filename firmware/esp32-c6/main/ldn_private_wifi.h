#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "esp_wifi.h"

/*
 * ABI copy of the private ESP-IDF v6.1 declarations used by the probe.
 * Source of truth:
 * components/wpa_supplicant/esp_supplicant/src/esp_wifi_driver.h
 */

enum ldn_wifi_appie {
    LDN_WIFI_APPIE_PROBEREQ = 0,
    LDN_WIFI_APPIE_ASSOC_REQ,
    LDN_WIFI_APPIE_ASSOC_RESP,
    LDN_WIFI_APPIE_WPA,
    LDN_WIFI_APPIE_RSN,
};

enum ldn_wpa_alg {
    LDN_WIFI_WPA_ALG_NONE = 0,
    LDN_WIFI_WPA_ALG_WEP40 = 1,
    LDN_WIFI_WPA_ALG_TKIP = 2,
    LDN_WIFI_WPA_ALG_CCMP = 3,
};

enum ldn_key_flag {
    LDN_KEY_FLAG_MODIFY = 1U << 0,
    LDN_KEY_FLAG_DEFAULT = 1U << 1,
    LDN_KEY_FLAG_RX = 1U << 2,
    LDN_KEY_FLAG_TX = 1U << 3,
    LDN_KEY_FLAG_GROUP = 1U << 4,
    LDN_KEY_FLAG_PAIRWISE = 1U << 5,
};

typedef struct {
    int proto;
    int pairwise_cipher;
    int group_cipher;
    int key_mgmt;
    int capabilities;
    size_t num_pmkid;
    const uint8_t *pmkid;
    int mgmt_group_cipher;
    uint8_t rsnxe_capa;
} ldn_wifi_wpa_ie_t;

typedef struct {
    void **sm;
    uint8_t *bssid;
    uint8_t *wpa_ie;
    uint8_t *rsnxe;
    bool *pmf_enable;
    uint8_t *pairwise_cipher;
    uint8_t *rsn_selection_ie;
    uint8_t *owe_dhie;
    int subtype;
    uint16_t rsnxe_len;
    uint8_t wpa_ie_len;
    uint8_t owe_dh_len;
} ldn_wpa_station_join_param_t;

struct ldn_wpa_funcs {
    bool (*wpa_sta_init)(void);
    bool (*wpa_sta_deinit)(void);
    int (*wpa_sta_connect)(uint8_t *bssid);
    void (*wpa_sta_connected_cb)(uint8_t *bssid);
    void (*wpa_sta_disconnected_cb)(uint8_t reason_code);
    int (*wpa_sta_rx_eapol)(uint8_t *src_addr, uint8_t *buf, uint32_t len);
    bool (*wpa_sta_in_4way_handshake)(void);
    void *(*wpa_ap_init)(void);
    bool (*wpa_ap_deinit)(void *data);
    bool (*wpa_ap_join)(ldn_wpa_station_join_param_t *join);
    bool (*wpa_ap_remove)(uint8_t *bssid);
    uint8_t *(*wpa_ap_get_wpa_ie)(size_t *len);
    bool (*wpa_ap_rx_eapol)(void *hapd_data, void *sm, uint8_t *data, size_t data_len);
    void (*wpa_ap_get_peer_spp_msg)(void *sm, bool *spp_cap, bool *spp_req);
    char *(*wpa_config_parse_string)(const char *value, size_t *len);
    int (*wpa_parse_wpa_ie)(const uint8_t *wpa_ie, size_t wpa_ie_len,
                            ldn_wifi_wpa_ie_t *data);
    int (*wpa_config_bss)(uint8_t *bssid);
    int (*wpa_michael_mic_failure)(uint16_t is_unicast);
    uint8_t *(*wpa3_build_sae_msg)(uint8_t *bssid, uint32_t type, size_t *len);
    int (*wpa3_parse_sae_msg)(uint8_t *buf, size_t len, uint32_t type, uint16_t status);
    int (*wpa3_hostap_handle_auth)(uint8_t *buf, size_t len, uint32_t type,
                                   uint16_t status, uint8_t *bssid);
    int (*wpa_sta_rx_mgmt)(uint8_t type, uint8_t *frame, size_t len,
                           uint8_t *sender, int8_t rssi, uint8_t channel,
                           uint64_t current_tsf);
    void (*wpa_config_done)(void);
    uint8_t *(*owe_build_dhie)(uint16_t group);
    int (*owe_process_assoc_resp)(const uint8_t *rsn_ie, size_t rsn_len,
                                  const uint8_t *dh_ie, size_t dh_len);
    void (*wpa_sta_clear_curr_pmksa)(void);
    void (*wpa_config_reload)(void);
};

extern struct ldn_wpa_funcs *wpa_cb;

int esp_wifi_set_appie_internal(uint8_t type, uint8_t *ie, uint16_t len,
                                uint8_t flag);
int esp_wifi_set_sta_key_internal(int alg, uint8_t *addr, int key_idx,
                                  int set_tx, uint8_t *seq, size_t seq_len,
                                  uint8_t *key, size_t key_len,
                                  enum ldn_key_flag key_flag);
int esp_wifi_get_sta_key_internal(uint8_t *ifx, int *alg, uint8_t *addr,
                                  int *key_idx, uint8_t *key, size_t key_len,
                                  enum ldn_key_flag key_flag);
int esp_wifi_register_wpa_cb_internal(struct ldn_wpa_funcs *cb);
bool esp_wifi_auth_done_internal(void);
bool esp_wifi_sta_is_running_internal(void);
