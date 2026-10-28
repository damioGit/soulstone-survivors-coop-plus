#!/usr/bin/env bash
# Titan Hunt to the final boss, fast: vial set to 3 on the host, one quick map, then the Primordial Portal.
D=/i/soulstone-coop/tools/uitest/duo.sh
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors/BepInEx"
info() { echo "-- $1 A: $(bash $D A cmd crusade info | cut -c1-330)"; echo "   $1 B: $(bash $D B cmd crusade info | cut -c1-330)"; }
quickmap() {
  bash $D A cmd crusade keys >/dev/null; sleep 5
  bash $D A cmd crusade goto door >/dev/null; sleep 10
  bash $D A cmd crusade goto boss >/dev/null; sleep 8
  for i in $(seq 1 6); do bash $D A cmd killbosses >/dev/null; sleep 2; done
  sleep 3; bash $D A cmd crusade goto blood >/dev/null; sleep 5
  info "$1"
}
[ "${START:-1}" = "1" ] && { bash $D start 2>&1 | tail -1 | cut -c1-80; bash $D A fight crusade; sleep 25; }
for w in A B; do bash $D $w cmd god >/dev/null; bash $D $w cmd autopick >/dev/null; done
bash $D A cmd crusade portal; sleep 3; bash $D A cmd crusade pick 0; sleep 22
for w in A B; do bash $D $w cmd god >/dev/null; done
bash $D A cmd crusade blood 3
quickmap map1
bash $D A cmd crusade goto portal; sleep 6; info panel
bash $D A shot f_panel >/dev/null
bash $D A cmd crusade primo; sleep 22; info primo
bash $D A shot f_primo >/dev/null; bash $D B shot f_primoB >/dev/null
bash $D A cmd crusade runes
R=${RUNNER:-A}  # RUNNER=B: the guest lights the runes and takes the Primordial Portal
[ "$R" = B ] && bash $D A cmd tp -20 30 >/dev/null
for i in 0 1 2 3; do bash $D $R cmd crusade goto rune$i >/dev/null; sleep 3; done
bash $D A cmd crusade runes; info runes
bash $D A shot f_runes >/dev/null
bash $D $R cmd crusade goto primordial; sleep 8; for w in A B; do bash $D $w cmd god >/dev/null; done; info primordial
sleep 15; info "after primordial"
[ -n "${STOP_AFTER_PRIMORDIAL:-}" ] && { echo "== host"; grep -E "crusade\]" "$G/LogOutput.log" | grep -v trace | cut -c40-200 | tail -8; echo "== client"; grep -E "crusade\]" "$G/LogOutput.1.log" | grep -v trace | cut -c40-200 | tail -8; exit 0; }
sleep 15; for w in A B; do bash $D $w cmd god >/dev/null; done; info final
bash $D A cmd crusade keys >/dev/null; sleep 5
bash $D A cmd crusade goto door >/dev/null; sleep 10
bash $D A cmd crusade goto boss >/dev/null; sleep 12; info finalroom
bash $D A shot f_room >/dev/null; bash $D B shot f_roomB >/dev/null
echo "bosses A: $(bash $D A cmd bosses | cut -c1-300)"; echo "bosses B: $(bash $D B cmd bosses | cut -c1-300)"
for k in 1 2 3 4 5 6 7 8; do
  for i in 1 2 3 4; do bash $D A cmd killbosses >/dev/null; sleep 3; done
  for w in A B; do bash $D $w cmd god >/dev/null; done
  bash $D A cmd crusade info | grep -q "bloodItem[^@]*@([^)]*) act=True" && { echo "blood after $k"; break; }
done
for t in 1 2 3; do bash $D A cmd crusade goto blood >/dev/null; sleep 4; done; info blood
for k in 1 2 3 4 5; do
  case "$(bash $D A cmd crusade info)" in *lightSigil=open*) bash $D A cmd crusade goto sigil; sleep 8; info "sigil$k";; *) break;; esac
done
bash $D A shot f_sig >/dev/null; bash $D B shot f_sigB >/dev/null
for k in $(seq 1 25); do
  for i in 1 2 3; do bash $D A cmd killbosses >/dev/null; sleep 3; done
  for w in A B; do bash $D $w cmd god >/dev/null; done
  grep -q "AttemptCallCredits" "$G/LogOutput.log" && { echo "credits after $k"; break; }
  bash $D A cmd crusade info | grep -q "home@([^)]*) act=True" && { echo "home open after $k"; break; }
  [ $((k % 5)) = 0 ] && { info "vk$k"; bash $D A shot f_vk$k >/dev/null; bash $D B shot f_vk${k}B >/dev/null; }
done
grep -q "AttemptCallCredits" "$G/LogOutput.log" && sleep 45
for t in 1 2 3; do bash $D A cmd crusade goto blood >/dev/null; sleep 4; done; info crown
bash $D A cmd crusade goto home; sleep 15; info home
bash $D A shot f_home >/dev/null; bash $D B shot f_homeB >/dev/null
echo "A: $(bash $D A state | cut -c1-120)"; echo "B: $(bash $D B state | cut -c1-120)"
echo "== trace"; grep -E "crusade\] trace" "$G/LogOutput.log" | cut -c40-230 | grep -v "Panel.Setup" | tail -15
echo "== client"; grep -E "crusade\]|\[run\]" "$G/LogOutput.1.log" | grep -v trace | cut -c40-200 | uniq | tail -15
echo "== exceptions"; grep -c "^Exception\|\[Error" "$G/LogOutput.log" "$G/LogOutput.1.log"
