using System.Text;
using System.Text.RegularExpressions;

namespace EFP.Core.Windows;

/// <summary>
/// Génère les scripts PowerShell exécutés sur le PC du client.
/// Chaque étape écrit une ligne "##PP|étape|OK|WARN|FAIL|détail" que l'appli relit.
/// Toutes les valeurs venant de l'extérieur (noms SNMP, saisies) passent par <see cref="Quote"/>.
/// </summary>
public static class PowerShellScripts
{
    public const string Marker = "##PP|";

    /// <summary>Chaîne PowerShell entre apostrophes, sans interpolation possible.</summary>
    public static string Quote(string? value)
    {
        if (value is null) return "$null";
        // PowerShell considère aussi les apostrophes typographiques comme délimiteurs : on les double toutes.
        var sb = new StringBuilder("'");
        foreach (var c in value)
        {
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') sb.Append(c).Append(c);
            else if (c is '\r' or '\n' or '\0') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    public static string Bool(bool b) => b ? "$true" : "$false";

    private const string Header = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
        function Step([string]$Name, [scriptblock]$Action) {
            [Console]::Out.WriteLine('##PP|' + $Name + '|START|')
            try {
                $out = & $Action
                $text = (@($out) | Where-Object { $_ -ne $null } | ForEach-Object { "$_" }) -join ' '
                $status = 'OK'
                if ($text -like 'WARN:*') { $status = 'WARN'; $text = $text.Substring(5).Trim() }
                [Console]::Out.WriteLine(('##PP|{0}|{1}|{2}' -f $Name, $status, ($text -replace '[\r\n|]+', ' ')))
                return $true
            } catch {
                [Console]::Out.WriteLine(('##PP|{0}|FAIL|{1}' -f $Name, ($_.Exception.Message -replace '[\r\n|]+', ' ')))
                return $false
            }
        }

        """;

    /// <summary>Nom Windows de l'imprimante à partir du modèle de nom.</summary>
    public static string BuildPrinterName(DiscoveredPrinter p, InstallOptions o)
    {
        var name = o.NameTemplate
            .Replace("{Brand}", p.Brand == Brands.Unknown ? "Imprimante" : p.Brand, StringComparison.OrdinalIgnoreCase)
            .Replace("{Model}", p.Model ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Ip}", p.IpAddress, StringComparison.OrdinalIgnoreCase)
            .Replace("{Location}", o.Location ?? "", StringComparison.OrdinalIgnoreCase);
        // Évite "HP HP LaserJet" quand le modèle contient déjà la marque
        if (p.Model is not null && p.Brand != Brands.Unknown)
            name = Regex.Replace(name, $@"\b{Regex.Escape(p.Brand)}\s+{Regex.Escape(p.Brand)}\b", p.Brand, RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"[\\,""\x00-\x1F]", " ");          // caractères refusés par le spouleur
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', '-', '_');
        if (name.Length == 0) name = $"Imprimante {p.IpAddress}";
        return name.Length > 120 ? name[..120].Trim() : name;
    }

    public static string PortName(string ip) => $"IP_{ip}";

    /// <summary>Nom de partage Windows valide (pas de \ / , ; : * ? " &lt; &gt; |, 80 caractères max).</summary>
    /// <summary>Modèle réduit aux lettres et chiffres (MFC-L3760CDW → MFCL3760CDW), null s'il est trop court pour être fiable.</summary>
    public static string? ModelKey(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var k = Regex.Replace(model, "[^A-Za-z0-9]", "").ToUpperInvariant();
        return k.Length >= 5 && Regex.IsMatch(k, "[0-9]") ? k : null;
    }

    public static string BuildShareName(string printerName)
    {
        var n = Regex.Replace(printerName, @"[\\/,;:*?""<>|]", " ");
        n = Regex.Replace(n, @"\s+", " ").Trim();
        if (n.Length == 0) n = "Imprimante";
        return n.Length > 80 ? n[..80].Trim() : n;
    }

    public static string InstallPrinter(DiscoveredPrinter p, DriverChoice driver, InstallOptions o, string printerName)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine($"$driver  = {Quote(driver.DriverName)}");
        sb.AppendLine($"$inf     = {Quote(driver.InfPath)}");
        sb.AppendLine($"$ip      = {Quote(p.IpAddress)}");
        sb.AppendLine($"$port    = {Quote(PortName(p.IpAddress))}");
        sb.AppendLine($"$model   = {Quote(ModelKey(p.Model) is null ? "" : p.Model!.Trim())}");
        sb.AppendLine($"$state   = @{{ Name = {Quote(printerName)} }}");
        sb.AppendLine($"$location = {Quote(o.Location ?? "")}");
        sb.AppendLine($"$snmp    = {Bool(o.EnableSnmpStatus && p.SnmpResponded)}");
        sb.AppendLine($"$replace = {Bool(o.ReplaceExisting)}");
        sb.AppendLine($"$duplex  = {Bool(o.DuplexByDefault)}");
        sb.AppendLine($"$mono    = {Bool(o.MonochromeByDefault)}");
        sb.AppendLine($"$setDefault = {Bool(o.SetAsDefault)}");
        sb.AppendLine($"$testPage   = {Bool(o.PrintTestPage)}");
        sb.AppendLine($"$noAutoSetup = {Bool(o.DisableWindowsAutoSetup)}");
        sb.AppendLine($"$share   = {Bool(o.ShareOnNetwork)}");
        sb.AppendLine($"$shareName = {Quote(BuildShareName(string.IsNullOrWhiteSpace(o.ShareName) ? printerName : o.ShareName))}");
        sb.Append("""

            $ok = Step 'Pilote' {
                if (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue) { 'déjà présent'; return }
                if ($inf) {
                    if (-not (Test-Path -LiteralPath $inf)) { throw "INF introuvable : $inf" }
                    # Ajout au magasin de pilotes seulement (sans /install : évite un balayage des périphériques qui peut durer des minutes)
                    $pnp = & pnputil.exe /add-driver $inf 2>&1
                    # 0 = OK, 259 = déjà dans le magasin, 3010 = redémarrage conseillé
                    if ($LASTEXITCODE -notin 0, 259, 3010) { throw ("pnputil a échoué (code {0}) : {1}" -f $LASTEXITCODE, ((@($pnp) | Select-Object -Last 3) -join ' ')) }
                }
                Add-PrinterDriver -Name $driver
                'installé'
            }
            if (-not $ok) { exit 1 }

            $ok = Step 'Port' {
                if (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue) { 'déjà présent'; return }
                if ($snmp) { Add-PrinterPort -Name $port -PrinterHostAddress $ip -SNMP 1 -SNMPCommunity 'public' }
                else { Add-PrinterPort -Name $port -PrinterHostAddress $ip }
                "créé ($ip)"
            }
            if (-not $ok) { exit 1 }

            $ok = Step 'Imprimante' {
                # Les blocs s'exécutent dans une portée enfant : le nom final est stocké dans $state (table partagée)
                $n = $state.Name
                # Ports qui visent la même imprimante : le nôtre, et tout port dont l'adresse ou le nom contient l'IP
                $ipRx = '(^|[^0-9])' + [regex]::Escape($ip) + '([^0-9]|$)'
                $samePorts = @(Get-PrinterPort -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -eq $port -or $_.PrinterHostAddress -eq $ip -or $_.Name -match $ipRx } |
                    ForEach-Object { $_.Name })
                $samePorts += $port
                $dups = @(Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne $n -and $samePorts -contains $_.PortName } | ForEach-Object { $_.Name })
                # Copies ajoutées toutes seules par Windows via WSD (port « WSD-… » sans adresse IP) : reconnues au modèle
                if ($model) {
                    $dups += @(Get-Printer -ErrorAction SilentlyContinue | Where-Object {
                        $_.Name -ne $n -and "$($_.PortName)" -like 'WSD*' -and
                        ("$($_.Name)".IndexOf($model, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or "$($_.DriverName)".IndexOf($model, [StringComparison]::OrdinalIgnoreCase) -ge 0)
                    } | ForEach-Object { $_.Name })
                    $dups = @($dups | Select-Object -Unique)
                }
                $removed = @()
                if ($replace) {
                    foreach ($d in $dups) { Remove-Printer -Name $d; $removed += $d }
                    if (Get-Printer -Name $n -ErrorAction SilentlyContinue) { Remove-Printer -Name $n }
                    if ($removed.Count -gt 0 -or $dups.Count -gt 0) { Start-Sleep -Milliseconds 500 }
                } else {
                    $base = $n; $i = 2
                    while (Get-Printer -Name $n -ErrorAction SilentlyContinue) { $n = "$base ($i)"; $i++ }
                }
                $params = @{ Name = $n; DriverName = $driver; PortName = $port }
                if ($location) { $params.Location = $location }
                Add-Printer @params
                $state.Name = $n
                [Console]::Out.WriteLine('##PP|@name|OK|' + $n)
                if ($removed.Count -gt 0) { 'ajoutée, remplace : ' + ($removed -join ', ') }
                elseif ($dups.Count -gt 0) { 'WARN:ajoutée, mais déjà installée sous : ' + ($dups -join ', ') }
                else { 'ajoutée' }
            }
            if (-not $ok) { exit 1 }

            if ($duplex -or $mono) {
                Step 'Réglages' {
                    $cfg = @{ PrinterName = $state.Name }
                    if ($duplex) { $cfg.DuplexingMode = 'TwoSidedLongEdge' }
                    if ($mono) { $cfg.Color = $false }
                    try { Set-PrintConfiguration @cfg; (@(if ($duplex) { 'recto-verso' }; if ($mono) { 'noir et blanc' }) -join ', ') }
                    catch { 'WARN:réglages refusés par le pilote (' + $_.Exception.Message + ')' }
                } | Out-Null
            }

            # Windows affiche parfois « Hors connexion » à tort quand l'état SNMP du port ne répond pas : on corrige
            Step 'État' {
                try {
                    Start-Sleep -Seconds 3
                    $p = Get-Printer -Name $state.Name -ErrorAction SilentlyContinue
                    if ($p -and "$($p.PrinterStatus)" -match 'Offline' -and $snmp) {
                        $escapedPort = $port.Replace('\', '\\').Replace("'", "\'")
                        Get-CimInstance -ClassName Win32_TCPIPPrinterPort -Filter ("Name='{0}'" -f $escapedPort) | Set-CimInstance -Property @{ SNMPEnabled = $false }
                        'WARN:Windows indiquait « Hors connexion » : état SNMP du port désactivé (fréquent sur HP OfficeJet)'
                    } elseif ($p -and "$($p.PrinterStatus)" -match 'Offline') {
                        'WARN:imprimante hors connexion (éteinte ou IP incorrecte ?)'
                    } else { 'en ligne' }
                } catch { 'WARN:' + $_.Exception.Message }
            } | Out-Null

            if ($share) {
                Step 'Partage réseau' {
                    Set-Printer -Name $state.Name -Shared $true -ShareName $shareName
                    $rules = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue |
                        Where-Object { $_.Profile.ToString() -match 'Private|Domain|Any' })
                    if ($rules.Count -gt 0) { $rules | Enable-NetFirewallRule }
                    $unc = "\\$env:COMPUTERNAME\$shareName"
                    [Console]::Out.WriteLine('##PP|@share|OK|' + $unc)
                    "partagée : $unc"
                } | Out-Null
            }

            if ($noAutoSetup) {
                Step 'Auto-ajout Windows' {
                    try {
                        # « Configurer automatiquement les appareils connectés au réseau » : sinon Windows réinstalle sa copie avec un pilote générique
                        foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\NcdAutoSetup\Private', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\NcdAutoSetup\Public') {
                            if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }
                            New-ItemProperty -Path $k -Name 'AutoSetup' -Value 0 -PropertyType DWord -Force | Out-Null
                        }
                        'désactivé (Windows n''ajoutera plus ses propres copies)'
                    } catch { 'WARN:' + $_.Exception.Message }
                } | Out-Null
            }

            function Get-WmiPrinter([string]$n) {
                $escaped = $n.Replace('\', '\\').Replace("'", "\'")
                Get-CimInstance -ClassName Win32_Printer -Filter ("Name='{0}'" -f $escaped)
            }

            if ($setDefault) {
                Step 'Par défaut' {
                    try {
                        $key = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows'
                        Set-ItemProperty -Path $key -Name 'LegacyDefaultPrinterMode' -Value 1 -Type DWord
                        $wp = Get-WmiPrinter $state.Name
                        $r = Invoke-CimMethod -InputObject $wp -MethodName SetDefaultPrinter
                        if ($r.ReturnValue -ne 0) { "WARN:code $($r.ReturnValue)" } else { "pour l'utilisateur $env:USERNAME" }
                    } catch { 'WARN:' + $_.Exception.Message }
                } | Out-Null
            }

            if ($testPage) {
                Step 'Page de test' {
                    try {
                        $wp = Get-WmiPrinter $state.Name
                        $r = Invoke-CimMethod -InputObject $wp -MethodName PrintTestPage
                        if ($r.ReturnValue -ne 0) { "WARN:code $($r.ReturnValue)" } else { 'envoyée' }
                    } catch { 'WARN:' + $_.Exception.Message }
                } | Out-Null
            }
            exit 0

            """);
        return sb.ToString();
    }

    /// <summary>Après l'installation : imprimante par défaut et/ou page de test (choix demandés à la fin).</summary>
    public static string FinishPrinter(string printerName, bool setDefault, bool testPage)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine($"$state   = @{{ Name = {Quote(printerName)} }}");
        sb.AppendLine($"$setDefault = {Bool(setDefault)}");
        sb.AppendLine($"$testPage   = {Bool(testPage)}");
        sb.Append("""
            function Get-WmiPrinter([string]$n) {
                $escaped = $n.Replace('\', '\\').Replace("'", "\'")
                Get-CimInstance -ClassName Win32_Printer -Filter ("Name='{0}'" -f $escaped)
            }
            if ($setDefault) {
                Step 'Par défaut' {
                    try {
                        $key = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows'
                        Set-ItemProperty -Path $key -Name 'LegacyDefaultPrinterMode' -Value 1 -Type DWord
                        $wp = Get-WmiPrinter $state.Name
                        $r = Invoke-CimMethod -InputObject $wp -MethodName SetDefaultPrinter
                        if ($r.ReturnValue -ne 0) { "WARN:code $($r.ReturnValue)" } else { "pour l'utilisateur $env:USERNAME" }
                    } catch { 'WARN:' + $_.Exception.Message }
                } | Out-Null
            }
            if ($testPage) {
                Step 'Page de test' {
                    try {
                        $wp = Get-WmiPrinter $state.Name
                        $r = Invoke-CimMethod -InputObject $wp -MethodName PrintTestPage
                        if ($r.ReturnValue -ne 0) { "WARN:code $($r.ReturnValue)" } else { 'envoyée' }
                    } catch { 'WARN:' + $_.Exception.Message }
                } | Out-Null
            }
            exit 0

            """);
        return sb.ToString();
    }

    /// <summary>Imprimantes déjà installées sur ce PC : une ligne « @inst » par imprimante réseau (nom, pilote, version, IP).</summary>
    public static string ListInstalledPrinters() => Header + """
        $ports = @{}
        try { Get-PrinterPort -ErrorAction Stop | ForEach-Object { if ($_.PrinterHostAddress) { $ports[$_.Name] = $_.PrinterHostAddress } } } catch { }
        $drivers = @{}
        try {
            Get-PrinterDriver -ErrorAction Stop | ForEach-Object {
                $v = [uint64]$_.DriverVersion
                $drivers[$_.Name] = '{0}.{1}.{2}.{3}' -f ($v -shr 48), (($v -shr 32) -band 0xFFFF), (($v -shr 16) -band 0xFFFF), ($v -band 0xFFFF)
            }
        } catch { }
        foreach ($p in @(Get-Printer -ErrorAction SilentlyContinue)) {
            $ip = $ports[$p.PortName]
            if (-not $ip -and $p.PortName -match '^(?:IP_)?(\d{1,3}(?:\.\d{1,3}){3})') { $ip = $Matches[1] }
            # Copie WSD ajoutée automatiquement : pas d'IP, reconnue au modèle côté EFP
            if (-not $ip -and "$($p.PortName)" -like 'WSD*') { $ip = 'wsd' }
            if (-not $ip) { continue }
            $f = @($p.Name, $p.DriverName, $drivers[$p.DriverName], $ip) | ForEach-Object { "$_" -replace '[\r\n|\t]+', ' ' }
            [Console]::Out.WriteLine('##PP|@inst|OK|' + ($f -join "`t"))
        }
        exit 0

        """;

    public static string CreateScanFolder(ScanFolderOptions o)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine($"$folder = {Quote(o.FolderPath)}");
        sb.AppendLine($"$share  = {Quote(o.ShareName)}");
        sb.AppendLine($"$user   = {Quote(o.UserName)}");
        sb.AppendLine($"$pass   = {Quote(o.Password)}");
        sb.AppendLine($"$hide   = {Bool(o.HideUserFromLogonScreen)}");
        sb.AppendLine($"$shortcut = {Bool(o.CreateDesktopShortcut)}");
        sb.AppendLine($"$setPrivate = {Bool(o.SetNetworkPrivate)}");
        sb.Append("""

            $ok = Step 'Dossier' {
                if (Test-Path -LiteralPath $folder) { 'déjà présent'; return }
                New-Item -ItemType Directory -Path $folder -Force | Out-Null
                'créé'
            }
            if (-not $ok) { exit 1 }

            $ok = Step 'Utilisateur' {
                $sec = ConvertTo-SecureString $pass -AsPlainText -Force
                if (Get-LocalUser -Name $user -ErrorAction SilentlyContinue) {
                    Set-LocalUser -Name $user -Password $sec -PasswordNeverExpires $true
                    Enable-LocalUser -Name $user
                    'existait : mot de passe mis à jour'
                } else {
                    New-LocalUser -Name $user -Password $sec -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires -Description 'Compte de numerisation (EFP)' | Out-Null
                    'créé'
                }
            }
            if (-not $ok) { exit 1 }

            if ($hide) {
                Step 'Masquer le compte' {
                    $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
                    if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }
                    New-ItemProperty -Path $k -Name $user -Value 0 -PropertyType DWord -Force | Out-Null
                    "absent de l'écran de connexion"
                } | Out-Null
            }

            $ok = Step 'Droits NTFS' {
                $acct = "$env:COMPUTERNAME\$user"
                $r = & icacls.exe $folder /grant ("{0}:(OI)(CI)M" -f $acct) 2>&1
                if ($LASTEXITCODE -ne 0) { throw ((@($r)) -join ' ') }
                "modification pour $acct"
            }
            if (-not $ok) { exit 1 }

            $ok = Step 'Partage' {
                $svc = Get-Service -Name LanmanServer
                if ($svc.Status -ne 'Running') { Set-Service -Name LanmanServer -StartupType Automatic; Start-Service -Name LanmanServer }
                $acct = "$env:COMPUTERNAME\$user"
                $admins = (New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([System.Security.Principal.NTAccount]).Value
                $s = Get-SmbShare -Name $share -ErrorAction SilentlyContinue
                if ($s) {
                    if ($s.Path.TrimEnd('\') -ne $folder.TrimEnd('\')) { throw "Le partage '$share' existe déjà et pointe vers $($s.Path)" }
                    Grant-SmbShareAccess -Name $share -AccountName $acct -AccessRight Change -Force | Out-Null
                    'existait : droits ajoutés'
                } else {
                    New-SmbShare -Name $share -Path $folder -ChangeAccess $acct -FullAccess $admins | Out-Null
                    "\\$env:COMPUTERNAME\$share"
                }
            }
            if (-not $ok) { exit 1 }

            Step 'Pare-feu' {
                # Groupe "Partage de fichiers et d'imprimantes" désigné par son identifiant (indépendant de la langue)
                $rules = @(Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue |
                    Where-Object { $_.Profile.ToString() -match 'Private|Domain|Any' })
                if ($rules.Count -eq 0) { 'WARN:règles de partage introuvables'; return }
                $rules | Enable-NetFirewallRule
                "partage de fichiers autorisé (réseaux privés / domaine)"
            } | Out-Null

            Step 'Profil réseau' {
                $pub = @(Get-NetConnectionProfile -ErrorAction SilentlyContinue | Where-Object { $_.NetworkCategory -eq 'Public' })
                if ($pub.Count -eq 0) { 'privé / domaine'; return }
                $names = ($pub | ForEach-Object { $_.Name }) -join ', '
                if ($setPrivate) {
                    $pub | Set-NetConnectionProfile -NetworkCategory Private
                    "passé en Privé : $names"
                } else {
                    "WARN:réseau en mode Public ($names) : l'imprimante ne pourra pas écrire dans le partage"
                }
            } | Out-Null

            Step 'SMB1' {
                try {
                    $smb1 = (Get-SmbServerConfiguration).EnableSMB1Protocol
                    if ($smb1) { 'activé sur ce PC' } else { 'désactivé (normal) : les très vieilles imprimantes SMB1 ne pourront pas scanner' }
                } catch { 'WARN:état inconnu' }
            } | Out-Null

            if ($shortcut) {
                Step 'Raccourci' {
                    $desk = [Environment]::GetFolderPath('CommonDesktopDirectory')
                    $lnkPath = Join-Path $desk ($share + '.lnk')
                    $wsh = New-Object -ComObject WScript.Shell
                    $lnk = $wsh.CreateShortcut($lnkPath)
                    $lnk.TargetPath = $folder
                    $lnk.Save()
                    $lnkPath
                } | Out-Null
            }

            [Console]::Out.WriteLine('##PP|@computer|OK|' + $env:COMPUTERNAME)
            exit 0

            """);
        return sb.ToString();
    }

    /// <summary>Lit les lignes de résultat écrites par <c>Step</c>.</summary>
    public static List<(string Step, string Status, string Detail)> ParseOutput(string output)
    {
        var list = new List<(string, string, string)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var parts = line[(idx + Marker.Length)..].Split('|', 3);
            if (parts.Length < 2) continue;
            list.Add((parts[0], parts[1], parts.Length > 2 ? parts[2] : ""));
        }
        return list;
    }
}
