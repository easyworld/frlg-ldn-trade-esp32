idf_component_register(
    SRCS "ldn_probe.c" "ldn_control.c" "ldn_udp.c" "ldn_wire.c" "s3_transport.c"
    INCLUDE_DIRS "."
    REQUIRES esp_event esp_netif esp_wifi esp_timer nvs_flash wpa_supplicant esp_hw_support spi_flash esp_driver_uart lwip
)
if(NOT CONFIG_IDF_TARGET_ESP32S3)
    message(FATAL_ERROR "This project is for ESP32-S3 only")
endif()
file(SHA256 "$ENV{IDF_PATH}/components/esp_wifi/lib/esp32s3/libnet80211.a" s3_driver_sha256)
if(NOT s3_driver_sha256 STREQUAL "265e5c89ac0d2a9444b5b466afee49f631ec549f96774f91a721ab5069c58e20")
    message(FATAL_ERROR "S3 WPA callbacks and key APIs require the reviewed S3 driver binary")
endif()
