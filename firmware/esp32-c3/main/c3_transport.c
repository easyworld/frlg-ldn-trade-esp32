#include "c3_transport.h"
#include "driver/usb_serial_jtag.h"
#include "driver/usb_serial_jtag_vfs.h"
#include "freertos/FreeRTOS.h"
#include "esp_err.h"

static uint32_t dropped;

void c3_transport_init(void)
{
    usb_serial_jtag_driver_config_t config = {.rx_buffer_size = 32768, .tx_buffer_size = 8192};
    ESP_ERROR_CHECK(usb_serial_jtag_driver_install(&config));
    usb_serial_jtag_vfs_use_driver();
}

int c3_transport_read(void *buffer, size_t length)
{
    return usb_serial_jtag_read_bytes(buffer, length, 0);
}

void c3_transport_write(const void *buffer, size_t length)
{
    /* Bound blocking if the host closes the port; a following COBS delimiter resynchronizes RX. */
    int written = usb_serial_jtag_write_bytes(buffer, length, pdMS_TO_TICKS(100));
    if (written != (int)length) ++dropped;
}

uint32_t c3_transport_dropped(void) { return dropped; }
