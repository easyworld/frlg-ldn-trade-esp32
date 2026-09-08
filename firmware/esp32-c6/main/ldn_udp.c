#include "sdkconfig.h"
#if CONFIG_LDN_PROBE_CONTROL_PORT
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include "esp_timer.h"
#include "esp_wifi.h"
#include "lwip/etharp.h"
#include "lwip/sockets.h"
#include "lwip/tcpip.h"
#include "ldn_udp.h"
#include "ldn_wire.h"
#define printf ldn_wire_printf

#if !ETHARP_SUPPORT_STATIC_ENTRIES
#error "LDN requires CONFIG_LWIP_DHCPS_STATIC_ENTRIES for static neighbor support"
#endif

#define PIA_PORT 12345
#define PAYLOAD_MAX 1472
#define PEERS_MAX 8

static esp_netif_t *s_netif;
static int s_socket = -1;
static uint8_t s_host[6];
static ip4_addr_t s_ip, s_peers[PEERS_MAX];
static int64_t s_heartbeat;
static unsigned s_tx, s_rx, s_rejected;

typedef struct {
    ip4_addr_t ip;
    struct eth_addr mac;
    bool remove;
    err_t result;
} neighbor_call_t;

static void neighbor_callback(void *arg)
{
    neighbor_call_t *call = arg;
    call->result = call->remove ? etharp_remove_static_entry(&call->ip)
                               : etharp_add_static_entry(&call->ip, &call->mac);
}

static int neighbor(ip4_addr_t ip, const uint8_t *mac)
{
    int slot = -1;
    for (int i = 0; i < PEERS_MAX; ++i) {
        if (s_peers[i].addr == ip.addr) { slot = i; break; }
        if (!s_peers[i].addr && slot < 0) slot = i;
    }
    if (slot < 0) return ESP_ERR_NO_MEM;
    neighbor_call_t call = {.ip = ip, .remove = mac == NULL};
    if (mac) memcpy(call.mac.addr, mac, 6);
    if (tcpip_callback_wait(neighbor_callback, &call) != ERR_OK) return ESP_FAIL;
    if (call.result != ERR_OK && !(call.remove && call.result == ERR_ARG)) return ESP_FAIL;
    s_peers[slot].addr = mac ? ip.addr : 0;
    return ESP_OK;
}

void ldn_udp_init(esp_netif_t *netif, const uint8_t host[6])
{
    s_netif = netif;
    memcpy(s_host, host, 6);
}

void ldn_udp_stop(void)
{
    if (s_socket >= 0) close(s_socket);
    s_socket = -1;
    for (int i = 0; i < PEERS_MAX; ++i) {
        if (s_peers[i].addr) neighbor(s_peers[i], NULL);
    }
    s_ip.addr = 0;
    esp_netif_ip_info_t empty = {0};
    esp_netif_set_ip_info(s_netif, &empty);
}

static bool local_address(const char *text, ip4_addr_t *ip)
{
    if (!ip4addr_aton(text, ip)) return false;
    uint32_t value = ntohl(ip->addr);
    return (value >> 16) == 0xa9fe && (value & 255) > 0 && (value & 255) < 255;
}

static bool same_subnet(ip4_addr_t ip)
{
    return s_ip.addr && ((ntohl(ip.addr) ^ ntohl(s_ip.addr)) >> 8) == 0;
}

static int start(const char *ours, const char *host)
{
    ip4_addr_t ip, peer;
    if (!local_address(ours, &ip) || !local_address(host, &peer) ||
        ip.addr == peer.addr || ((ntohl(ip.addr) ^ ntohl(peer.addr)) >> 8)) return ESP_ERR_INVALID_ARG;
    ldn_udp_stop();
    esp_netif_ip_info_t info = {0};
    info.ip.addr = ip.addr;
    info.netmask.addr = htonl(0xffffff00);
    int result = esp_netif_set_ip_info(s_netif, &info);
    if (result != ESP_OK) return result;
    s_ip = ip;
    result = neighbor(peer, s_host);
    if (result != ESP_OK) goto failed;
    s_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s_socket < 0) goto failed;
    int yes = 1;
    struct sockaddr_in address = {.sin_family = AF_INET, .sin_port = htons(PIA_PORT),
                                 .sin_addr.s_addr = INADDR_ANY};
    if (setsockopt(s_socket, SOL_SOCKET, SO_BROADCAST, &yes, sizeof(yes)) ||
        bind(s_socket, (struct sockaddr *)&address, sizeof(address)) ||
        fcntl(s_socket, F_SETFL, O_NONBLOCK)) goto failed;
    s_tx = s_rx = s_rejected = 0;
    s_heartbeat = esp_timer_get_time();
    return ESP_OK;
failed:
    ldn_udp_stop();
    return ESP_FAIL;
}

static int from_hex(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

bool ldn_udp_command(const char *line, bool connected)
{
    if (!strcmp(line, "LDN_PING")) {
        s_heartbeat = esp_timer_get_time();
        printf("LDN_PONG %u %u %u\n", s_tx, s_rx, s_rejected);
    } else if (!strcmp(line, "LDN_STOP")) {
        ldn_udp_stop();
        esp_wifi_disconnect();
        printf("LDN_STOPPED\n");
    } else if (!strncmp(line, "LDN_NET ", 8)) {
        char ours[16], host[16], extra;
        int result = ESP_ERR_INVALID_ARG;
        if (sscanf(line + 8, "%15s %15s %c", ours, host, &extra) == 2)
            result = connected ? start(ours, host) : ESP_ERR_INVALID_STATE;
        printf("LDN_NET_RESULT %d\n", result);
    } else if (!strncmp(line, "LDN_NEIGH ", 10)) {
        char address[16], mac[13], extra;
        uint8_t bytes[6];
        ip4_addr_t ip;
        int result = ESP_ERR_INVALID_ARG;
        if (sscanf(line + 10, "%15s %12s %c", address, mac, &extra) == 2 &&
            strlen(mac) == 12 && local_address(address, &ip) && same_subnet(ip) && ip.addr != s_ip.addr) {
            bool valid = true;
            for (int i = 0; i < 6; ++i) {
                int hi = from_hex(mac[2 * i]), lo = from_hex(mac[2 * i + 1]);
                if (hi < 0 || lo < 0) valid = false;
                bytes[i] = (uint8_t)((hi < 0 ? 0 : hi) * 16 + (lo < 0 ? 0 : lo));
            }
            if (valid && !(bytes[0] & 1)) result = neighbor(ip, bytes);
        }
        printf("LDN_NEIGH_RESULT %d\n", result);
    } else if (!strncmp(line, "LDN_FORGET ", 11)) {
        ip4_addr_t ip;
        int result = ESP_ERR_INVALID_ARG;
        if (local_address(line + 11, &ip) && same_subnet(ip)) result = neighbor(ip, NULL);
        printf("LDN_NEIGH_RESULT %d\n", result);
    } else if (!strncmp(line, "LDN_UDP ", 8)) {
        static uint8_t payload[PAYLOAD_MAX];
        char address[16];
        const char *separator = strchr(line + 8, ' ');
        int result = ESP_ERR_INVALID_ARG;
        if (!separator || separator - (line + 8) > 15 || separator == line + 8) goto tx_done;
        memcpy(address, line + 8, separator - (line + 8));
        address[separator - (line + 8)] = 0;
        struct sockaddr_in dest = {.sin_family = AF_INET, .sin_port = htons(PIA_PORT)};
        ip4_addr_t ip;
        if (!ip4addr_aton(address, &ip) || !same_subnet(ip) || ip.addr == s_ip.addr ||
            (ntohl(ip.addr) & 255) == 0) goto tx_done;
        dest.sin_addr.s_addr = ip.addr;
        const char *hex = separator + 1;
        size_t n = strlen(hex);
        if (n > PAYLOAD_MAX * 2 || n % 2) goto tx_done;
        for (size_t i = 0; i < n / 2; ++i) {
            int hi = from_hex(hex[2 * i]), lo = from_hex(hex[2 * i + 1]);
            if (hi < 0 || lo < 0) goto tx_done;
            payload[i] = (uint8_t)(hi * 16 + lo);
        }
        result = ESP_ERR_INVALID_STATE;
        if (s_socket < 0 || !connected) goto tx_done;
        result = sendto(s_socket, payload, n / 2, 0, (struct sockaddr *)&dest, sizeof(dest)) == n / 2
                     ? ESP_OK : ESP_FAIL;
tx_done:
        if (result == ESP_OK) ++s_tx;
        else { ++s_rejected; printf("LDN_UDP_ERROR %d\n", result); }
    } else return false;
    return true;
}

void ldn_udp_poll(bool connected)
{
    if (s_socket < 0) return;
    if (!connected || esp_timer_get_time() - s_heartbeat > 10000000) {
        ldn_udp_stop();
        esp_wifi_disconnect();
        printf("LDN_NET_LOST\n");
        return;
    }
    static uint8_t bytes[PAYLOAD_MAX + 1];
    static char hex[PAYLOAD_MAX * 2 + 1];
    static const char digits[] = "0123456789abcdef";
    /* Bound UART TX blocking so commands and the heartbeat are serviced between bursts. */
    for (int count = 0; count < 2; ++count) {
        struct sockaddr_in source;
        socklen_t size = sizeof(source);
        int n = recvfrom(s_socket, bytes, sizeof(bytes), 0, (struct sockaddr *)&source, &size);
        if (n < 0) {
            if (errno != EAGAIN && errno != EWOULDBLOCK) printf("LDN_UDP_ERROR %d\n", errno);
            break;
        }
        bool known = false;
        for (int i = 0; i < PEERS_MAX; ++i) known |= s_peers[i].addr == source.sin_addr.s_addr;
        if (n > PAYLOAD_MAX || source.sin_port != htons(PIA_PORT) || !known) { ++s_rejected; continue; }
        for (int i = 0; i < n; ++i) { hex[2 * i] = digits[bytes[i] >> 4]; hex[2 * i + 1] = digits[bytes[i] & 15]; }
        hex[n * 2] = 0;
        ++s_rx;
        printf("LDN_DATAGRAM %s %s\n", inet_ntoa(source.sin_addr), hex);
    }
}
#endif
