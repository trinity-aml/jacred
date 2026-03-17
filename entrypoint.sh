#!/bin/sh
set -eu
# Best-effort: ensure pipeline failures are detected where supported
set -o pipefail 2>/dev/null || true
# Apply umask from env var (set in Dockerfile; controls permissions of files created at runtime)
umask "${UMASK:-0027}"

# Helper: get the alternate config extension (yaml <-> conf)
get_alternate_ext() {
    case "$1" in
        yaml) echo "conf" ;;
        conf) echo "yaml" ;;
        *) echo "unknown" ;;
    esac
}

# Helper: get canonical path (directory resolved via cd + pwd, basename appended)
get_canonical_path() {
    path="$1"
    dir="$(dirname "$path")" || return 1
    base="$(basename "$path")" || return 1
    # Only resolve when parent dir exists to mirror apply_config guard
    if [ ! -d "$dir" ]; then
        return 1
    fi
    canonical_dir="$(cd "$dir" && pwd)" || return 1
    echo "$canonical_dir/$base"
}

# Helper: apply config from given source path for given extension
# Copies to config/ and app root, sets permissions, removes alternate file.
apply_config() {
    ext="$1"
    src="$2"
    dest_config="/app/config/init.$ext"
    dest_app="/app/init.$ext"
    alt_ext="$(get_alternate_ext "$ext")"
    alt_app="/app/init.$alt_ext"
    alt_config="/app/config/init.$alt_ext"

    # Resolve to canonical paths for comparison (handles ., .., symlinks).
    # Only resolve when parent dirs exist to avoid cd failures under set -e.
    src_dir="$(dirname "$src")"
    dest_config_dir="$(dirname "$dest_config")"
    dest_app_dir="$(dirname "$dest_app")"
    if [ -d "$src_dir" ] && [ -d "$dest_config_dir" ] && [ -d "$dest_app_dir" ]; then
        if src_canon="$(get_canonical_path "$src")"; then src_status=0; else src_status=$?; fi
        if dest_config_canon="$(get_canonical_path "$dest_config")"; then dest_config_status=0; else dest_config_status=$?; fi
        if dest_app_canon="$(get_canonical_path "$dest_app")"; then dest_app_status=0; else dest_app_status=$?; fi
        # If canonicalization failed for any path, fall back to string-guarded copies to prevent self-copy.
        if [ $src_status -ne 0 ] || [ $dest_config_status -ne 0 ] || [ $dest_app_status -ne 0 ]; then
            [ "$src" != "$dest_config" ] && { cp "$src" "$dest_config" || { echo "ERROR: Failed to copy config to $dest_config" >&2; return 1; }; }
            [ "$src" != "$dest_app" ] && { cp "$src" "$dest_app" || { echo "ERROR: Failed to copy config to $dest_app" >&2; return 1; }; }
        else
            if [ "$src_canon" != "$dest_config_canon" ]; then
                cp "$src" "$dest_config" || { echo "ERROR: Failed to copy config to $dest_config" >&2; return 1; }
            fi
            if [ "$src_canon" != "$dest_app_canon" ]; then
                cp "$src" "$dest_app" || { echo "ERROR: Failed to copy config to $dest_app" >&2; return 1; }
            fi
        fi
    else
        # Parent dirs not fully present; use string comparison as best-effort self-copy guard.
        [ "$src" != "$dest_config" ] && { cp "$src" "$dest_config" || { echo "ERROR: Failed to copy config to $dest_config" >&2; return 1; }; }
        [ "$src" != "$dest_app" ] && { cp "$src" "$dest_app" || { echo "ERROR: Failed to copy config to $dest_app" >&2; return 1; }; }
    fi
    # 600 = owner read/write only; safe here since entrypoint and app both run as JacRed user
    chmod 600 "$dest_config" "$dest_app" || { echo "ERROR: Failed to set permissions on config files" >&2; return 1; }

    for f in "$alt_app" "$alt_config"; do
        if [ -f "$f" ]; then
            echo "Removing alternate config: $f"
            rm "$f" || { echo "ERROR: Failed to remove alternate config: $f" >&2; return 1; }
        fi
    done
}

# Helper: initialize config from Data or defaults for given extension (yaml or conf)
init_config() {
    ext="$1"
    data_file="/app/Data/init.$ext"
    default_file="/app/defaults/init.$ext"
    if [ -f "$data_file" ] || [ -f "$default_file" ]; then
        src="$data_file"
        [ -f "$src" ] || src="$default_file"
        apply_config "$ext" "$src"
        return 0
    fi
    return 1
}

# Helper: use existing config from config/ directory
use_existing_config() {
    ext="$1"
    apply_config "$ext" "/app/config/init.$ext"
}

# Config priority: init.yaml > init.conf (same as application)
if [ -f /app/config/init.yaml ]; then
    echo "Using existing configuration (init.yaml)..."
    use_existing_config yaml
elif [ -f /app/config/init.conf ]; then
    echo "Using existing configuration (init.conf)..."
    use_existing_config conf
else
    echo "Initializing configuration..."
    # Prefer Data (populated with named volumes), fallback to defaults (when Data is bind-mounted empty)
    init_config yaml || init_config conf || {
        echo "ERROR: No default init config (init.yaml or init.conf) found in /app/Data or /app/defaults" >&2
        echo "Ensure /app/Data or /app/defaults contains an init.yaml or init.conf used for initialization" >&2
        exit 1
    }
fi

# Config file paths (extend this list if additional formats are added)
# 0 = not found/not readable, 1 = found/readable
config_file_found=0
config_file_readable=0
for f in /app/init.yaml /app/init.conf; do
    if [ -f "$f" ]; then
        config_file_found=1
        if [ -r "$f" ]; then
            config_file_readable=1
            break
        fi
    fi
done
if [ "$config_file_found" -eq 0 ]; then
    echo "ERROR: Configuration file not found (init.yaml or init.conf)" >&2
    echo "Ensure /app/init.yaml or /app/init.conf exists" >&2
    exit 1
fi
if [ "$config_file_readable" -eq 0 ]; then
    echo "ERROR: Configuration file exists but is not readable" >&2
    echo "Check file permissions and ownership of /app/init.yaml or /app/init.conf" >&2
    exit 1
fi

if [ ! -f /app/JacRed ]; then
    echo "ERROR: Application binary does not exist at /app/JacRed" >&2
    echo "Ensure /app/JacRed is present in the container image" >&2
    exit 1
elif [ ! -x /app/JacRed ]; then
    echo "ERROR: Application binary is not executable" >&2
    echo "Ensure /app/JacRed has execute permissions" >&2
    exit 1
fi

# Start JacRed
echo "Starting JacRed (version: ${JACRED_VERSION:-unknown}) on $(date)"
echo "Architecture: $(uname -m)"
echo "User: $(id)"

exec "$@"
