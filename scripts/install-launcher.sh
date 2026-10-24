#!/usr/bin/env bash
set -euo pipefail

# Instalasi per-user: binary dan ikon disalin keluar dari folder project.
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
data_home=${XDG_DATA_HOME:-"$HOME/.local/share"}
[[ "$data_home" == /* ]] || data_home="$HOME/.local/share"
recall_dir="$data_home/recall"
app_dir="$recall_dir/app"
app_path="$app_dir/Recall.Desktop"
icon_path="$app_dir/recall.svg"
ownership_marker='.recall-managed-app'
ownership_value='Recall managed application v1'
staging_dir=''
backup_dir=''

restore_previous_installation() {
    if [[ -n "$backup_dir" && -d "$backup_dir/app" && ! -e "$app_dir" && ! -L "$app_dir" ]]; then
        if mv -T -- "$backup_dir/app" "$app_dir"; then
            rmdir -- "$backup_dir"
            backup_dir=''
            printf 'Instalasi Recall sebelumnya sudah dipulihkan.\n' >&2
        else
            printf 'Instalasi sebelumnya masih tersedia di: %s/app\n' "$backup_dir" >&2
        fi
    fi
}

cleanup_staging() {
    restore_previous_installation
    if [[ -n "$staging_dir" && -d "$staging_dir" ]]; then
        rm -rf -- "$staging_dir"
    fi
}
trap cleanup_staging EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

check_app_target() {
    if [[ -e "$app_dir" || -L "$app_dir" ]]; then
        if [[ -L "$app_dir" || ! -d "$app_dir" \
            || -L "$app_dir/$ownership_marker" || ! -f "$app_dir/$ownership_marker" \
            || "$(cat -- "$app_dir/$ownership_marker")" != "$ownership_value" ]]; then
            printf 'Folder target bukan instalasi Recall yang dikenali dan tidak akan diubah: %s\n' "$app_dir" >&2
            return 1
        fi
    fi
}

command -v dotnet >/dev/null || { printf '.NET SDK diperlukan untuk membangun Recall.\n' >&2; exit 1; }
if [[ "$app_path" == *'='* ]]; then
    printf 'Lokasi instalasi tidak boleh mengandung tanda = untuk launcher desktop.\n' >&2
    exit 1
fi
check_app_target

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
printf 'Membangun Recall. Tutup Recall terlebih dahulu jika sedang berjalan.\n'
dotnet publish "$project_dir/src/Recall.Desktop/Recall.Desktop.csproj" \
    --configuration Release --no-restore --self-contained false \
    --disable-build-servers -m:1 --output "$publish_dir"
[[ -x "$publish_dir/Recall.Desktop" ]] || { printf 'Executable Recall tidak ditemukan.\n' >&2; exit 1; }
[[ -f "$project_dir/packaging/linux/recall.svg" ]] || { printf 'Ikon Recall tidak ditemukan.\n' >&2; exit 1; }

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
    printf 'Build dan launcher siap: %s\nBelum dipasang ke: %s\n' "$launcher_path" "$app_dir"
    exit 0
fi

# Hanya subfolder app yang diganti; database, log, dan konfigurasi tidak disentuh.
install -d -m 700 -- "$recall_dir"
staging_dir=$(mktemp -d "$recall_dir/.app-staging-XXXXXX")
cp -a -- "$publish_dir/." "$staging_dir/"
install -m 644 -- "$project_dir/packaging/linux/recall.svg" "$staging_dir/recall.svg"
printf '%s\n' "$ownership_value" > "$staging_dir/$ownership_marker"
[[ -x "$staging_dir/Recall.Desktop" ]] || { printf 'Executable hasil salinan tidak valid.\n' >&2; exit 1; }
check_app_target
if [[ -d "$app_dir" ]]; then
    backup_dir=$(mktemp -d "$recall_dir/.app-backup-XXXXXX")
    mv -- "$app_dir" "$backup_dir/app"
fi
if ! mv -T -- "$staging_dir" "$app_dir"; then
    printf 'Pembaruan aplikasi gagal.\n' >&2
    restore_previous_installation
    exit 1
fi
staging_dir=''
if [[ -n "$backup_dir" ]]; then
    rm -rf -- "$backup_dir"
fi

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
printf 'Aplikasi terpasang di: %s\n' "$app_dir"
printf 'Jika ikon tidak terlihat di permukaan desktop, gunakan menu aplikasi tersebut.\n'
printf 'Folder project boleh dipindahkan; launcher memakai salinan aplikasi yang terpasang.\n'
printf 'Jalankan ulang skrip ini setelah memperbarui kode Recall.\n'
