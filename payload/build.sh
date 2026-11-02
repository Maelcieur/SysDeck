#!/bin/bash
# Compile le payload avec devkitARM. Usage : build.sh [clean]
# La BDK de hekate (version fixée ci-dessous) est téléchargée dans ../third_party/hekate si absente.
HEKATE_VERSION=v6.5.4

export DEVKITPRO=/opt/devkitpro DEVKITARM=/opt/devkitpro/devkitARM
export PATH=/opt/devkitpro/devkitARM/bin:/opt/devkitpro/tools/bin:$PATH
cd "$(dirname "$0")" || exit 1

if [ ! -d ../third_party/hekate/bdk ]; then
	git clone --depth 1 --branch "$HEKATE_VERSION" https://github.com/CTCaer/hekate.git ../third_party/hekate || exit 1
fi

make -j8 "$@" 2>&1 | grep -vE "^CC |^AS "
exit ${PIPESTATUS[0]}
