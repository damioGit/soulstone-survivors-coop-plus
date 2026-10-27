#!/usr/bin/env bash
# Fast Mastery co-op run to the end: upgrades auto-picked, all bosses killed by the host every 2 s.
# Prints state changes, screenshots both windows on each change, and the logs that matter.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
bash $D start 2>&1 | tail -1
bash $D A fight mastery; sleep 35
for w in A B; do bash $D $w cmd god >/dev/null; bash $D $w cmd autopick >/dev/null; done
bash $D B cmd tp -3.75 13.75 >/dev/null
last=""
for i in $(seq 1 150); do
  bash $D A cmd killbosses >/dev/null
  sleep 2
  a=$(bash $D A state | grep -o "scene=[^ ]*\|pos=([^)]*)" | paste -sd' ')
  am=$(bash $D A cmd mastery | grep -o "cycle=[0-9/]*" )
  b=$(bash $D B state | grep -o "scene=[^ ]*\|pos=([^)]*)" | paste -sd' ')
  [ -z "$b" ] && b="NO REPLY"
  cur="A[$am] B[${b%% pos*}]"
  if [ "$cur" != "$last" ]; then echo "i=$i A[$a $am] B[$b]"; last=$cur; bash $D A shot f$i >/dev/null; bash $D B shot f$i >/dev/null; fi
  case "$a" in *Menu*) echo "host in menu"; break;; esac
  case "$am" in cycle=4*) ;; esac
done
echo "== client finale"; grep -E "mastery\] Client: (finale|pulled|main boss)|Victory|RunEnd" "$G/LogOutput.1.log" | cut -c50-220
echo "== host finale"; grep -E "mastery\] Host: finale|Victory|RunEnd" "$G/LogOutput.log" | cut -c50-200 | tail -4
echo "== exceptions"; grep -c "^Exception" "$G/LogOutput.log" "$G/LogOutput.1.log"
