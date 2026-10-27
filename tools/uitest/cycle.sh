#!/usr/bin/env bash
# One UI test cycle: build, close the game, deploy, launch from Steam, wait for the probe's done.txt.
# Usage: cycle.sh "<flag file lines separated by ;>"
set -u
G="/i/SteamLibrary/steamapps/common/Soulstone Survivors"
OUT="$USERPROFILE/soulstone-uitest"
cd /i/soulstone-coop
dotnet build src/SoulstoneCoop -c Release -p:GameDir="I:\\SteamLibrary\\steamapps\\common\\Soulstone Survivors" -p:SkipDeploy=true 2>&1 | grep -E " error |Kompilacja NIE" && exit 1
pid=$(tasklist //FI "IMAGENAME eq Soulstone Survivors.exe" //FO CSV //NH | grep -i soulstone | cut -d, -f2 | tr -d '"')
if [ -n "$pid" ]; then taskkill //PID "$pid" //F >/dev/null; sleep 4; fi
cp src/SoulstoneCoop/bin/Release/net6.0/SoulstoneCoop.dll "$G/BepInEx/plugins/SoulstoneCoop/" || exit 1
echo "${1:-quit}" | tr ';' '\n' > "$USERPROFILE/.soulstone-uitest"
rm -f "$OUT/done.txt"
cmd //c start "" "steam://rungameid/2066020"
for i in $(seq 1 90); do
  [ -f "$OUT/done.txt" ] && break
  sleep 2
done
cat "$OUT/done.txt" 2>/dev/null || echo "TIMEOUT"
grep -E "uitest|\[ui\]|Exception|Error" "$G/BepInEx/LogOutput.log" | tail -15
ls "$OUT"
