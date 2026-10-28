#!/usr/bin/env bash
# 0.11.0: shared XP pool with the team's XP bonus, minimap icons of minerals / healing crystals on the guest,
# continuous enemy attacks (Slayer's Bladestorm) kept on the guest until the host's end, camera zoom, enemy hp bonus.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
bash $D start 2>&1 | tail -1 | cut -c1-60
bash $D A fight; sleep 30
for w in A B; do bash $D $w cmd god >/dev/null; done
echo "--- xp"
bash $D B cmd xpmod 1
bash $D B cmd pickup 10 >/dev/null; sleep 2.5
bash $D A cmd xpmult
echo "A before: $(bash $D A cmd xp)"; echo "B before: $(bash $D B cmd xp)"
bash $D A cmd pickup 1000 >/dev/null; sleep 1
echo "A after host pickup: $(bash $D A cmd xp)"; echo "B after host pickup: $(bash $D B cmd xp)"
bash $D B cmd pickup 1000 >/dev/null; sleep 1
echo "A after guest pickup: $(bash $D A cmd xp)"; echo "B after guest pickup: $(bash $D B cmd xp)"
echo "--- slayer"
bash $D A cmd spawn Bosses/Slayer
sleep 40
echo "--- minimap"
echo "host: $(bash $D A cmd mmicons)"
echo "guest: $(bash $D B cmd mmicons)"
echo "guest sync: $(bash $D B cmd mmsync)"
echo "--- levels after play"
echo "A: $(bash $D A cmd xp)"; echo "B: $(bash $D B cmd xp)"
grep -h "Bladestorm" "$G/LogOutput.log" "$G/LogOutput.1.log" | cut -c40-220 | sort -u | head -4
echo "host ends: $(grep -o 'channelEnds=[^ ]* openChannels=[^ ]*' "$G/LogOutput.log" | tail -3 | tr '\n' ' ')"
echo "guest ends: $(grep -o 'channelEnds=[^ ]* openChannels=[^ ]*' "$G/LogOutput.1.log" | tail -3 | tr '\n' ' ')"
grep -h "mmicons\]\|zoomed out\|hpbonus\]" "$G/LogOutput.log" "$G/LogOutput.1.log" | cut -c40-200 | sort -u | head -6
echo "exceptions: host $(grep -c '^Exception\|\[Error' "$G/LogOutput.log") guest $(grep -c '^Exception\|\[Error' "$G/LogOutput.1.log")"
for f in LogOutput.log LogOutput.1.log; do grep -A4 '^Exception\|\[Error' "$G/$f" | grep -E '^  at |\[Error  :Soul' | cut -c1-160 | sort | uniq -c | head -4; done
