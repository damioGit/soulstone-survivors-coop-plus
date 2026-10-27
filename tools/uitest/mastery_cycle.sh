#!/usr/bin/env bash
# Mastery co-op: guest fires the portal, the host kills bosses fast; when the guest advances a cycle, screenshot it
# (its Mastery power pick should be open) before picking. Prints both sides' state and the cycle logs.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
bash $D start 2>&1 | tail -1
bash $D A fight mastery; sleep 40
bash $D A cmd god >/dev/null; bash $D B cmd god >/dev/null
echo "== bars before portal"; bash $D A cmd bosses; bash $D B cmd bosses; bash $D A shot start >/dev/null; bash $D B shot start >/dev/null
bash $D B cmd tp -3.75 13.75 >/dev/null; sleep 8
seen=0
for i in $(seq 1 80); do
  bash $D A cmd bosses kill >/dev/null
  bash $D A cmd pick 0 >/dev/null
  n=$(bash $D B cmd masterysync | grep -o "cyclesApplied=[0-9]*" | cut -d= -f2)
  if [ "${n:-0}" -gt "$seen" ]; then
    seen=$n; sleep 1.5; bash $D B shot cyc$seen >/dev/null; bash $D A shot cyc$seen >/dev/null; sleep 5; bash $D B shot cyc${seen}b >/dev/null; bash $D A shot cyc${seen}b >/dev/null
    echo "guest advanced ($seen) at i=$i"
  fi
  bash $D B cmd pick 0 >/dev/null
  sleep 3
  c=$(bash $D A cmd mastery | grep -o "cycle=[0-9]*")
  [ "$c" = "cycle=3" ] && { echo "host cycle 3 at i=$i"; break; }
done
sleep 4
bash $D A cmd masterysync; bash $D B cmd masterysync
echo "== A"; bash $D A cmd mastery | cut -c1-110
echo "== B"; bash $D B cmd mastery | cut -c1-110
echo "== host cycle log"; grep -E "mastery\] (Host: cycle|trace AdvanceCycle)" "$G/LogOutput.log" | cut -c50-200
echo "== client cycle log"; grep -E "mastery] (Client|trace (AdvanceCycle|CastMastery|ResetPlayer))|Boss bar of" "$G/LogOutput.1.log" | cut -c50-200
echo "== exceptions"; grep -c "Exception" "$G/LogOutput.log" "$G/LogOutput.1.log"
