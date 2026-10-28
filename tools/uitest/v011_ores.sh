#!/usr/bin/env bash
# 0.11.0: ores / healing crystals reach the guest (puppet + minimap icon), the guest breaks one and gets its materials.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
bash $D start 2>&1 | tail -1 | cut -c1-40
bash $D A fight; sleep 45
for w in A B; do bash $D $w cmd god >/dev/null; done
echo "A: $(bash $D A cmd find Ore)"; echo "B: $(bash $D B cmd find Ore)"
sleep 3
echo "host icons: $(bash $D A cmd mmicons)"
echo "guest icons: $(bash $D B cmd mmicons)"
echo "guest sync: $(bash $D B cmd mmsync)"
bash $D A cmd spawn Bosses/Slayer
sleep 60
echo "B after: $(bash $D B cmd find Ore)"
echo "guest icons later: $(bash $D B cmd mmicons)"
grep -h "mmicons\]\|non-enemy-team\|First 'res:Minerals\|Bladestorm" "$G/LogOutput.log" "$G/LogOutput.1.log" | cut -c40-230 | sort -u | head -8
echo "exceptions: host $(grep -c '^Exception\|\[Error' "$G/LogOutput.log") guest $(grep -c '^Exception\|\[Error' "$G/LogOutput.1.log")"
for f in LogOutput.log LogOutput.1.log; do grep -A4 '^Exception\|\[Error' "$G/$f" | grep -E '^  at |\[Error  :Soul' | cut -c1-160 | sort | uniq -c | head -4; done
