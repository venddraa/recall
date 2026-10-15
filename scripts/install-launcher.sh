#!/usr/bin/env bash
set -euo pipefail

# Launcher development: binary dan ikon tetap berada di folder project.
# --prepare-only membangun dan memvalidasi tanpa menulis ke direktori pengguna.
prepare_only=false
case "${1:-}" in
    --prepare-only) prepare_only=true ;;
    "") ;;
    *) printf 'Penggunaan: bash scripts/install-launcher.sh [--prepare-only]\n' >&2; exit 1 ;;
esac
if (( $# > 1 )); then
    printf 'Terlalu banyak argumen.\n' >&2
    exit 1
fi

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
cd -- "$project_dir"
publish_dir="$project_dir/artifacts/linux"
launcher_path="$project_dir/artifacts/Recall.desktop"
app_path="$publish_dir/Recall.Desktop"
icon_path="$project_dir/packaging/linux/recall.svg"

command -v dotnet >/dev/null || { printf '.NET SDK diperlukan untuk membangun Recall.\n' >&2; exit 1; }
if [[ "$app_path" == *'='* ]]; then
    printf 'Lokasi project tidak boleh mengandung tanda = untuk launcher desktop.\n' >&2
    exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
printf 'Membangun Recall. Tutup Recall terlebih dahulu jika sedang berjalan.\n'
dotnet publish "$project_dir/src/Recall.Desktop/Recall.Desktop.csproj" \
    --configuration Release --no-restore --self-contained false \
    --disable-build-servers -m:1 --output "$publish_dir"
[[ -x "$app_path" ]] || { printf 'Executable Recall tidak ditemukan.\n' >&2; exit 1; }

# Desktop Entry memiliki aturan escaping sendiri, bukan aturan shell.
desktop_string() {
    local value=$1
    value=${value//\\/\\\\}
    value=${value//$'\n'/\\n}
    value=${value//$'\r'/\\r}
    value=${value//$'\t'/\\t}
    printf '%s' "$value"
}
desktop_exec() {
    local value=$1
    value=${value//\\/\\\\}
    value=${value//\"/\\\"}
    value=${value//\`/\\\`}
    value=${value//\$/\\\$}
    value=${value//%/%%}
    desktop_string "\"$value\""
}

{
    printf '[Desktop Entry]\nType=Application\nVersion=1.0\nName=Recall\n'
    printf 'Comment=Find local files by the words you remember\n'
    printf 'Comment[id]=Temukan file lokal dari kata yang kamu ingat\n'
    printf 'Exec=%s\n' "$(desktop_exec "$app_path")"
    printf 'Icon=%s\n' "$(desktop_string "$icon_path")"
    printf 'Terminal=false\nCategories=Utility;\nKeywords=Search;Files;Local;\n'
    printf 'StartupNotify=true\nStartupWMClass=Recall\n'
} > "$launcher_path"
if command -v desktop-file-validate >/dev/null; then
    desktop-file-validate "$launcher_path"
fi
if "$prepare_only"; then
    printf 'Launcher siap: %s\n' "$launcher_path"
    exit 0
fi

data_home=${XDG_DATA_HOME:-"$HOME/.local/share"}
[[ "$data_home" == /* ]] || data_home="$HOME/.local/share"
applications_dir="$data_home/applications"
install -d -- "$applications_dir"
install -m 644 -- "$launcher_path" "$applications_dir/Recall.desktop"
if command -v update-desktop-database >/dev/null; then
    update-desktop-database "$applications_dir" || printf 'Cache menu belum diperbarui; coba buka ulang pencarian aplikasi.\n' >&2
fi

desktop_dir="$HOME/Desktop"
if command -v xdg-user-dir >/dev/null; then
    desktop_dir=$(xdg-user-dir DESKTOP)
fi
if [[ "$desktop_dir" == /* && "$desktop_dir" != "$HOME" ]]; then
    install -d -- "$desktop_dir"
    install -m 755 -- "$launcher_path" "$desktop_dir/Recall.desktop"
    if command -v gio >/dev/null; then
        gio set "$desktop_dir/Recall.desktop" metadata::trusted true 2>/dev/null \
            || printf 'Jika diminta, klik kanan launcher Desktop lalu pilih Allow Launching / Izinkan Peluncuran.\n'
    fi
    printf 'Shortcut Desktop: %s/Recall.desktop\n' "$desktop_dir"
fi
printf 'Selesai. Tekan Super, ketik Recall, lalu Enter.\n'
printf 'Jika ikon tidak terlihat di permukaan desktop, gunakan menu aplikasi tersebut.\n'
printf 'Jalankan ulang skrip ini setelah memperbarui atau memindahkan project.\n'
