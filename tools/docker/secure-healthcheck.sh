#!/bin/sh
set -eu

require_port() {
    value="$1"
    name="$2"

    case "$value" in
        ''|*[!0-9]*)
            echo "invalid ${name}" >&2
            exit 1
            ;;
    esac

    if [ "$value" -lt 1 ] || [ "$value" -gt 65535 ]; then
        echo "invalid ${name}" >&2
        exit 1
    fi
}

has_socket() {
    protocol="$1"
    port="$2"
    state="$3"
    port_hex="$(printf '%04X' "$port")"

    awk -v expected_port="$port_hex" -v expected_state="$state" '
        FNR == 1 { next }
        {
            split($2, endpoint, ":")
            if (toupper(endpoint[2]) == expected_port &&
                toupper($4) == expected_state) {
                found = 1
            }
        }
        END { exit(found ? 0 : 1) }
    ' "/proc/net/${protocol}" "/proc/net/${protocol}6" 2>/dev/null
}

login_port="${GODSWAR_SECURE_LOGIN_PORT:-6599}"
game_port="${GODSWAR_SECURE_GAME_PORT:-7443}"
udp_port="${GODSWAR_SECURE_UDP_PORT:-7444}"

require_port "$login_port" GODSWAR_SECURE_LOGIN_PORT
require_port "$game_port" GODSWAR_SECURE_GAME_PORT
require_port "$udp_port" GODSWAR_SECURE_UDP_PORT

has_socket tcp "$login_port" 0A
has_socket tcp "$game_port" 0A
has_socket udp "$udp_port" 07
