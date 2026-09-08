idf_component_register(
    SRCS "ldn_probe.c" "ldn_control.c" "ldn_udp.c" "ldn_wire.c" "c3_transport.c"
    INCLUDE_DIRS "."
    REQUIRES esp_event esp_netif esp_wifi esp_timer nvs_flash wpa_supplicant esp_hw_support spi_flash esp_driver_usb_serial_jtag lwip
)
if(NOT CONFIG_IDF_TARGET_ESP32C3)
    message(FATAL_ERROR "This project is for ESP32-C3 only")
endif()
file(SHA256 "$ENV{IDF_PATH}/components/esp_wifi/lib/esp32c3/libnet80211.a" c3_driver_sha256)
if(NOT c3_driver_sha256 STREQUAL "0fcbed322d3254063b2c191bc8b8004e703ffd640296164a6552676180909a0c")
    message(FATAL_ERROR "C3 WPA callbacks and key APIs require the reviewed C3 driver binary")
endif()
