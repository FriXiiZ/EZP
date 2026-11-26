using System.Text;
using System.Text.RegularExpressions;
using EFP.Core.Windows;

namespace EFP.Core.Platform;

/// <summary>
/// Scripts sh pour CUPS (Linux, macOS). Même protocole de sortie que les scripts PowerShell :
/// « ##PP|étape|START/OK/WARN/FAIL|détail », pour réutiliser l'analyse et la progression en direct.
/// </summary>
public static class CupsScripts
{
    public const string GenericPostScript = "drv:///sample.drv/generic.ppd";
    public const string Everywhere = "everywhere";

    /// <summary>Guillemets simples sh : 'abc' (une apostrophe devient '\'').</summary>
    public static string Q(string? s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    /// <summary>Nom de file CUPS : lettres, chiffres, - _ . (pas d'espace, pas de / ni #).</summary>
    public static string QueueName(string displayName)
    {
        var n = Regex.Replace(displayName.Trim(), @"[^A-Za-z0-9._-]+", "_").Trim('_');
        if (n.Length == 0) n = "Imprimante";
        return n.Length > 100 ? n[..100] : n;
    }

    /// <summary>Adresse de l'imprimante : IPP pour le sans pilote, sinon port 9100, sinon IPP, sinon LPD.</summary>
    public static string DeviceUri(DiscoveredPrinter p, DriverChoice driver) =>
        driver.Kind == DriverMatchKind.Driverless ? $"ipp://{p.IpAddress}/ipp/print"
        : p.HasRawPort ? $"socket://{p.IpAddress}:9100"
        : p.HasIpp ? $"ipp://{p.IpAddress}/ipp/print"
        : p.OpenPorts.Contains(515) ? $"lpd://{p.IpAddress}/queue"
        : $"socket://{p.IpAddress}:9100";

    private const string Header = """
        #!/bin/sh
        export LC_ALL=C
        PATH="/usr/sbin:/usr/bin:/sbin:/bin:$PATH"
        pp() { printf '##PP|%s|%s|%s\n' "$1" "$2" "$(printf '%s' "$3" | tr '\n|' '  ')"; }

        """;

    public static string InstallPrinter(DiscoveredPrinter p, DriverChoice driver, InstallOptions o, string queue, string description)
    {
        var model = driver.InfPath ?? GenericPostScript;
        var sb = new StringBuilder(Header);
        sb.AppendLine($"NAME={Q(queue)}; DESC={Q(description)}; LOC={Q(o.Location ?? "")}");
        sb.AppendLine($"URI={Q(DeviceUri(p, driver))}; MODEL={Q(model)}; IP={Q(p.IpAddress)}");
        sb.AppendLine($"FALLBACK_URI={Q(p.HasRawPort ? $"socket://{p.IpAddress}:9100" : $"ipp://{p.IpAddress}/ipp/print")}");
        sb.AppendLine($"REPLACE={(o.ReplaceExisting ? 1 : 0)}; DUPLEX={(o.DuplexByDefault ? 1 : 0)}");
        sb.AppendLine($"MODELKEY={Q(PowerShellScripts.ModelKey(p.Model) ?? "")}");
        sb.Append("""

            pp Pilote START ""
            if [ "$MODEL" = everywhere ]; then
                pp Pilote OK "IPP / AirPrint"
            elif [ "${MODEL#driverless:}" != "$MODEL" ]; then
                pp Pilote OK "IPP / AirPrint (fiche du modèle)"
            elif [ -f "$MODEL" ]; then
                pp Pilote OK "PPD $(basename "$MODEL")"
            elif lpinfo -m 2>/dev/null | grep -qF "$MODEL "; then
                pp Pilote OK "$MODEL"
            else
                pp Pilote WARN "$MODEL introuvable dans CUPS : PostScript générique"
                MODEL='drv:///sample.drv/generic.ppd'
            fi

            pp Port START ""
            removed=""
            if [ "$REPLACE" = 1 ]; then
                for q in $(lpstat -v 2>/dev/null | grep -F "/$IP" | sed -n 's/^device for \([^:]*\):.*/\1/p') \
                         $(lpstat -v 2>/dev/null | grep -F "/$IP:" | sed -n 's/^device for \([^:]*\):.*/\1/p'); do
                    if [ "$q" != "$NAME" ] && lpadmin -x "$q" 2>/dev/null; then removed="$removed $q"; fi
                done
                # Files créées toutes seules par le système (Bonjour / cups-browsed : dnssd://, ipps://….local, implicitclass://),
                # sans IP dans l'adresse : reconnues au modèle (MFC_L3760CDW ≈ MFC-L3760CDW)
                if [ -n "$MODELKEY" ]; then
                    lpstat -v 2>/dev/null | sed -n 's/^device for \([^:]*\): *\(.*\)$/\1 \2/p' | while read -r q uri; do
                        case "$uri" in dnssd://*|implicitclass://*|*.local/*|*.local:*|*._tcp*) ;; *) continue ;; esac
                        key=$(printf '%s %s' "$q" "$uri" | sed 's/%20/ /g' | tr -cd '[:alnum:]' | tr '[:lower:]' '[:upper:]')
                        case "$key" in *"$MODELKEY"*) [ "$q" != "$NAME" ] && lpadmin -x "$q" 2>/dev/null && echo "$q" ;; esac
                    done > /tmp/ezp-removed.$$
                    for q in $(cat /tmp/ezp-removed.$$); do removed="$removed $q"; done
                    rm -f /tmp/ezp-removed.$$
                fi
            fi
            if [ -n "$removed" ]; then pp Port OK "$URI (remplace :$removed)"; else pp Port OK "$URI"; fi

            pp Imprimante START ""
            add() { # $1 = adresse, $2 = modèle
                if [ -f "$2" ]; then lpadmin -p "$NAME" -E -v "$1" -P "$2" -D "$DESC" -L "$LOC" 2>&1
                else lpadmin -p "$NAME" -E -v "$1" -m "$2" -D "$DESC" -L "$LOC" 2>&1; fi
            }
            out=$(add "$URI" "$MODEL"); rc=$?
            if [ $rc -ne 0 ] && { [ "$MODEL" = everywhere ] || [ "${MODEL#driverless:}" != "$MODEL" ]; }; then
                # l'imprimante ne répond pas en IPP Everywhere : PostScript générique
                out2=$(add "$FALLBACK_URI" 'drv:///sample.drv/generic.ppd'); rc=$?
                if [ $rc -eq 0 ]; then pp Imprimante WARN "IPP / AirPrint impossible ($out), PostScript générique utilisé"
                else pp Imprimante FAIL "$out2"; exit 1; fi
            elif [ $rc -ne 0 ]; then
                pp Imprimante FAIL "$out"; exit 1
            else
                pp Imprimante OK "$NAME"
            fi

            pp Réglages START ""
            lpadmin -p "$NAME" -o printer-is-shared=false >/dev/null 2>&1
            if [ "$DUPLEX" = 1 ]; then
                lpadmin -p "$NAME" -o sides-default=two-sided-long-edge >/dev/null 2>&1
                lpadmin -p "$NAME" -o Duplex=DuplexNoTumble >/dev/null 2>&1
                pp Réglages OK "recto-verso par défaut, non partagée"
            else
                pp Réglages OK "non partagée"
            fi

            pp État START ""
            cupsenable "$NAME" 2>/dev/null; cupsaccept "$NAME" 2>/dev/null
            st=$(lpstat -p "$NAME" 2>/dev/null | head -n 1)
            if [ -n "$st" ]; then pp État OK "$st"; else pp État WARN "état inconnu"; fi
            printf '##PP|@name|OK|%s\n' "$NAME"
            exit 0

            """);
        return sb.ToString();
    }

    /// <summary>Files CUPS existantes : une ligne « @inst » (nom, modèle, -, IP) par imprimante réseau.</summary>
    public static string ListInstalled() => Header + """
        lpstat -v 2>/dev/null | sed -n 's/^device for \([^:]*\): *\(.*\)$/\1 \2/p' | while read -r q uri; do
            host=$(printf '%s' "$uri" | sed -n 's#^[a-z]*://\([^/:]*\).*#\1#p')
            [ -z "$host" ] && continue
            mm=$(lpoptions -p "$q" 2>/dev/null | tr ' ' '\n' | sed -n "s/^printer-make-and-model=//p" | tr -d "'")
            [ -z "$mm" ] && mm=$(lpoptions -p "$q" 2>/dev/null | sed -n "s/.*printer-make-and-model='\([^']*\)'.*/\1/p")
            printf '##PP|@inst|OK|%s\t%s\t\t%s\n' "$q" "$mm" "$host"
        done
        exit 0

        """;

    /// <summary>Par défaut (pour l'utilisateur courant) et page de test : sans droits administrateur.</summary>
    public static string Finish(string queue, bool setDefault, bool testPage)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine($"NAME={Q(queue)}; SETDEF={(setDefault ? 1 : 0)}; TEST={(testPage ? 1 : 0)}");
        sb.Append("""
            if [ "$SETDEF" = 1 ]; then
                if out=$(lpoptions -d "$NAME" 2>&1); then pp 'Par défaut' OK "pour l'utilisateur $(id -un)"; else pp 'Par défaut' WARN "$out"; fi
            fi
            if [ "$TEST" = 1 ]; then
                f=/usr/share/cups/data/testprint
                if [ -f "$f" ]; then out=$(lp -d "$NAME" "$f" 2>&1)
                else out=$(printf 'EZP - page de test\n\nImprimante : %s\nDate : %s\n' "$NAME" "$(date)" | lp -d "$NAME" -t "EZP test" 2>&1); fi
                if [ $? -eq 0 ]; then pp 'Page de test' OK "envoyée ($out)"; else pp 'Page de test' WARN "$out"; fi
            fi
            exit 0

            """);
        return sb.ToString();
    }
}
