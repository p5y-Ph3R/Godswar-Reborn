#!/bin/sh
set -eu

management_port="${GODSWAR_MANAGEMENT_PORT:-9090}"
case "$management_port" in
    ''|*[!0-9]*)
        exit 1
        ;;
esac

if [ "$management_port" -lt 1 ] || [ "$management_port" -gt 65535 ]; then
    exit 1
fi

exec dotnet /app/Godswar.Server.dll --management-probe ready "$management_port"
