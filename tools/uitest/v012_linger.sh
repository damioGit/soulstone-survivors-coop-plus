#!/usr/bin/env bash
# Lingering enemy skills: spawn an enemy next to the guest and compare the objects named like its skill on both sides.
#   v012_linger.sh Creeps/SandElemental-Elite Tornado
D=/i/soulstone-coop/tools/uitest/duo.sh
P=${1:-Creeps/SandElemental-Elite}; N=${2:-Tornado}
if [ -z "${KEEP:-}" ]; then
  bash $D start 2>&1 | tail -1
  bash $D A fight; sleep 40
  bash $D A cmd god >/dev/null; bash $D B cmd god >/dev/null
fi
bash $D A cmd spawn $P
for i in $(seq 1 ${LOOPS:-12}); do
  sleep 2
  echo "A $(bash $D A cmd objs $N | head -c 400)"
  echo "B $(bash $D B cmd objs $N | head -c 400)"
  [ "$i" = "${SHOT:-6}" ] && { bash $D A shot linger >/dev/null; bash $D B shot linger >/dev/null; }
done
