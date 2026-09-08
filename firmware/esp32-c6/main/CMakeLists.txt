idf_component_register(
    SRCS "ldn_probe.c" "ldn_led.c" "ldn_control.c" "ldn_private_raw.c" "ldn_tx_trace.c" "ldn_udp.c" "ldn_wire.c"
    INCLUDE_DIRS "."
    REQUIRES esp_event esp_netif esp_wifi esp_timer nvs_flash wpa_supplicant esp_hw_support spi_flash esp_driver_uart lwip led_strip
)

if(CONFIG_LDN_PROBE_PRIVATE_RAW_TX)
    file(SHA256 "$ENV{IDF_PATH}/components/esp_wifi/lib/esp32c6/libpp.a" pp_archive_sha256)
    if(NOT pp_archive_sha256 STREQUAL "4be44aeb1df591f15ca711fa09ffb9c39cb3f46b139545c9043bbc40fa31f0d6")
        message(FATAL_ERROR "Hardware TX tracing requires the audited C6 libpp.a")
    endif()
    target_link_libraries(${COMPONENT_LIB} INTERFACE "-Wl,--wrap=hal_mac_tx_set_ppdu")
    set(raw_overlay "${CMAKE_CURRENT_BINARY_DIR}/ldn_raw_overlay.o")
    set(raw_archive "$ENV{IDF_PATH}/components/esp_wifi/lib/esp32c6/libnet80211.a")
    idf_build_get_property(idf_python PYTHON)
    add_custom_command(OUTPUT "${raw_overlay}"
        COMMAND "${idf_python}" "${CMAKE_CURRENT_LIST_DIR}/../tools/prepare_raw_overlay.py"
            --archive "${raw_archive}" --output "${raw_overlay}"
            --ar "${CMAKE_AR}" --objcopy "${CMAKE_OBJCOPY}"
        DEPENDS "${raw_archive}" "${CMAKE_CURRENT_LIST_DIR}/../tools/prepare_raw_overlay.py"
        VERBATIM)
    set_source_files_properties("${raw_overlay}" PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)
    target_sources(${COMPONENT_LIB} PRIVATE "${raw_overlay}")
endif()
