#!/bin/sh
# Ajoute EZP au menu des applications (sans droits administrateur).
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/256x256/apps"
cp "$DIR/EZP" "$HOME/.local/bin/EZP" && chmod +x "$HOME/.local/bin/EZP"
cp "$DIR/ezp.png" "$HOME/.local/share/icons/hicolor/256x256/apps/ezp.png"
cat > "$HOME/.local/share/applications/ezp.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=EZP — EazyPrinter
GenericName=Installation d'imprimantes
Comment=Trouver et installer les imprimantes réseau en 3 clics
Exec=$HOME/.local/bin/EZP
Icon=$HOME/.local/share/icons/hicolor/256x256/apps/ezp.png
StartupWMClass=EZP
Categories=System;Settings;HardwareSettings;Printing;
Terminal=false
DESK
command -v update-desktop-database >/dev/null && update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
echo "EZP est dans le menu des applications (programme : $HOME/.local/bin/EZP)."
