idf_component_register(
    SRCS "ldn_probe.c" "ldn_control.c" "ldn_udp.c" "ldn_wire.c" "esp32_transport.c"
    INCLUDE_DIRS "."
    REQUIRES esp_event esp_netif esp_wifi esp_timer nvs_flash wpa_supplicant esp_hw_support spi_flash esp_driver_uart lwip
)
if(NOT CONFIG_IDF_TARGET_ESP32)
    message(FATAL_ERROR "This project is for classic ESP32 only")
endif()
file(SHA256 "$ENV{IDF_PATH}/components/esp_wifi/lib/esp32/libnet80211.a" esp32_driver_sha256)
if(NOT esp32_driver_sha256 STREQUAL "018dd68296790dadf4b4e9632f174d6c3a685ef786ec9d7429d3c4c6439f0cf1")
    message(FATAL_ERROR "ESP32 WPA callbacks and key APIs require the reviewed ESP-IDF v6.1 driver binary")
endif()
