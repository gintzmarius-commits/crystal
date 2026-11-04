#!/bin/bash
# Test automatique sur emulateur (Pixel 6 : 1080x2400, densite 2.625).
# Installe l'APK, accorde les autorisations, teste l'ilot, les notifs, les gestes et le menu.
P=com.liquidisland
OUT=shots
mkdir -p $OUT
# Trace complete (commandes + erreurs) pour comprendre un eventuel blocage
exec 2>>$OUT/trace.txt
set -x
# Chaque commande adb a une limite de temps : un blocage ne fige plus tout le test
ADB_BIN=$(command -v adb)
adb() { timeout 45 "$ADB_BIN" "$@"; }
shot() { adb exec-out screencap -p > "$OUT/$1.png"; echo "::notice::capture $1"; }
fail=0

adb install -r -g WaveIsland.apk || { echo "::error::Installation de l'APK echouee"; exit 1; }
adb shell appops set $P SYSTEM_ALERT_WINDOW allow
adb shell cmd notification allow_listener $P/$P.NotifListener
adb shell dumpsys deviceidle whitelist +$P
adb shell settings put secure enabled_accessibility_services $P/$P.IslandAccessibility
adb shell settings put secure accessibility_enabled 1
# Autorise la capture d'ecran sans la fenetre de confirmation (test automatique seulement)
adb shell appops set $P PROJECT_MEDIA allow
adb logcat -c
adb logcat -G 8M

adb shell am start -W -n $P/.MainActivity
sleep 4
shot 1-appli-haut
adb shell input swipe 540 1700 540 500 400
sleep 2
shot 2-appli-bas

adb shell am start -W -n $P/.MainActivity --es ci start
sleep 3
adb shell am start -W -n $P/.MainActivity --es ci live
sleep 3
adb shell input keyevent KEYCODE_HOME
sleep 3
shot 3-ilot-replie

adb shell am start -W -n $P/.MainActivity --es ci test
sleep 0.35
shot 4a-animation
sleep 1.5
shot 4-notif-test

adb shell input keyevent KEYCODE_HOME
sleep 6
adb shell "cmd notification post -S bigtext -t 'Marie' chat1 'Tu viens ce soir ? On se retrouve a 20h.'"
sleep 2
shot 5-vraie-notif
sleep 6
shot 6-referme

# Toucher la notif => ouvre l'appli concernee (ici Wave Island)
adb shell am start -W -n $P/.MainActivity --es ci testbg
sleep 1.5
adb shell input tap 540 121
sleep 2
shot 9-toucher-ouvre-appli

# Glisser la notif vers le bas => ouverture en fenetre (flottante si le telephone l'accepte)
adb shell input keyevent KEYCODE_HOME
sleep 6
adb shell am start -W -n $P/.MainActivity --es ci testbg
sleep 2
adb shell input swipe 540 240 540 700 900
sleep 2
shot 10-glisser-ouvre

# Appui long sur l'ilot replie => mini menu
adb shell input keyevent KEYCODE_HOME
sleep 6
adb shell input swipe 540 66 540 66 1200
sleep 1
shot 11-menu
sleep 8
# Boutons de volume : l'indicateur facon iPhone doit remplacer celui du telephone
# Vrais appuis materiels : on ecrit directement dans le peripherique de touches de l'emulateur
press() {  # $1 = code (115 = volume +, 114 = volume -)
  adb shell "su 0 sh -c 'sendevent $KDEV 1 $1 1; sendevent $KDEV 0 0 0; sendevent $KDEV 1 $1 0; sendevent $KDEV 0 0 0'"
}
KDEV=$(adb shell "su 0 getevent -pl" 2>/dev/null | awk '/^add device/{d=$4} /KEY_VOLUMEUP/{print d; exit}' | tr -d '\r')
echo "::notice::Peripherique des touches : ${KDEV:-introuvable}"
if [ -n "$KDEV" ]; then
  press 115; sleep 0.4; press 115; sleep 0.7
else
  adb shell input keyevent KEYCODE_VOLUME_UP; sleep 0.4; adb shell input keyevent KEYCODE_VOLUME_UP; sleep 0.7
fi
shot 16-volume
[ -n "$KDEV" ] && press 114 || adb shell input keyevent KEYCODE_VOLUME_DOWN
sleep 3

# Arreter puis redemarrer l'ilot : il doit revenir au-dessus de la barre d'etat
adb shell am start -W -n $P/.MainActivity --es ci stop
sleep 2
adb shell am start -W -n $P/.MainActivity --es ci start
sleep 3
adb shell input keyevent KEYCODE_HOME
sleep 2
shot 17-apres-redemarrage

# Defilement : le bord deforme doit suivre en direct
adb shell am start -W -n $P/.MainActivity
sleep 2
( adb shell input swipe 540 1900 540 700 1600 & )
sleep 0.8
shot 15-defilement
sleep 2
adb shell input keyevent KEYCODE_HOME
sleep 7

# Plein ecran : l'ilot doit se cacher
adb shell am start -W -n $P/.MainActivity --es ci fullscreen
sleep 2
shot 12-plein-ecran
adb shell am start -W -n $P/.MainActivity --es ci exitfullscreen
sleep 1
# Paysage : l'ilot reste contre la camera (bord gauche), s'etend vers le centre
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 1
adb shell am start -W -n $P/.MainActivity
sleep 3
shot 13-paysage
adb shell am start -W -n $P/.MainActivity --es ci test
sleep 1.5
shot 14-paysage-notif
sleep 6
adb shell settings put system user_rotation 0
sleep 2
adb devices >&2
adb shell dumpsys activity activities | grep -E "mResumedActivity|topResumed" >&2

# ---------- Wave Drop (2.0) ----------
adb shell settings put system accelerometer_rotation 0
adb shell settings put system user_rotation 0
sleep 2
for perm in BLUETOOTH_SCAN BLUETOOTH_ADVERTISE BLUETOOTH_CONNECT; do adb shell pm grant $P android.permission.$perm 2>/dev/null || true; done
adb shell am start -W -n $P/.MainActivity --es ci dropon
sleep 3
shot 18-wavedrop-onglet
# Glisser l'ilot replie vers le bas : le panneau Wave Drop coule depuis l'ilot
adb shell input keyevent KEYCODE_HOME
sleep 2
W=$(adb shell wm size | grep -o '[0-9]*x[0-9]*' | tail -1 | cut -dx -f1)
adb shell input swipe 540 66 540 520 900
sleep 2.5
shot 19-wavedrop-panneau
adb shell input keyevent KEYCODE_BACK
sleep 1.5
adb shell am start -W -n $P/.MainActivity --es ci dropsend
sleep 2.5
shot 19b-wavedrop-panneau-bouton
adb shell input keyevent KEYCODE_BACK
sleep 1

# ---------- Centres de notifications (gauche) et de controle (droite) ----------
adb shell input keyevent KEYCODE_HOME
sleep 2
adb shell "cmd notification post -S bigtext -t 'Lea' chat3 'Photos du week-end envoyees !'"
sleep 7
adb shell input swipe 150 20 150 900 500
sleep 2.5
shot 24-centre-notifications
adb shell input keyevent KEYCODE_BACK
sleep 1.5
adb shell input swipe 930 20 930 900 500
sleep 2.5
shot 25-centre-controle
sleep 4
shot 25b-centre-controle-fini
adb shell input keyevent KEYCODE_BACK
sleep 1.5

timeout 90 "$ADB_BIN" logcat -d > $OUT/logcat-complet.txt
echo "::notice::logcat $(wc -l < $OUT/logcat-complet.txt) lignes"
grep -E "LiquidIsland|AndroidRuntime|FATAL" $OUT/logcat-complet.txt > $OUT/log.txt
adb shell dumpsys activity services $P | grep -i "ServiceRecord" >> $OUT/log.txt

grep -q "FATAL EXCEPTION" $OUT/logcat-complet.txt && grep -A12 "FATAL EXCEPTION" $OUT/logcat-complet.txt | head -20 | while read -r l; do echo "::error::$l"; done && fail=1
grep -q "barre d'etat=true" $OUT/log.txt || { echo "::error::L'ilot n'est pas passe au-dessus de la barre d'etat"; fail=1; }
grep -q "Ilot affiche" $OUT/log.txt || { echo "::error::L'ilot ne s'est pas affiche"; fail=1; }
grep -q "Notification affichee : Wave Island" $OUT/log.txt || { echo "::error::La notif de test n'est pas arrivee"; fail=1; }
grep -q "Notification affichee : Marie" $OUT/log.txt || { echo "::error::La vraie notif n'est pas arrivee"; fail=1; }
grep -q "flottante=false, ok=true" $OUT/log.txt || { echo "::error::Toucher la notif n'a pas ouvert l'appli"; fail=1; }
grep -q "flottante=true, ok=true" $OUT/log.txt || { echo "::error::Glisser la notif n'a pas ouvert l'appli"; fail=1; }
grep -q "Effet goutte en direct actif" $OUT/log.txt || { echo "::error::L'effet goutte en direct ne s'est pas active"; fail=1; }
grep "Fond en direct" $OUT/log.txt | while read -r l; do echo "::notice::$l"; done
grep -q "Plein ecran : oui" $OUT/log.txt || echo "::warning::Le plein ecran n'a pas ete detecte"
# Avant le test plein ecran, l'ilot ne doit jamais se croire en plein ecran
awk '/Plein ecran : oui/{print; exit}' $OUT/log.txt > $OUT/premier-plein.txt
grep -q "Menu ouvert" $OUT/log.txt && [ "$(grep -n 'Plein ecran : oui' $OUT/log.txt | head -1 | cut -d: -f1)" -lt "$(grep -n 'Menu ouvert' $OUT/log.txt | head -1 | cut -d: -f1)" ] && { echo "::error::L'ilot s'est cru en plein ecran trop tot"; fail=1; }
[ "$(grep -c "barre d'etat=true" $OUT/log.txt)" -ge 2 ] || { echo "::error::Apres redemarrage l'ilot n'est plus au-dessus de la barre d'etat"; fail=1; }
grep -E "filtre des touches|Touche recue" $OUT/log.txt | while read -r l; do echo "::notice::$l"; done
grep -q "Volume : " $OUT/log.txt || { echo "::error::Les boutons de volume ne sont pas passes par l'indicateur"; fail=1; }
grep -q "Menu ouvert" $OUT/log.txt || { echo "::error::L'appui long n'a pas ouvert le menu"; fail=1; }
grep -q "Wave Drop : demarrage" $OUT/log.txt || { echo "::error::Wave Drop ne s'est pas lance"; fail=1; }
grep -q "panneau ouvert depuis l'ilot" $OUT/log.txt || echo "::warning::Glisser l'ilot n'a pas ouvert Wave Drop"
grep -q "Centre Wave Island ouvert" $OUT/log.txt || echo "::warning::Tirer la barre d'etat n'a pas ouvert le centre Wave Island"
grep "Bord du haut tire" $OUT/log.txt | while read -r l; do echo "::notice::$l"; done
grep -q "Fond capture pour le verre" $OUT/log.txt || echo "::warning::Pas de capture du fond (refraction)"
grep "LiquidIsland" $OUT/log.txt | while read -r l; do echo "::notice::$l"; done

# Mode encoche en dernier (journaux deja recuperes)
adb shell am start -W -n $P/.MainActivity --es ci notch
sleep 2
adb shell input keyevent KEYCODE_HOME
sleep 2
shot 7-encoche
adb shell "cmd notification post -S bigtext -t 'Paul' chat2 'Match a 18h, tu ramenes le ballon ?'"
sleep 2
shot 8-encoche-notif

timeout 60 "$ADB_BIN" logcat -d | grep -E "LiquidIsland|FATAL|AndroidRuntime" > $OUT/log-encoche.txt
grep -q "forme=encoche" $OUT/log-encoche.txt || echo "::warning::Pas de trace du mode encoche"
exit $fail
