#!/bin/bash
# Test automatique sur emulateur : installe l'APK, accorde les autorisations,
# demarre l'ilot, envoie une notif de test et une vraie notif, prend des captures.
P=com.liquidisland
OUT=shots
mkdir -p $OUT
shot() { adb exec-out screencap -p > "$OUT/$1.png"; echo "capture $1"; }
fail=0

adb install -r -g LiquidIsland.apk || { echo "::error::Installation de l'APK echouee"; exit 1; }
adb shell appops set $P SYSTEM_ALERT_WINDOW allow
adb shell cmd notification allow_listener $P/$P.NotifListener
adb shell dumpsys deviceidle whitelist +$P
adb logcat -c

adb shell am start -W -n $P/.MainActivity
sleep 4
shot 1-appli-haut
adb shell input swipe 540 1700 540 500 400
sleep 2
shot 2-appli-bas

adb shell am start -W -n $P/.MainActivity --es ci start
sleep 3
adb shell input keyevent KEYCODE_HOME
sleep 3
shot 3-ilot-replie

adb shell am start -W -n $P/.MainActivity --es ci test
sleep 2
shot 4-notif-test

adb shell input keyevent KEYCODE_HOME
sleep 6
adb shell cmd notification post -S bigtext -t "Marie" chat1 "Tu viens ce soir ? On se retrouve a 20h."
sleep 2
shot 5-vraie-notif

adb logcat -d > $OUT/logcat-complet.txt
grep -E "LiquidIsland|AndroidRuntime|FATAL" $OUT/logcat-complet.txt > $OUT/log.txt
adb shell dumpsys activity services $P | grep -i "ServiceRecord" >> $OUT/log.txt

grep -q "FATAL EXCEPTION" $OUT/logcat-complet.txt && grep -A12 "FATAL EXCEPTION" $OUT/logcat-complet.txt | head -20 | while read -r l; do echo "::error::$l"; done && fail=1
grep -q "Ilot affiche" $OUT/log.txt || { echo "::error::L'ilot ne s'est pas affiche"; fail=1; }
grep -q "Notification affichee : Liquid Island" $OUT/log.txt || { echo "::error::La notif de test n'est pas arrivee"; fail=1; }
grep -q "Notification affichee : Marie" $OUT/log.txt || { echo "::error::La vraie notif n'est pas arrivee"; fail=1; }
grep "LiquidIsland" $OUT/log.txt | while read -r l; do echo "::notice::$l"; done
exit $fail
