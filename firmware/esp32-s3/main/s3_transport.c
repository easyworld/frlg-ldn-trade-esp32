#include "s3_transport.h"
#include <stdio.h>
#include "driver/uart.h"
#include "driver/uart_vfs.h"
#include "esp_err.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"

static uint32_t dropped;
static SemaphoreHandle_t s_tx_lock;

void s3_transport_init(void)
{
    s_tx_lock = xSemaphoreCreateMutex();
    ESP_ERROR_CHECK(s_tx_lock == NULL ? ESP_ERR_NO_MEM : ESP_OK);
    ESP_ERROR_CHECK(uart_driver_install(CONFIG_ESP_CONSOLE_UART_NUM, 32768, 8192, 0, NULL, 0));
    uart_vfs_dev_use_driver(CONFIG_ESP_CONSOLE_UART_NUM);
}

int s3_transport_read(void *buffer, size_t length)
{
    return uart_read_bytes(CONFIG_ESP_CONSOLE_UART_NUM, buffer, length, 0);
}

void s3_transport_write(const void *buffer, size_t length)
{
    xSemaphoreTake(s_tx_lock, portMAX_DELAY);
    /* Bound blocking if the host closes the port; a following COBS delimiter resynchronizes RX. */
    int written = uart_write_bytes(CONFIG_ESP_CONSOLE_UART_NUM, buffer, length);
    xSemaphoreGive(s_tx_lock);
    if (written != (int)length) ++dropped;
}

void s3_transport_set_baud(int baud)
{
    /* Take the stdout lock first: line-mode logs go straight to VFS from other
       tasks, binary-mode logs go through s3_transport_write. Keeping both out of
       the window between wait_tx_done and set_baudrate is what prevents a frame
       from straddling the two baud rates. */
    flockfile(stdout);
    xSemaphoreTake(s_tx_lock, portMAX_DELAY);
    fflush(stdout);
    /* The lock stops new TX, so the pending bytes always fit in the drain window;
       retries only cover a first pass that started mid-burst. */
    for (unsigned attempt = 0; attempt < 3; ++attempt) {
        const esp_err_t wait = uart_wait_tx_done(CONFIG_ESP_CONSOLE_UART_NUM, pdMS_TO_TICKS(1000));
        if (wait == ESP_OK) break;
        ++dropped;
    }
    uart_set_baudrate(CONFIG_ESP_CONSOLE_UART_NUM, baud);
    xSemaphoreGive(s_tx_lock);
    funlockfile(stdout);
}

uint32_t s3_transport_dropped(void) { return dropped; }
