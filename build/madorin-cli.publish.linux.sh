#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
SOLUTION_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
PS1="$SCRIPT_DIR/madorin-cli.publish.ps1"
WORKERS_JSON="$SCRIPT_DIR/provider-workers.json"
DEFAULT_RID="${MADORIN_PUBLISH_DEFAULT_RID:-linux-x64}"
RID_REGEX="${MADORIN_PUBLISH_RID_REGEX:-^(linux-x64|linux-arm64)$}"
HASH_CMD="${MADORIN_PUBLISH_HASH_CMD:-sha256sum}"

RID="$DEFAULT_RID"
REBUILD=0
NO_BUMP=0
BUMP=patch
SKIP_PROVIDERS=0

usage() {
  echo "Usage: $0 [--rid linux-x64|linux-arm64] [--rebuild] [--no-bump] [--bump patch|minor|major] [--skip-providers]" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      RID="${2:-}"
      shift 2
      ;;
    --rebuild)
      REBUILD=1
      shift
      ;;
    --no-bump)
      NO_BUMP=1
      shift
      ;;
    --bump)
      BUMP="${2:-}"
      shift 2
      ;;
    --skip-providers)
      SKIP_PROVIDERS=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ ! "$RID" =~ $RID_REGEX ]]; then
  echo "Unsupported RID '$RID'." >&2
  exit 1
fi

if [[ "$BUMP" != "patch" && "$BUMP" != "minor" && "$BUMP" != "major" ]]; then
  echo "Unsupported bump '$BUMP'." >&2
  exit 1
fi

if [[ ! -f "$PS1" ]]; then
  echo "Publish script was not found: $PS1" >&2
  exit 1
fi

if command -v pwsh >/dev/null 2>&1; then
  pwsh_args=(-NoProfile -File "$PS1" -Rid "$RID" -Bump "$BUMP")
  if [[ "$REBUILD" -eq 1 ]]; then
    pwsh_args+=(-Rebuild)
  fi
  if [[ "$NO_BUMP" -eq 1 ]]; then
    pwsh_args+=(-NoBump)
  fi
  if [[ "$SKIP_PROVIDERS" -eq 1 ]]; then
    pwsh_args+=(-SkipProviders)
  fi
  exec pwsh "${pwsh_args[@]}"
fi

echo "pwsh was not found; using native bash publish."

PROJECT_FILE="$SOLUTION_DIR/src/Madorin.AI.Runtime.Cli/Madorin.AI.Runtime.Cli.csproj"
CONTRACTS_PROJECT="$SOLUTION_DIR/src/Madorin.AI.Runtime.Contracts/Madorin.AI.Runtime.Contracts.csproj"
CLIENT_PROJECT="$SOLUTION_DIR/src/Madorin.AI.Runtime.Client/Madorin.AI.Runtime.Client.csproj"
PACKAGE_VERSIONS="$SOLUTION_DIR/Directory.Packages.props"
DIRECTORY_BUILD_PROPS="$SOLUTION_DIR/Directory.Build.props"
SKILL_SOURCE="$SOLUTION_DIR/skills/madorin-cli"
RELEASES_DIR="$SOLUTION_DIR/releases"
OUTPUT_DIR="$RELEASES_DIR/Madorin-Cli"
PACKAGES_DIR="$RELEASES_DIR/Packages"
SKILL_OUTPUT_DIR="$OUTPUT_DIR/madorin-cli"
BIN_DIR="$SKILL_OUTPUT_DIR/bin"
CLI_NAME="madorin-cli"
PACKAGE_NAME="Madorin.Cli"

if [[ ! -f "$DIRECTORY_BUILD_PROPS" ]]; then
  echo "Directory.Build.props was not found: $DIRECTORY_BUILD_PROPS" >&2
  exit 1
fi

read_cli_version() {
  sed -n 's/.*<MadorinCliVersion>\([^<]*\)<\/MadorinCliVersion>.*/\1/p' "$DIRECTORY_BUILD_PROPS" | head -n 1
}

set_xml_value() {
  local file="$1"
  local tag="$2"
  local value="$3"
  local tmp
  tmp=$(mktemp)
  awk -v tag="$tag" -v value="$value" '{
    gsub("<" tag ">[^<]*</" tag ">", "<" tag ">" value "</" tag ">")
    print
  }' "$file" > "$tmp"
  mv "$tmp" "$file"
}

bump_version() {
  local current major minor patch
  current=$(read_cli_version)
  IFS='.' read -r major minor patch <<< "$current"
  case "$BUMP" in
    major) major=$((major + 1)); minor=0; patch=0 ;;
    minor) minor=$((minor + 1)); patch=0 ;;
    patch) patch=$((patch + 1)) ;;
  esac
  local next="$major.$minor.$patch"
  set_xml_value "$DIRECTORY_BUILD_PROPS" MadorinCliVersion "$next"
  echo "$next"
}

list_workers() {
  awk '
    /"protocol"/ { proto=$0; gsub(/.*"protocol": "|".*/, "", proto) }
    /"slug"/ { slug=$0; gsub(/.*"slug": "|".*/, "", slug) }
    /"project"/ { proj=$0; gsub(/.*"project": "|".*/, "", proj); print proto "|" slug "|" proj }
  ' "$WORKERS_JSON"
}

worker_file_name() {
  if [[ "$RID" == win* ]]; then
    echo "madorin-provider-$1.exe"
  else
    echo "madorin-provider-$1"
  fi
}

if [[ "$NO_BUMP" -eq 1 ]]; then
  NEW_VERSION=$(read_cli_version)
else
  NEW_VERSION=$(bump_version)
  echo "Version bumped to $NEW_VERSION"
fi
set_xml_value "$PACKAGE_VERSIONS" MadorinRuntimeContractsVersion "$NEW_VERSION"

RESTORE_PACKAGES_ROOT="$SOLUTION_DIR/obj/release-packages"
RESTORE_PACKAGES_DIR="$RESTORE_PACKAGES_ROOT/$NEW_VERSION"
NUGET_CONFIG="$RESTORE_PACKAGES_DIR/NuGet.Config"

mkdir -p "$OUTPUT_DIR" "$PACKAGES_DIR"
rm -rf "$SKILL_OUTPUT_DIR"
cp -a "$SKILL_SOURCE" "$SKILL_OUTPUT_DIR"
mkdir -p "$BIN_DIR"
rm -rf "$RESTORE_PACKAGES_DIR"
mkdir -p "$RESTORE_PACKAGES_DIR"

cat > "$NUGET_CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="madorin-local" value="$PACKAGES_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="netor.me" value="http://nuget.netor.me/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="madorin-local">
      <package pattern="Madorin.AI.Runtime.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="netor.me">
      <package pattern="Netor.*" />
      <package pattern="Madorin.AI.Runtime.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

dotnet_common=(--disable-build-servers --nologo
  "-p:Version=$NEW_VERSION"
  "-p:AssemblyVersion=$NEW_VERSION"
  "-p:FileVersion=$NEW_VERSION"
  "-p:InformationalVersion=$NEW_VERSION"
  "-p:MadorinRuntimeContractsVersion=$NEW_VERSION"
  "-p:RestoreConfigFile=$NUGET_CONFIG"
  "-p:RestorePackagesPath=$RESTORE_PACKAGES_DIR")

if [[ "$REBUILD" -eq 1 ]]; then
  rm -rf "$SOLUTION_DIR/src/Madorin.AI.Runtime.Cli/bin/Release" \
    "$SOLUTION_DIR/src/Madorin.AI.Runtime.Cli/obj/Release" \
    "$SOLUTION_DIR/src/Madorin.AI.Runtime.Contracts/bin/Release" \
    "$SOLUTION_DIR/src/Madorin.AI.Runtime.Contracts/obj/Release" \
    "$SOLUTION_DIR/src/Madorin.AI.Runtime.Client/bin/Release" \
    "$SOLUTION_DIR/src/Madorin.AI.Runtime.Client/obj/Release"
fi

echo "[1/7] Copied skill files"
echo "[2/7] Packing Contracts"
dotnet pack "$CONTRACTS_PROJECT" -c Release -o "$PACKAGES_DIR" \
  "-p:Version=$NEW_VERSION" "-p:PackageVersion=$NEW_VERSION" \
  "-p:AssemblyVersion=$NEW_VERSION" "-p:FileVersion=$NEW_VERSION" \
  "-p:InformationalVersion=$NEW_VERSION" "${dotnet_common[@]}"

echo "[3/7] Publishing CLI"
dotnet publish "$PROJECT_FILE" -c Release -r "$RID" --self-contained true -o "$BIN_DIR" \
  "${dotnet_common[@]}"

if [[ "$SKIP_PROVIDERS" -eq 1 ]]; then
  echo "[4/7] Skipping provider workers"
else
  echo "[4/7] Publishing provider workers"
  while IFS='|' read -r protocol slug project; do
    [[ -z "$slug" ]] && continue
    project_path="$SOLUTION_DIR/$project"
    echo "  $protocol ($slug)"
    dotnet publish "$project_path" -c Release -r "$RID" --self-contained true -o "$BIN_DIR" \
      "${dotnet_common[@]}"
    worker_path="$BIN_DIR/$(worker_file_name "$slug")"
    if [[ ! -f "$worker_path" ]]; then
      echo "Provider worker output was not found: $worker_path" >&2
      exit 1
    fi
    size_mb=$(awk -v s="$(wc -c < "$worker_path")" 'BEGIN { printf "%.2f", s / 1024 / 1024 }')
    echo "    $worker_path (${size_mb} MB)"
  done < <(list_workers)
fi

echo "[5/7] Packing Client"
dotnet pack "$CLIENT_PROJECT" -c Release -o "$PACKAGES_DIR" \
  "-p:Version=$NEW_VERSION" "-p:PackageVersion=$NEW_VERSION" \
  "-p:AssemblyVersion=$NEW_VERSION" "-p:FileVersion=$NEW_VERSION" \
  "-p:InformationalVersion=$NEW_VERSION" "${dotnet_common[@]}"

echo "[6/7] Cleaning extra files"
find "$BIN_DIR" -maxdepth 1 -type f \( -name '*.pdb' -o -name '*.xml' -o -name '*.deps.json' -o -name '*.runtimeconfig.dev.json' \) -delete
chmod 755 "$BIN_DIR/$CLI_NAME" 2>/dev/null || true
find "$BIN_DIR" -maxdepth 1 -type f ! -name '*.*' -exec chmod 755 {} +

manifest="$BIN_DIR/providers.manifest.json"
{
  echo "{"
  echo "  \"version\": \"$NEW_VERSION\","
  echo "  \"rid\": \"$RID\","
  echo "  \"workers\": ["
  first=1
  while IFS='|' read -r protocol slug project; do
    [[ -z "$slug" ]] && continue
    file_name=$(worker_file_name "$slug")
    if [[ "$first" -eq 0 ]]; then
      echo ","
    fi
    first=0
    printf '    { "protocol": "%s", "slug": "%s", "fileName": "%s" }' "$protocol" "$slug" "$file_name"
  done < <(list_workers)
  echo
  echo "  ]"
  echo "}"
} > "$manifest"
echo "  Manifest: $manifest"

echo "[7/7] Packaging zip"
zip_name="$PACKAGE_NAME-v$NEW_VERSION-$RID.zip"
hash_name="$PACKAGE_NAME-v$NEW_VERSION-$RID.sha256"
zip_path="$RELEASES_DIR/$zip_name"
hash_path="$RELEASES_DIR/$hash_name"
rm -f "$zip_path" "$hash_path"
(
  cd "$OUTPUT_DIR"
  zip -r "$zip_path" "madorin-cli"
)
if [[ "$HASH_CMD" == "sha256sum" ]]; then
  (cd "$RELEASES_DIR" && sha256sum "$zip_name" | awk '{print $1 " *" $2}' > "$hash_name")
else
  (cd "$RELEASES_DIR" && shasum -a 256 "$zip_name" | awk '{print $1 " *" $2}' > "$hash_name")
fi
echo "  Zip: $zip_path"
echo "  SHA256: $hash_path"
