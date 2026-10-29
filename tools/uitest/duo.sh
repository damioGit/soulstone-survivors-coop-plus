#!/usr/bin/env bash
# Two game instances on this PC for co-op tests: A hosts (direct IP), B joins 127.0.0.1. Both run the TestBridge.
#   duo.sh start      build, deploy, launch A and B windowed, press Play, wait for B connected
#   duo.sh stop       kill both, remove steam_appid.txt, restore the screen settings from the backup
#   duo.sh start fresh   same, but both test saves start again from the template
#   duo.sh A "state"  send one bridge command to an instance and print the reply (shots are also shrunk to *_s.jpg)
#   duo.sh diff       "cmd snap" on both, then what differs between the host (A) and the guest (B)
# Saves: each instance plays on its own copy in $OUT/saves/A|B (TestSave.cs), seeded from $OUT/save-template,
# which is filled once from the user's save backup. The real save in LocalLow is never opened.
set -u
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors"
OUT="$USERPROFILE/soulstone-uitest"
REPO=/i/soulstone-coop

pids() { tasklist //FI "IMAGENAME eq Soulstone Survivors.exe" //FO CSV //NH | grep -i soulstone | cut -d, -f2 | tr -d '"'; }

req() {
  local who=$1; shift
  local d="$OUT/$who"
  rm -f "$d/reply.txt"
  echo "$*" > "$d/request.txt"
  for i in $(seq 1 40); do [ -f "$d/reply.txt" ] && break; sleep 0.25; done
  cat "$d/reply.txt" 2>/dev/null || echo "no reply from $who"; echo
  case "$1" in
    shot) sleep 1.2; py -3 -c "
from PIL import Image
im=Image.open(r'$d/$2.png').convert('RGB'); im.resize((im.width//2, im.height//2)).save(r'$d/$2_s.jpg',quality=85)" ;;
  esac
}

case "${1:-}" in
  start)
    cd $REPO
    dotnet build src/SoulstoneCoop -c Release -p:GameDir="I:\\SteamLibrary\\steamapps\\common\\Soulstone Survivors" -p:SkipDeploy=true 2>&1 | grep -E " error |Kompilacja NIE" && exit 1
    for p in $(pids); do taskkill //PID $p //F >/dev/null; done; sleep 3
    cp src/SoulstoneCoop/bin/Release/net6.0/SoulstoneCoop.dll "$G/BepInEx/plugins/SoulstoneCoop/" || exit 1
    rm -f "$USERPROFILE/.soulstone-uitest"
    if [ ! -d "$OUT/save-template" ]; then
      mkdir -p "$OUT/save-template" && cp /i/soulstone-coop-backup3/LocalLow-SoulstoneSurvivors/playerProfile-* "$OUT/save-template/" || exit 1
    fi
    [ "${2:-}" = "fresh" ] && rm -rf "$OUT/saves"
    echo 2066020 > "$G/steam_appid.txt"
    # silent tests: master volume 0 (duo.sh stop restores the registry from the backup)
    reg add "HKCU\Software\Game Smithing\Soulstone Survivors" //v masterVolume_h3359004273 //t REG_DWORD //d 0 //f >/dev/null
    W="-screen-fullscreen 0 -screen-width 1280 -screen-height 720"
    cd "$G"
    for attempt in 1 2 3; do
      for p in $(pids); do taskkill //PID $p //F >/dev/null; done; sleep 3
      rm -rf "$OUT/A" "$OUT/B"
      cmd //c start "" "Soulstone Survivors.exe" $W --coop-host --coop-name A --coop-test A
      sleep 12
      cmd //c start "" "Soulstone Survivors.exe" $W --coop-connect 127.0.0.1 --coop-name B --coop-test B
      for w in A B; do for i in $(seq 1 60); do [ -f "$OUT/$w/ready.txt" ] && break; sleep 2; done; done
      sleep 10
      ok=0
      for w in A B; do for i in $(seq 1 12); do r=$(req $w play); case "$r" in *pressed*) ok=$((ok+1)); break;; esac; sleep 2; done; done
      sleep 8
      sa=$(req A state); sb=$(req B state)
      case "$sb" in *Connected*)
        echo "$sa"; echo "$sb"
        for w in A B; do sp=$(req $w cmd savepath); case "$sp" in *"provider=$(cygpath -w "$OUT")"*) ;; *) echo "SAVE NOT REDIRECTED on $w: $sp"; for p in $(pids); do taskkill //PID $p //F >/dev/null; done; exit 1;; esac; done
        exit 0;;
      esac
      echo "attempt $attempt failed (A: ${sa:0:60} | B: ${sb:0:60}), restarting"
    done
    exit 1
    ;;
  stop)
    for p in $(pids); do taskkill //PID $p //F >/dev/null; done
    rm -f "$G/steam_appid.txt"
    reg import "I:\\soulstone-coop-backup3\\registry.reg" 2>&1 | tail -1
    echo stopped
    ;;
  A|B) who=$1; shift; req $who "$@" ;;
  diff)
    req A cmd snap > "$OUT/snapA.txt"; req B cmd snap > "$OUT/snapB.txt"
    diff --label host "$OUT/snapA.txt" --label guest "$OUT/snapB.txt" -U0 | grep -v '^@@' && true
    echo "(host $(grep -c . "$OUT/snapA.txt") lines, guest $(grep -c . "$OUT/snapB.txt") lines)"
    ;;
  *) echo "usage: duo.sh start [fresh]|stop|diff|A cmd|B cmd" ;;
esac
