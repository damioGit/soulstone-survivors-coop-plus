#!/usr/bin/env bash
# Titan Hunt, the guest drives: it walks into the portals, proposes a titan, opens the door and the boss room, takes
# the titan blood; the host only presses the titan. Checks both sides after each step.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
info() { echo "-- $1 A: $(bash $D A cmd crusade info | cut -c1-300)"; echo "   $1 B: $(bash $D B cmd crusade info | cut -c1-300)"; }
[ "${START:-1}" = "1" ] && { bash $D start 2>&1 | tail -1 | cut -c1-80; bash $D A fight crusade; sleep 25; }
for w in A B; do bash $D $w cmd god >/dev/null; bash $D $w cmd autopick >/dev/null; done
bash $D B cmd crusade goto unstable; sleep 5; info "guest portal"
bash $D A shot q_panelA >/dev/null; bash $D B shot q_panelB >/dev/null
bash $D B cmd crusade pick 1; sleep 2
bash $D A cmd crusade pick 1; sleep 22; info map
for w in A B; do bash $D $w cmd god >/dev/null; done
bash $D A cmd crusade keys >/dev/null; sleep 6
bash $D A cmd tp -60 -60 >/dev/null
bash $D B cmd crusade goto door; sleep 12; info "guest door"
bash $D B cmd crusade goto boss; sleep 10; info "guest boss room"
for i in $(seq 1 8); do bash $D A cmd killbosses >/dev/null; sleep 3; done
bash $D B cmd crusade goto blood; sleep 6; info "guest blood"
bash $D B cmd crusade goto portal; sleep 6; info "guest exit portal"
bash $D B shot q_exitB >/dev/null; bash $D A shot q_exitA >/dev/null
bash $D A cmd crusade pick 0; sleep 22; info map2
echo "== host"; grep -E "crusade\]" "$G/LogOutput.log" | grep -v trace | cut -c40-200 | uniq | tail -14
echo "== client"; grep -E "crusade\]" "$G/LogOutput.1.log" | grep -v trace | cut -c40-200 | uniq | tail -16
echo "== exceptions"; grep -c "^Exception\|\[Error" "$G/LogOutput.log" "$G/LogOutput.1.log"
