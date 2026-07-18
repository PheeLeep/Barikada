#!/usr/bin/env bash
#
# add_scatters.sh — add scatter site URLs to all three Barikada blocklists.
#
# Takes URLs or bare domains, normalizes them, drops ones already listed, and
# inserts the rest under the "(updated Last ...)" marker in each list, bumping
# that marker to today's date.
#
# Usage:
#   ./add_scatters.sh https://foo.ph/ bar.com www.baz.vip
#   ./add_scatters.sh -f new_sites.txt
#   pbpaste | ./add_scatters.sh
#   ./add_scatters.sh --dry-run foo.ph      # preview, write nothing
#
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UBLOCK="$REPO/anti_scatter.txt"
HOSTS="$REPO/barikada_hosts.txt"
UBLACKLIST="$REPO/ublacklist_antiscatter.txt"

DRY_RUN=0
inputs=()

usage() { sed -n '3,15p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'; exit "${1:-0}"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        -f|--file)
            [[ -r "${2:-}" ]] || { echo "error: cannot read file '${2:-}'" >&2; exit 1; }
            while IFS= read -r line; do inputs+=("$line"); done < "$2"
            shift 2
            ;;
        -n|--dry-run) DRY_RUN=1; shift ;;
        -h|--help)    usage 0 ;;
        -*)           echo "error: unknown option '$1'" >&2; usage 1 ;;
        *)            inputs+=("$1"); shift ;;
    esac
done

# No arguments and stdin is a pipe: read the list from stdin.
if [[ ${#inputs[@]} -eq 0 && ! -t 0 ]]; then
    while IFS= read -r line; do inputs+=("$line"); done
fi
[[ ${#inputs[@]} -gt 0 ]] || usage 1

for f in "$UBLOCK" "$HOSTS" "$UBLACKLIST"; do
    [[ -w "$f" ]] || { echo "error: missing or unwritable list: $f" >&2; exit 1; }
done

# --- normalize -------------------------------------------------------------
# https://www.Foo.PH:8443/path?x=1 -> foo.ph
normalize() {
    local d="${1%%[[:space:]]*}"
    d="${d,,}"
    d="${d#*://}"       # scheme
    d="${d#*@}"         # userinfo
    d="${d%%/*}"        # path
    d="${d%%\?*}"       # stray query with no path
    d="${d%%:*}"        # port
    d="${d#www.}"       # www prefix (lists store the bare domain)
    d="${d%.}"          # trailing root dot
    printf '%s' "$d"
}

# --- existing entries ------------------------------------------------------
# Union of all three lists, so a domain present in any one is treated as known.
declare -A known=()
while IFS= read -r d; do [[ -n "$d" ]] && known["$d"]=1; done < <(
    sed -n 's/^||\(.*\)\^\$all$/\1/p'   "$UBLOCK"
    sed -n 's/^0\.0\.0\.0 \(.*\)$/\1/p' "$HOSTS" | sed 's/^www\.//'
    # Both the dotted form and the 41 legacy dotless "*://*foo.com/*" entries.
    sed -n 's|^\*://\*\.\{0,1\}\(.*\)/\*$|\1|p' "$UBLACKLIST"
)

# --- validate, dedupe ------------------------------------------------------
new=()
declare -A seen=()
skipped_dupe=0 skipped_bad=0

for raw in "${inputs[@]}"; do
    raw="${raw#"${raw%%[![:space:]]*}"}"          # trim leading space
    [[ -z "$raw" || "$raw" == \#* ]] && continue  # blanks and comments
    d="$(normalize "$raw")"

    if [[ ! "$d" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]]; then
        echo "  skip (not a domain): $raw" >&2
        (( ++skipped_bad )); continue
    fi
    if [[ -n "${known[$d]:-}" ]]; then
        echo "  skip (already listed): $d" >&2
        (( ++skipped_dupe )); continue
    fi
    if [[ -n "${seen[$d]:-}" ]]; then
        (( ++skipped_dupe )); continue
    fi
    seen["$d"]=1
    new+=("$d")
done

if [[ ${#new[@]} -eq 0 ]]; then
    echo "Nothing to add (${skipped_dupe} duplicate, ${skipped_bad} invalid)."
    exit 0
fi

TODAY="$(date '+%B %-d, %Y')"

echo "Adding ${#new[@]} domain(s), marking lists as \"$TODAY\":"
printf '  + %s\n' "${new[@]}"
[[ $skipped_dupe -gt 0 || $skipped_bad -gt 0 ]] &&
    echo "  (${skipped_dupe} duplicate, ${skipped_bad} invalid skipped)"

if [[ $DRY_RUN -eq 1 ]]; then
    echo
    echo "Dry run — no files written. Entries would be:"
    for d in "${new[@]}"; do
        printf '  %-28s %-24s %s\n' "||$d^\$all" "0.0.0.0 $d" "*://*.$d/*"
    done
    exit 0
fi

# --- insert ----------------------------------------------------------------
# Rewrites the "(updated Last <date>)" marker line and drops the new entries
# directly beneath it, matching how the lists have been maintained by hand.
insert() {
    local file="$1" comment="$2" fmt="$3"
    local tmp; tmp="$(mktemp)"

    NEW_ENTRIES="$(printf "$fmt\n" "${new[@]}")" \
    awk -v comment="$comment" -v today="$TODAY" '
        !done && $0 ~ "^" comment " \\(updated Last .*\\)$" {
            print comment " (updated Last " today ")"
            print ENVIRON["NEW_ENTRIES"]
            done = 1
            next
        }
        { print }
        END { if (!done) exit 3 }
    ' "$file" > "$tmp" || {
        rm -f "$tmp"
        echo "error: no \"$comment (updated Last ...)\" marker found in $file" >&2
        exit 1
    }

    mv "$tmp" "$file"
    echo "  updated $(basename "$file")"
}

insert "$UBLOCK"     '!' '||%s^$all'
insert "$HOSTS"      '#' '0.0.0.0 %s'
insert "$UBLACKLIST" '#' '*://*.%s/*'

echo
echo "Done. Review with: git -C \"$REPO\" diff"
