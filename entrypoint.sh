#!/bin/sh
set -eu
# Best-effort: ensure pipeline failures are detected where supported
set -o pipefail 2>/dev/null || true
# Apply umask from env var (set in Dockerfile; controls permissions of files created at runtime)
umask "${UMASK:-0027}"
# CONFIG_FILE_MODE: permissions for copied config files (default: 600)

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

# Helper: get canonical path and capture exit status into named variable
# _status_var must contain only alphanumeric and underscore (validates against injection)
get_canonical_path_capture() {
    path="$1"
    _status_var="$2"
    case "$_status_var" in
        *[!a-zA-Z0-9_]*)
            echo "ERROR: Invalid status variable name (must be alphanumeric and underscore only)" >&2
            return 1
            ;;
    esac
    _output="$(get_canonical_path "$path")"
    _ec=$?
    eval "$_status_var=$_ec"
    echo "$_output"
}

# Helper: copy src to dest if comparison paths differ
copy_if_different() {
    src_path="$1"
    dest_path="$2"
    cmp_left="$3"
    cmp_right="$4"

    if [ "$cmp_left" != "$cmp_right" ]; then
        _mode="${CONFIG_FILE_MODE:-600}"
        install -m "$_mode" "$src_path" "$dest_path" || { echo "ERROR: Failed to copy config to $dest_path" >&2; return 1; }
    fi
}

# Helper: copy config to both dest_config and dest_app using given comparison paths
copy_config_files() {
    src_path="$1"
    dest_config_path="$2"
    dest_app_path="$3"
    cmp_left="$4"
    cmp_right_config="$5"
    cmp_right_app="$6"
    copy_if_different "$src_path" "$dest_config_path" "$cmp_left" "$cmp_right_config" || return 1
    copy_if_different "$src_path" "$dest_app_path" "$cmp_left" "$cmp_right_app" || return 1
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
        src_status=0
        dest_config_status=0
        dest_app_status=0
        src_canon="$(get_canonical_path_capture "$src" src_status)"
        dest_config_canon="$(get_canonical_path_capture "$dest_config" dest_config_status)"
        dest_app_canon="$(get_canonical_path_capture "$dest_app" dest_app_status)"
        # If canonicalization failed for any path, fall back to string-guarded copies to prevent self-copy.
        if [ "$src_status" -ne 0 ] || [ "$dest_config_status" -ne 0 ] || [ "$dest_app_status" -ne 0 ]; then
            copy_config_files "$src" "$dest_config" "$dest_app" "$src" "$dest_config" "$dest_app" || return 1
        else
            copy_config_files "$src" "$dest_config" "$dest_app" "$src_canon" "$dest_config_canon" "$dest_app_canon" || return 1
        fi
    else
        # Parent dirs not fully present; use string comparison as best-effort self-copy guard.
        copy_config_files "$src" "$dest_config" "$dest_app" "$src" "$dest_config" "$dest_app" || return 1
    fi

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
    use_existing_config yaml || {
        echo "ERROR: Failed to apply existing configuration (init.yaml)" >&2
        exit 1
    }
elif [ -f /app/config/init.conf ]; then
    echo "Using existing configuration (init.conf)..."
    use_existing_config conf || {
        echo "ERROR: Failed to apply existing configuration (init.conf)" >&2
        exit 1
    }
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
config_file_found=false
config_file_readable=false
for f in /app/init.yaml /app/init.conf; do
    if [ -f "$f" ]; then
        config_file_found=true
        if [ -r "$f" ]; then
            config_file_readable=true
            break
        fi
    fi
done
if [ "$config_file_found" = false ]; then
    echo "ERROR: Configuration file not found (init.yaml or init.conf)" >&2
    echo "Ensure /app/init.yaml or /app/init.conf exists" >&2
    exit 1
fi
if [ "$config_file_readable" = false ]; then
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
