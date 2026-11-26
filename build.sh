#!/bin/sh
# Compile EZP pour Windows, Linux et macOS (Intel + Apple Silicon) et prépare les zips dans out/.
# Prérequis : SDK .NET 8 ou plus récent, zip ; python3 + Pillow pour l'icône macOS.
# Sur un Mac, l'app est en plus signée « ad hoc » (obligatoire sur Apple Silicon).
set -e
cd "$(dirname "$0")"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/EFP.Desktop/EFP.Desktop.csproj)
[ "$1" = "--skip-tests" ] || dotnet run --project tests/EFP.Tests -c Release
rm -rf out && mkdir -p out/pkg
for rid in win-x64 linux-x64 osx-x64 osx-arm64; do
    dotnet publish src/EFP.Desktop -c Release -r $rid -o out/$rid
done
(cd out/win-x64 && zip -q ../EZP-$VERSION-Windows.zip EZP.exe)

mkdir -p out/pkg/linux/EZP && cp out/linux-x64/EZP packaging/linux/* out/pkg/linux/EZP/ && cp src/EFP.Desktop/Assets/efp-logo.png out/pkg/linux/EZP/ezp.png
chmod +x out/pkg/linux/EZP/EZP out/pkg/linux/EZP/*.sh
(cd out/pkg/linux && zip -qry ../../EZP-$VERSION-Linux-x64.zip EZP)

python3 -c "from PIL import Image; Image.open('src/EFP.Desktop/Assets/efp-logo.png').convert('RGBA').resize((1024,1024)).save('out/pkg/ezp.icns')"
for arch in arm64 x64; do
    app=out/pkg/mac-$arch/EZP.app
    mkdir -p $app/Contents/MacOS $app/Contents/Resources
    cp out/osx-$arch/EZP $app/Contents/MacOS/EZP && chmod +x $app/Contents/MacOS/EZP
    cp out/pkg/ezp.icns $app/Contents/Resources/ezp.icns
    sed "s/@VERSION@/$VERSION/g" packaging/macos/Info.plist > $app/Contents/Info.plist
    command -v codesign >/dev/null && codesign --force --deep -s - $app
    name=$([ $arch = arm64 ] && echo AppleSilicon || echo Intel)
    (cd out/pkg/mac-$arch && zip -qry ../../EZP-$VERSION-macOS-$name.zip EZP.app)
done
ls -la out/*.zip
