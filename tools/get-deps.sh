#!/usr/bin/env bash
# Downloads build time mod dependencies listed in vendor_deps.json from the Vintage Story mod db.
# Zips land in libs/mods, top-level DLLs from each zip are extracted to libs.
# "game", "survival", "creative" and "essentials" are skipped, those ship with the install.
#
# Usage: ./tools/get-deps.sh [--deps <path>] [--out <path>] [--no-extract]
# Needs: curl, jq, unzip
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deps="$root/vendor_deps.json"
out="$root/libs"
extract=1

while [[ $# -gt 0 ]]; do
    case "$1" in
        --deps) deps="$2"; shift 2 ;;
        --out) out="$2"; shift 2 ;;
        --no-extract) extract=0; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

api_base='https://mods.vintagestory.at/api/mod'
built_in=' game survival creative essentials '

mkdir -p "$out/mods"

jq -r 'to_entries[] | "\(.key)\t\(.value)"' "$deps" | tr -d '\r' |
while IFS=$'\t' read -r modid wanted; do
    [[ "$built_in" == *" $modid "* ]] && continue

    echo "[$modid] wanted $wanted"
    if ! resp="$(curl -fsSL "$api_base/$modid")"; then
        echo "[$modid] mod db lookup failed" >&2
        continue
    fi

    # Releases come back newest first
    version="$(jq -r '.mod.releases[0].modversion // empty' <<<"$resp")"
    url="$(jq -r '.mod.releases[0].mainfile // empty' <<<"$resp")"
    if [[ -z "$version" || -z "$url" ]]; then
        echo "[$modid] no releases found" >&2
        continue
    fi

    if [[ -n "$wanted" && "$wanted" != "*" ]]; then
        lowest="$(printf '%s\n%s\n' "$wanted" "$version" | sort -V | head -n1)"
        [[ "$lowest" != "$wanted" ]] && echo "[$modid] latest $version is older than required $wanted" >&2
    fi

    zip="$out/mods/$modid-$version.zip"
    if [[ -f "$zip" ]]; then
        echo "[$modid] $version already downloaded"
    else
        echo "[$modid] downloading $version"
        curl -fsSL "$url" -o "$zip"
    fi

    [[ $extract -eq 0 ]] && continue

    tmp="$(mktemp -d)"
    unzip -q -o "$zip" -d "$tmp"
    for dll in "$tmp"/*.dll; do
        [[ -e "$dll" ]] || continue
        cp -f "$dll" "$out/"
        echo "[$modid] extracted $(basename "$dll") -> $out"
    done
    rm -rf "$tmp"
done
