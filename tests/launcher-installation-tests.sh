#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
test_dir=$(mktemp -d /tmp/recall-launcher-tests-XXXXXX)
trap 'rm -rf -- "$test_dir"' EXIT
fixture_project="$test_dir/project"
fake_bin="$test_dir/bin"
desktop_dir="$test_dir/Desktop"
mkdir -p -- "$fixture_project/scripts" "$fixture_project/packaging/linux" \
    "$fixture_project/src/Recall.Desktop" "$fake_bin"
cp -- "$project_root/scripts/install-launcher.sh" "$fixture_project/scripts/"
cp -- "$project_root/packaging/linux/recall.svg" "$fixture_project/packaging/linux/"
printf '<Project />\n' > "$fixture_project/src/Recall.Desktop/Recall.Desktop.csproj"

cat > "$fake_bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
[[ "${1:-}" == publish ]]
output=''
while (( $# )); do
    case "$1" in
        --output) output=$2; shift 2 ;;
        *) shift ;;
    esac
done
[[ -n "$output" ]]
mkdir -p -- "$output"
cat > "$output/Recall.Desktop" <<APP
#!/usr/bin/env bash
printf '%s\n' 'Recall fixture ${RECALL_TEST_BUILD_TAG:?}'
APP
chmod 755 -- "$output/Recall.Desktop"
SH

cat > "$fake_bin/xdg-user-dir" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$RECALL_TEST_DESKTOP"
SH
cat > "$fake_bin/gio" <<'SH'
#!/usr/bin/env bash
exit 1
SH
cat > "$fake_bin/update-desktop-database" <<'SH'
#!/usr/bin/env bash
exit 0
SH
cat > "$fake_bin/cp" <<'SH'
#!/usr/bin/env bash
if [[ "${RECALL_TEST_FAIL_COPY:-0}" == 1 ]]; then exit 73; fi
exec /usr/bin/cp "$@"
SH
cat > "$fake_bin/mv" <<'SH'
#!/usr/bin/env bash
arguments=("$@")
source_path=${arguments[${#arguments[@]}-2]}
target_path=${arguments[${#arguments[@]}-1]}
if [[ "${RECALL_TEST_INTERRUPT_SWAP:-0}" == 1 \
    && "$source_path" == "$RECALL_TEST_APP_DIR" && "$target_path" == */.app-backup-*/app ]]; then
    /usr/bin/mv "$@"
    kill -TERM "$PPID"
    exit 0
fi
if [[ "${RECALL_TEST_FAIL_SWAP:-0}" == 1 \
    && "$source_path" == */.app-staging-* && "$target_path" == "$RECALL_TEST_APP_DIR" ]]; then
    exit 73
fi
exec /usr/bin/mv "$@"
SH
chmod 755 -- "$fake_bin/"*

data_home="$test_dir/prepare-data"
app_dir="$data_home/recall/app"
run_installer() {
    local tag=$1
    shift
    PATH="$fake_bin:$PATH" XDG_DATA_HOME="$data_home" \
        RECALL_TEST_BUILD_TAG="$tag" RECALL_TEST_DESKTOP="$desktop_dir" \
        RECALL_TEST_APP_DIR="$app_dir" RECALL_TEST_FAIL_COPY="${RECALL_TEST_FAIL_COPY:-0}" \
        RECALL_TEST_FAIL_SWAP="${RECALL_TEST_FAIL_SWAP:-0}" \
        RECALL_TEST_INTERRUPT_SWAP="${RECALL_TEST_INTERRUPT_SWAP:-0}" \
        bash "$fixture_project/scripts/install-launcher.sh" "$@" > "$test_dir/installer-output.txt" 2>&1
}

run_installer prepare --prepare-only
[[ ! -e "$data_home" && ! -e "$desktop_dir" ]]
[[ -x "$fixture_project/artifacts/linux/Recall.Desktop" ]]
printf 'PASS: prepare-only tidak memasang aplikasi atau launcher\n'

data_home="$test_dir/data with spaces %"
app_dir="$data_home/recall/app"
mkdir -p -- "$data_home/recall"
printf 'database sentinel\n' > "$data_home/recall/recall.db"
printf 'log sentinel\n' > "$data_home/recall/recall.log"
run_installer first
[[ "$("$app_dir/Recall.Desktop")" == 'Recall fixture first' ]]
[[ -f "$app_dir/recall.svg" && -f "$app_dir/.recall-managed-app" ]]
cmp -- "$fixture_project/packaging/linux/recall.svg" "$app_dir/recall.svg"
cmp -- "$data_home/applications/Recall.desktop" "$desktop_dir/Recall.desktop"
[[ $(stat -c '%a' "$desktop_dir/Recall.desktop") == 755 ]]
entry_exec=''
entry_icon=''
entry_terminal=''
entry_wm_class=''
while IFS='=' read -r key value; do
    case "$key" in
        Exec) entry_exec=$value ;;
        Icon) entry_icon=$value ;;
        Terminal) entry_terminal=$value ;;
        StartupWMClass) entry_wm_class=$value ;;
    esac
done < "$data_home/applications/Recall.desktop"
[[ "$entry_exec" == "\"$test_dir/data with spaces %%/recall/app/Recall.Desktop\"" ]]
[[ "$entry_icon" == "$app_dir/recall.svg" ]]
[[ "$entry_terminal" == false && "$entry_wm_class" == Recall ]]
printf 'PASS: binary, ikon, dan launcher dipasang pada lokasi pengguna\n'

mv -- "$fixture_project" "$test_dir/project-moved"
fixture_project="$test_dir/project-moved"
[[ "$("$app_dir/Recall.Desktop")" == 'Recall fixture first' ]]
printf 'PASS: aplikasi terpasang tetap berjalan setelah project dipindahkan\n'

run_installer second
[[ "$("$app_dir/Recall.Desktop")" == 'Recall fixture second' ]]
[[ $(cat -- "$data_home/recall/recall.db") == 'database sentinel' ]]
[[ $(cat -- "$data_home/recall/recall.log") == 'log sentinel' ]]
printf 'PASS: pembaruan mengganti aplikasi dan mempertahankan database/log\n'

if RECALL_TEST_FAIL_COPY=1 run_installer failed-copy; then
    printf 'FAIL: salinan yang gagal seharusnya menghentikan instalasi\n' >&2; exit 1
fi
[[ "$("$app_dir/Recall.Desktop")" == 'Recall fixture second' ]]
if RECALL_TEST_FAIL_SWAP=1 run_installer failed-swap; then
    printf 'FAIL: pergantian yang gagal seharusnya menghentikan instalasi\n' >&2; exit 1
fi
[[ "$("$app_dir/Recall.Desktop")" == 'Recall fixture second' ]]
[[ $(cat -- "$data_home/recall/recall.db") == 'database sentinel' ]]
[[ $(cat -- "$data_home/recall/recall.log") == 'log sentinel' ]]
[[ -z $(find "$data_home/recall" -maxdepth 1 -name '.app-*' -print -quit) ]]
printf 'PASS: kegagalan salin/pergantian memulihkan instalasi sebelumnya\n'

if RECALL_TEST_INTERRUPT_SWAP=1 run_installer interrupted; then
    printf 'FAIL: installer yang dihentikan seharusnya melaporkan kegagalan\n' >&2; exit 1
fi
[[ "$("$app_dir/Recall.Desktop")" == 'Recall fixture second' ]]
[[ -z $(find "$data_home/recall" -maxdepth 1 -name '.app-*' -print -quit) ]]
printf 'PASS: installer yang terhenti mengembalikan aplikasi sebelumnya\n'

data_home="$test_dir/unmanaged-data"
app_dir="$data_home/recall/app"
mkdir -p -- "$app_dir"
printf 'keep original\n' > "$app_dir/original.txt"
if run_installer unmanaged; then
    printf 'FAIL: folder tanpa penanda kepemilikan seharusnya ditolak\n' >&2; exit 1
fi
[[ $(cat -- "$app_dir/original.txt") == 'keep original' ]]
printf 'PASS: folder target yang tidak dikenali tidak ditimpa\n'

data_home="$test_dir/linked-data"
app_dir="$data_home/recall/app"
mkdir -p -- "$data_home/recall" "$test_dir/originals"
printf 'keep linked target\n' > "$test_dir/originals/original.txt"
ln -s -- "$test_dir/originals" "$app_dir"
if run_installer linked; then
    printf 'FAIL: target instalasi symbolic link seharusnya ditolak\n' >&2; exit 1
fi
[[ $(cat -- "$test_dir/originals/original.txt") == 'keep linked target' ]]
printf 'PASS: symbolic link tidak diikuti sebagai target instalasi\n'
