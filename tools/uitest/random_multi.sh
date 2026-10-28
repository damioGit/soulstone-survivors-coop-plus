#!/usr/bin/env bash
# Random co-op runs: different characters for the host and the guest, different modes; ~90 s each with upgrades
# auto-picked, then both sides' state, the sync numbers and every exception/error line of both logs.
# COMBOS="hostChar guestChar mode ..." overrides the list.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
COMBOS=${COMBOS:-"Barbarian Necromancer survivor|Rogue Engineer mastery|Samurai Shaman crusade|Paladin Elementalist survivor|DeathKnight Pirate crusade|Bloodmage MonkeyKing mastery"}
IFS='|' read -ra LIST <<< "$COMBOS"
n=0
for combo in "${LIST[@]}"; do
  n=$((n+1)); set -- $combo; ha=$1; gb=$2; mode=$3
  echo "=================== $n: host $ha, guest $gb, $mode"
  bash $D start 2>&1 | tail -1 | cut -c1-60
  echo "chars: $(bash $D A cmd char $ha) / $(bash $D B cmd char $gb)"; sleep 3
  if [ "$mode" = survivor ]; then bash $D A fight; else bash $D A fight $mode; fi
  sleep 30
  for w in A B; do bash $D $w cmd god >/dev/null; bash $D $w cmd autopick >/dev/null; done
  if [ "$mode" = crusade ]; then bash $D A cmd crusade portal >/dev/null; sleep 3; bash $D A cmd crusade pick $((n % 3)) >/dev/null; sleep 25; fi
  for t in 1 2 3 4 5 6; do
    sleep 15
    echo "  t$t A: $(bash $D A state | grep -o 'scene=[^ ]*\|place=[^ ]*\|hp=[^ ]*' | tr '\n' ' ')| B: $(bash $D B state | grep -o 'scene=[^ ]*\|place=[^ ]*\|hp=[^ ]*' | tr '\n' ' ')"
  done
  bash $D A shot rm${n}A >/dev/null; bash $D B shot rm${n}B >/dev/null
  echo "  host sees: $(grep -o 'Hello from slot 1: [^ ]* [^ ]*' "$G/LogOutput.log" | tail -1) | guest sees: $(grep -o 'Hello from slot 0: [^ ]* [^ ]*' "$G/LogOutput.1.log" | tail -1)"
  echo "  puppets: $(grep -o 'avgErr=[^ ]* maxErr=[^ ]* teleports=[^ ]* unknownNetIds=[^ ]*' "$G/LogOutput.1.log" | tail -1)"
  echo "  enemies: $(grep -o 'Host: tracked=[^ ]* peak=[^ ]*' "$G/LogOutput.log" | tail -1)"
  echo "  exceptions: host $(grep -c '^Exception\|\[Error' "$G/LogOutput.log") guest $(grep -c '^Exception\|\[Error' "$G/LogOutput.1.log")"
  for f in LogOutput.log LogOutput.1.log; do
    grep -A4 '^Exception\|\[Error' "$G/$f" | grep -E '^  at |\[Error  :Soul' | cut -c1-140 | sort | uniq -c | sort -rn | head -4 | sed "s/^/    $f: /"
  done
  grep -h "Warning" "$G/LogOutput.log" "$G/LogOutput.1.log" | grep -v HarmonyX | cut -c1-150 | sort | uniq -c | sort -rn | head -5 | sed 's/^/    warn: /'
done
bash $D stop | tail -1
