#!/usr/bin/env bash
# Mastery finale test: bosses killed fast up to the last cycle, then left alone so the main boss can pull the
# players into its arena; screenshots of both, then the boss is killed and the victory checked on both sides.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
pos() { bash $D $1 state | grep -o "pos=([^)]*)" | tr -d 'pos=()' ; }
far() { py -3 -c "import sys; x,y,z=[float(v) for v in '$1'.replace(',', ' ').split()[:3]] if '$1' else (0,0,0); print(1 if (x*x+z*z)**0.5>60 else 0)" 2>/dev/null; }
bash $D start 2>&1 | tail -1
bash $D A fight mastery; sleep 35
for w in A B; do bash $D $w cmd god >/dev/null; bash $D $w cmd autopick >/dev/null; done
bash $D B cmd tp -3.75 13.75 >/dev/null
for i in $(seq 1 120); do
  bash $D A cmd killbosses >/dev/null; sleep 2
  c=$(bash $D A cmd mastery | grep -o "cycle=[0-9]*")
  [ "$c" = "cycle=4" ] && { echo "last cycle at i=$i"; break; }
done
echo "== waiting for the pull (one hit to push the boss's phase)"
sleep 15; bash $D A cmd killbosses
for i in $(seq 1 60); do
  sleep 2
  a=$(pos A)
  if [ "$(far "$a")" = "1" ]; then echo "host pulled to $a after $((i*2)) s"; break; fi
done
sleep 4; bash $D A shot pullA >/dev/null; bash $D B shot pullB >/dev/null
echo "host $(pos A) | guest $(pos B)"
sleep 6; bash $D B shot pullB2 >/dev/null
echo "guest after 10 s: $(pos B)"
for i in $(seq 1 10); do bash $D A cmd killbosses >/dev/null; sleep 2; done
sleep 4; bash $D A shot winA >/dev/null; bash $D B shot winB >/dev/null
echo "== client"; grep -E "mastery] Client: (finale|pulled|main boss|ground|fell|no |loading|scene)|Victory|RunEnd|phases skipped|survived" "$G/LogOutput.1.log" | grep -v 'scene changes' | cut -c50-220
echo "== host"; grep -E "mastery] Host: (finale|scene|watching|phase)|Victory" "$G/LogOutput.log" | grep -v 'scene changes' | cut -c50-200 | tail -8
echo "== exceptions"; grep -c "^Exception" "$G/LogOutput.log" "$G/LogOutput.1.log"
