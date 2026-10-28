#!/usr/bin/env bash
# Titan Hunt (Crusade) walk-through on one side: lobby -> map choice -> keys -> door -> boss room -> boss -> blood -> portal.
# WHO=A (default) drives the host; info is printed at every step. START=0 skips launching the games.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
W=${WHO:-A}
info() { echo "-- $1 A: $(bash $D A cmd crusade info | cut -c1-400)"; echo "   $1 B: $(bash $D B cmd crusade info | cut -c1-400)"; echo "   B: $(bash $D B state | cut -c1-140)"; }
[ "${START:-1}" = "1" ] && { bash $D start 2>&1 | tail -1 | cut -c1-80; bash $D A fight crusade; sleep 25; }
for w in A B; do bash $D $w cmd god >/dev/null; bash $D $w cmd autopick >/dev/null; done
info lobby
bash $D $W cmd crusade portal; sleep 3; info panel
bash $D $W cmd crusade pick ${PICK:-0}; sleep 22; info map
bash $D $W cmd crusade keys; sleep 6; info keys
bash $D $W cmd crusade goto door; sleep 12; info door
bash $D $W shot r_door >/dev/null
bash $D $W cmd crusade goto boss; sleep 10; info bossroom
bash $D $W shot r_boss >/dev/null; bash $D B shot r_bossB >/dev/null
for i in $(seq 1 8); do bash $D A cmd killbosses >/dev/null; sleep 2; done
info killed
bash $D $W shot r_killed >/dev/null
sleep 4; info later
bash $D $W cmd crusade goto blood; sleep 6; info blood
bash $D $W cmd crusade goto portal; sleep 8; info portal
bash $D $W shot r_portal >/dev/null
sleep 10; info afterportal
bash $D $W shot r_after >/dev/null
bash $D $W cmd crusade pick 1; sleep 22; info map2
bash $D $W shot r_map2 >/dev/null; bash $D B shot r_map2B >/dev/null
echo "== trace"; grep -E "crusade\] trace" "$G/LogOutput.log" | cut -c40-230 | grep -v "Panel.Setup" | tail -40
echo "== client"; grep -E "crusade\]|\[run\]" "$G/LogOutput.1.log" | grep -v trace | cut -c40-230 | tail -25
echo "== exceptions"; grep -c "^Exception\|\[Error" "$G/LogOutput.log" "$G/LogOutput.1.log"
