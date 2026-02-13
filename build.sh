#!/usr/bin/env bash
# Build JacRed for current architecture by default, or all architectures with --all flag
# Output: dist/<platform>/

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

OUTPUT_BASE="${OUTPUT_BASE:-$SCRIPT_DIR/dist}"

# Check for --all flag
BUILD_ALL=false
if [[ "${1:-}" == "--all" ]]; then
  BUILD_ALL=true
fi

# Detect current OS and architecture
detect_current_platform() {
  local os=""
  local arch=""
  
  case "$(uname -s)" in
    Linux*)   os="linux" ;;
    Darwin*)  os="osx" ;;
    MINGW*|MSYS*|CYGWIN*) os="windows" ;;
    *)        echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
  esac
  
  case "$(uname -m)" in
    x86_64|amd64) arch="amd64" ;;
    arm64|aarch64) arch="arm64" ;;
    *)        echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
  esac
  
  echo "${os}-${arch}"
}

# Build to a temp dir in project root so existing dist/ is never copied into a later publish output
BUILD_ROOT="$SCRIPT_DIR/.builds"
rm -rf "$BUILD_ROOT"
mkdir -p "$BUILD_ROOT"
trap 'rm -rf "$BUILD_ROOT"' EXIT
rm -fr dist

PUBLISH_OPTS=(
  --configuration Release
  --self-contained true
  -p:PublishTrimmed=false
  -p:PublishSingleFile=true
  -p:DebugType=None
  -p:EnableCompressionInSingleFile=true
  -p:OptimizationPreference=Speed
  -p:SuppressTrimAnalysisWarnings=true
  -p:IlcOptimizationPreference=Speed
  -p:IlcFoldIdenticalMethodBodies=true
)

build_for() {
  local rid="$1"
  local out_dir="$2"
  local name="$3"
  echo "==> Building for $name (RID: $rid) -> $out_dir"
  dotnet publish JacRed.csproj \
    --runtime "$rid" \
    --output "$out_dir" \
    "${PUBLISH_OPTS[@]}" \
    --verbosity minimal
  echo "    Done: $out_dir"
}

# Restore once for all targets
echo "==> Restoring packages..."
dotnet restore --verbosity minimal

ALL_PLATFORMS=(
  linux-arm64
  linux-amd64
  win-x64
  osx-arm64
  osx-amd64
)

if [[ "$BUILD_ALL" == "true" ]]; then
  PLATFORMS=("${ALL_PLATFORMS[@]}")
  echo "==> Building for all platforms..."
else
  CURRENT_PLATFORM=$(detect_current_platform)
  PLATFORMS=("$CURRENT_PLATFORM")
  echo "==> Building for current platform: $CURRENT_PLATFORM"
fi

for platform in "${PLATFORMS[@]}"; do
  build_for "$platform" "$BUILD_ROOT/$platform" "$platform"
done

# Replace dist with build result (avoids dist/ from project being copied into publish output)
echo "==> Writing to $OUTPUT_BASE ..."
OUTPUT_NEW="$(mktemp -d "${OUTPUT_BASE}.new.XXXXXX")"
mkdir -p "$OUTPUT_NEW"
for platform in "${PLATFORMS[@]}"; do
  mv "$BUILD_ROOT/$platform" "$OUTPUT_NEW/"
done
rm -rf "$OUTPUT_BASE"
mv "$OUTPUT_NEW" "$OUTPUT_BASE"

echo ""
echo "Build complete. Outputs:"
for platform in "${PLATFORMS[@]}"; do
  echo "  $OUTPUT_BASE/$platform/   ($platform)"
done
