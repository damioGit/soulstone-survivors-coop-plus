#!/usr/bin/env bash
# Spawn enemies next to the guest, then diff the active "(Clone)" objects (skill areas, projectiles, VFX) between
# host and guest a few times: what the host has and the guest doesn't is a skill the guest never sees.
#   v012_diff.sh Creeps/VoidPresence-Survivors Creeps/Slime-Elite ...
D=/i/soulstone-coop/tools/uitest/duo.sh
OUT=$USERPROFILE/soulstone-uitest
if [ -z "${KEEP:-}" ]; then
  bash $D start 2>&1 | tail -1
  bash $D A fight; sleep 40
  bash $D A cmd god >/dev/null; bash $D B cmd god >/dev/null
fi
for p in "$@"; do bash $D A cmd spawn $p; done
for i in $(seq 1 ${LOOPS:-8}); do
  sleep 3
  bash $D A cmd objs Clone all | tr '|' '\n' | sed 's/^ *//; s/ x[0-9]*$//; s/^[0-9]*: //' | sort -u > $OUT/objsA.txt
  bash $D B cmd objs Clone all | tr '|' '\n' | sed 's/^ *//; s/ x[0-9]*$//; s/^[0-9]*: //' | sort -u > $OUT/objsB.txt
  comm -23 $OUT/objsA.txt $OUT/objsB.txt | grep -v -i "RemoteProxy\|SoulstoneCoop\|^$" >> $OUT/onlyA.txt
done
echo "== only on the host (count of samples)"; sort $OUT/onlyA.txt | uniq -c | sort -rn | head -60
rm -f $OUT/onlyA.txt
