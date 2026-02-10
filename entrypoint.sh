#!/bin/sh
set -eu

cleanup() {
    echo "Received signal, shutting down gracefully..."
    exit 0
}
trap cleanup TERM INT

# Config priority: init.yaml > init.conf (same as application)
if [ -f /app/config/init.yaml ]; then
    echo "Using existing configuration (init.yaml)..."
    cp /app/config/init.yaml /app/init.yaml
    chmod 640 /app/init.yaml
    rm -f /app/init.conf
elif [ -f /app/config/init.conf ]; then
    echo "Using existing configuration (init.conf)..."
    cp /app/config/init.conf /app/init.conf
    chmod 640 /app/init.conf
    rm -f /app/init.yaml
else
    echo "Initializing configuration..."
    if [ -f /app/Data/init.yaml ]; then
        cp /app/Data/init.yaml /app/config/init.yaml
        cp /app/Data/init.yaml /app/init.yaml
        chmod 640 /app/config/init.yaml /app/init.yaml
        rm -f /app/init.conf
    else
        cp /app/Data/init.conf /app/config/init.conf
        cp /app/Data/init.conf /app/init.conf
        chmod 640 /app/config/init.conf /app/init.conf
        rm -f /app/init.yaml
    fi
fi

if [ ! -r /app/init.yaml ] && [ ! -r /app/init.conf ]; then
    echo "ERROR: Cannot read configuration file (init.yaml or init.conf)" >&2
    exit 1
fi

if [ ! -x /app/JacRed ]; then
    echo "ERROR: Application binary is not executable" >&2
    exit 1
fi

# Start JacRed
echo "Starting Jacred (version: ${JACRED_VERSION:-unknown}) on $(date)"
echo "Architecture: $(uname -m)"
echo "User: $(id)"

exec "$@"
