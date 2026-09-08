#pragma once

/* Breathing status light on the onboard WS2812. Each state breathes in its
   own color and tempo, so the link state is readable across the room. */
typedef enum {
    LDN_LED_BOOT,    /* white: powering up */
    LDN_LED_IDLE,    /* blue, slow: waiting for the host to send a room */
    LDN_LED_JOINING, /* amber, fast: associating with a room */
    LDN_LED_LINKED,  /* green: room authenticated, bridge ready */
    LDN_LED_ERROR,   /* red, fast: join failed; reverts to idle by itself */
} ldn_led_state_t;

void ldn_led_init(void);
void ldn_led_set(ldn_led_state_t state);
