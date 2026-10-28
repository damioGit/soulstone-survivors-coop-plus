#!/usr/bin/env bash
# 0.11.0: Slayer and Avatar of Fire fight the players until one uses its channel (Bladestorm / Flamethrower) on its
# own; the guest's replay must end with the host's.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
bash $D start 2>&1 | tail -1 | cut -c1-40
bash $D A fight; sleep 30
for w in A B; do bash $D $w cmd god >/dev/null; done
bash $D A cmd spawn Bosses/Slayer; bash $D A cmd spawn Bosses/Avatar-of-Fire
for t in $(seq 1 100); do
  grep -q "Bladestorm-Skill' (#[0-9]*) ended\|Flamethrower-Skill' (#[0-9]*) ended" "$G/LogOutput.1.log" && break
  sleep 5
done
grep -h "Bladestorm\|Flamethrower\|ended by the host" "$G/LogOutput.log" "$G/LogOutput.1.log" | cut -c40-230 | sort -u | head -8
echo "after ${t}x5s"
echo "exceptions: host $(grep -c '^Exception\|\[Error' "$G/LogOutput.log") guest $(grep -c '^Exception\|\[Error' "$G/LogOutput.1.log")"
for f in LogOutput.log LogOutput.1.log; do grep -A4 '^Exception\|\[Error' "$G/$f" | grep -E '^  at |\[Error  :Soul' | cut -c1-160 | sort | uniq -c | head -4; done
