#!/usr/bin/env bash
# Checks the features taken from the author's 1.1.0: party panel (both sides), the compact upgrade panel at the top
# with the pad hint (PauseOnLevelUp off), and the guest seeing every enemy type the host spawns (#6).
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
bash $D start 2>&1 | tail -1 | cut -c1-60
for w in A B; do bash $D $w cmd options 5 | grep -o "opt.levelup_pause=[^ ]*"; done
bash $D A fight; sleep 30
for w in A B; do bash $D $w cmd god >/dev/null; done
sleep 30
bash $D A shot u_partyA >/dev/null; bash $D B shot u_partyB >/dev/null
bash $D A cmd levelup; sleep 3
bash $D A shot u_levelA >/dev/null; bash $D B shot u_levelB >/dev/null
bash $D A cmd pick 0 >/dev/null
sleep 60
echo "host enemies: $(grep -o 'Host: tracked=[^ ]* peak=[^ ]*' "$G/LogOutput.log" | tail -1)"
echo "guest puppets: $(grep -o 'alive=[^ ]*\|unknownNetIds=[^ ]*' "$G/LogOutput.1.log" | tail -2 | tr '\n' ' ')"
echo "guest missing types: $(grep -ci "no prefab\|unknown key\|can't resolve\|could not resolve\|not found" "$G/LogOutput.1.log")"
grep -i "resolve\|tier list\|prefab type\|#6" "$G/LogOutput.1.log" | cut -c40-200 | sort | uniq -c | head -8
echo "party: $(grep -i 'party' "$G/LogOutput.log" | cut -c40-180 | tail -3)"
echo "exceptions: host $(grep -c '^Exception\|\[Error' "$G/LogOutput.log") guest $(grep -c '^Exception\|\[Error' "$G/LogOutput.1.log")"
for f in LogOutput.log LogOutput.1.log; do grep -A4 '^Exception\|\[Error' "$G/$f" | grep -E '^  at |\[Error  :Soul' | cut -c1-140 | sort | uniq -c | head -4; done
