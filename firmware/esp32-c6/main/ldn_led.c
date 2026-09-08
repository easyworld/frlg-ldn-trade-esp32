#include "sdkconfig.h"

#include <stdbool.h>
#include <stdint.h>

#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "led_strip.h"

#include "ldn_led.h"

#if CONFIG_LDN_PROBE_STATUS_LED

static const char *TAG = "ldn_led";

typedef struct {
    uint8_t red, green, blue;
    uint32_t period_ms; /* Full dark-bright-dark cycle. */
} led_style_t;

/* Peaks stay near half scale: the onboard LED is bright and runs unbuffered. */
static const led_style_t s_styles[] = {
    [LDN_LED_BOOT] = {90, 90, 90, 1600},
    [LDN_LED_IDLE] = {0, 0, 100, 3200},
    [LDN_LED_JOINING] = {130, 60, 0, 1100},
    [LDN_LED_LINKED] = {0, 100, 25, 2400},
    [LDN_LED_ERROR] = {120, 0, 10, 700},
};

#define LED_FRAME_MS 20
#define LED_ERROR_REVERT_US (3 * 1000000)

static led_strip_handle_t s_strip;
static volatile ldn_led_state_t s_state = LDN_LED_BOOT;
static int64_t s_error_deadline;

/* (1 - cos(2*pi*x/256)) / 2 scaled 0..128, quarter wave for x = 0..64. */
static const uint8_t s_breathe[65] = {
    0, 1, 2, 5, 10, 15, 21, 29, 37, 47, 57, 67,
    79, 90, 103, 115, 128,
};

static uint32_t breathe_level(uint8_t phase)
{
    const uint8_t rising = phase < 128 ? phase : (uint8_t)(256 - phase);
    const uint32_t level = rising <= 64 ? s_breathe[rising] : s_breathe[128 - rising];
    return level * 2; /* 0..256 */
}

/* Quadratic gamma keeps the bottom of the breath visibly dim on WS2812. */
static uint8_t dim(uint8_t peak, uint32_t level)
{
    return (uint8_t)((peak * level * level) >> 16);
}

static void show(const led_style_t *style, uint32_t level)
{
    if (led_strip_set_pixel(s_strip, 0, dim(style->red, level),
                            dim(style->green, level), dim(style->blue, level)) != ESP_OK ||
        led_strip_refresh(s_strip) != ESP_OK) {
        /* A status light must never take the trade bridge down with it. */
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

static void led_task(void *argument)
{
    (void)argument;
    for (;;) {
        if (s_state == LDN_LED_ERROR && esp_timer_get_time() >= s_error_deadline) {
            s_state = LDN_LED_IDLE;
            continue;
        }
        const led_style_t *style = &s_styles[s_state];
        const int64_t period = (int64_t)style->period_ms * 1000;
        const int64_t phase = esp_timer_get_time() % period;
        show(style, breathe_level((uint8_t)(phase * 256 / period)));
        vTaskDelay(pdMS_TO_TICKS(LED_FRAME_MS));
    }
}

void ldn_led_init(void)
{
    const led_strip_config_t strip = {
        .strip_gpio_num = CONFIG_LDN_PROBE_STATUS_LED_GPIO,
        .max_leds = 1,
        .led_model = LED_MODEL_WS2812,
        .color_component_format = LED_STRIP_COLOR_COMPONENT_FMT_GRB,
    };
    const led_strip_rmt_config_t rmt = {
        .resolution_hz = 10 * 1000 * 1000, /* 100 ns ticks */
    };
    if (led_strip_new_rmt_device(&strip, &rmt, &s_strip) != ESP_OK ||
        xTaskCreate(led_task, "ldn_led", 2560, NULL, 4, NULL) != pdPASS) {
        ESP_LOGE(TAG, "status LED unavailable; continuing without it");
    }
}

void ldn_led_set(ldn_led_state_t state)
{
    if ((unsigned)state > LDN_LED_ERROR) return;
    if (state == LDN_LED_ERROR) {
        s_error_deadline = esp_timer_get_time() + LED_ERROR_REVERT_US;
    }
    s_state = state;
}

#else

void ldn_led_init(void) {}
void ldn_led_set(ldn_led_state_t state) { (void)state; }

#endif
