#!/usr/bin/env bash
# Continue a running Mastery co-op test to the end: kill bosses on the host, pick on both, report state changes.
D=/i/soulstone-coop/tools/uitest/duo.sh
last=""
for i in $(seq 1 120); do
  bash $D A cmd bosses kill >/dev/null
  bash $D A cmd pick 0 >/dev/null; bash $D B cmd pick 0 >/dev/null
  sleep 3
  a=$(bash $D A state | grep -o "scene=[^ ]*\|place=[^ ]*" | paste -sd' ')
  am=$(bash $D A cmd mastery | grep -o "cycle=[0-9/]*\|mainBoss=[^ ]*" | paste -sd' ')
  b=$(bash $D B state | grep -o "scene=[^ ]*\|place=[^ ]*" | paste -sd' ')
  bm=$(bash $D B cmd bosses | head -c 120)
  cur="A[$a $am] B[$b]"
  if [ "$cur" != "$last" ]; then echo "i=$i $cur | B bosses: $bm"; last=$cur; bash $D A shot fin$i >/dev/null; bash $D B shot fin$i >/dev/null; fi
  case "$a" in *Menu*) echo "host back in menu"; break;; esac
done
sleep 3
bash $D A state | cut -c1-120; bash $D B state | cut -c1-120
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
grep -E "\[life\].*(Victory|RunEnd|run end|EndRun)|RunEnd" "$G/LogOutput.log" | tail -3 | cut -c1-200
grep -E "\[life\].*(Victory|RunEnd|run end|EndRun)|RunEnd" "$G/LogOutput.1.log" | tail -3 | cut -c1-200
echo "== exceptions"; grep -c "Exception" "$G/LogOutput.log" "$G/LogOutput.1.log"
