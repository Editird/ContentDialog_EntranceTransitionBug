#!/bin/bash
# usage: run.sh <mode> <runname> [extra cap args]
S="C:/Users/User/AppData/Local/Temp/claude/C--Users-User-Documents-microsoft-ui-xaml/71306581-61c4-4cd6-8ff8-55a6d60ec078/scratchpad"
CAP="$S/Cap/bin/x64/Release/net8.0-windows10.0.22621.0/win-x64/Cap.exe"
APP="$S/CdTest/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/CdTest.exe"
OUT="$S/runs/$2"; rm -rf "$OUT"; mkdir -p "$OUT"
"$CAP" --launch "$APP" --args "$1 --auto $OUT/applog.csv" --title "CdTest-$1" --out "$OUT" --duration 15000 --applog "$OUT/applog.csv" ${3:-} > "$OUT/cap.txt" 2>&1
cat "$OUT/summary.txt"
echo "--- app events (ms_since_start,event,data,qpc_ms)"
grep -v ",frame," "$OUT/applog.csv"
