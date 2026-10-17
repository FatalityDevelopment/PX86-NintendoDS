#!/usr/bin/env bash
# Build the Px86 test ROMs with clang + ld.lld (no devkitPro needed).
#   tools/testroms/build.sh            → Px86.NintendoDS.TestProject/TestRoms/*.nds
# A test "foo" is src/foo9.c (ARM9) + optional src/foo7.c (ARM7, else an idle loop).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../../Px86.NintendoDS.TestProject/TestRoms"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$out"

CFLAGS="-O2 -marm -ffreestanding -nostdlib -fno-builtin -fno-exceptions -Wall -Werror -Wno-unused-command-line-argument"
cpu9="--target=armv5te-none-eabi -mcpu=arm946e-s"
cpu7="--target=armv4t-none-eabi -mcpu=arm7tdmi"

build_cpu() { # cpu-flags linkscript out.bin sources...
    local flags="$1" ld="$2" bin="$3"; shift 3
    local objs=()
    for src in "$here/crt0.s" "$here/runtime.c" "$@"; do
        local obj="$tmp/$(basename "$src").$(basename "$bin").o"
        clang $flags $CFLAGS -c "$src" -o "$obj"
        objs+=("$obj")
    done
    ld.lld -T "$here/$ld" "${objs[@]}" -o "$tmp/$(basename "$bin").elf"
    llvm-objcopy -O binary "$tmp/$(basename "$bin").elf" "$bin"
}

idle7="$tmp/idle7.c"
printf 'int main(void){for(;;){}}\n' > "$idle7"

for arm9 in "$here"/src/*9.c; do
    name="$(basename "$arm9" 9.c)"
    arm7="$here/src/${name}7.c"
    [ -f "$arm7" ] || arm7="$idle7"
    build_cpu "$cpu9" link9.ld "$tmp/$name.arm9.bin" "$arm9"
    build_cpu "$cpu7" link7.ld "$tmp/$name.arm7.bin" "$arm7"
    python3 "$here/mkrom.py" "$tmp/$name.arm9.bin" "$tmp/$name.arm7.bin" "$out/$name.nds" PXTS "$name"
    echo "built $out/$name.nds"
done
