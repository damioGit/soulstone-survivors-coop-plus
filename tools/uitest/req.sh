#!/usr/bin/env bash
# Ask the in-game UiProbe (watch mode) for something: req.sh "shot NAME" | "field" | "quit"
OUT="$USERPROFILE/soulstone-uitest"
rm -f "$OUT/reply.txt"
echo "$1" > "$OUT/request.txt"
for i in $(seq 1 40); do [ -f "$OUT/reply.txt" ] && break; sleep 0.25; done
cat "$OUT/reply.txt" 2>/dev/null || echo "no reply"; echo
case "$1" in
  shot\ *)
    n="${1#shot }"; sleep 1
    py -3 -c "
from PIL import Image
im=Image.open(r'$OUT/$n.png').convert('RGB')
im.resize((1280,720)).save(r'$OUT/${n}_s.jpg',quality=85)
im.crop((0,200,1100,1060)).save(r'$OUT/${n}_c.jpg',quality=85)" ;;
esac
