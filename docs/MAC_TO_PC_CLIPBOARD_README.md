# Mac-to-PC Clipboard Handoff

This setup sends the current macOS text clipboard to the Windows clipboard with
one BetterTouchTool hotkey. It is one-way, text-only, and does not require
Remote Login on the Mac.

The Mac acts only as an SSH client. The PC runs OpenSSH Server and Magic Input.
Magic Input watches a local inbox folder in the logged-in Windows desktop
session, then imports text into the real Windows clipboard so `Ctrl+V` and
`Win+V` work normally.

## How It Works

```text
BetterTouchTool hotkey
  -> pbpaste
  -> ssh to Windows PC using a restricted SSH key
  -> receive-mac-clipboard.ps1 writes a .clip file
  -> Magic Input imports the file into the desktop clipboard
```

No new network listener is added by Magic Input. SSH remains the only remote
entry point.

## PC Prerequisites

- Windows OpenSSH Server is installed, running, and allowed through the Private
  firewall profile on the LAN.
- Magic Input is installed and running in the logged-in Windows desktop session.
- The Windows account used for SSH can write to its own `%APPDATA%` folder.

Install or update Magic Input from this repository:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\install-magic-input.ps1 -SkipDriver
```

The installer publishes the app and installs this helper:

```text
%LOCALAPPDATA%\Programs\MagicInput\receive-mac-clipboard.ps1
```

Magic Input watches:

```text
%APPDATA%\MagicInput\clipboard-inbox
```

## 1. Create A Dedicated SSH Key On The Mac

Use a dedicated key for this hotkey. For no password prompt, leave the key
passphrase empty when prompted:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/magicinput_pc_clipboard -C magicinput-pc-clipboard
```

Print the public key:

```bash
cat ~/.ssh/magicinput_pc_clipboard.pub
```

Copy the full `ssh-ed25519 ... magicinput-pc-clipboard` line.

## 2. Restrict That Key On The PC

Run this in an elevated PowerShell window on the PC. Replace the placeholders
before running it:

```powershell
$publicKey = 'ssh-ed25519 <public-key-body> magicinput-pc-clipboard'
$windowsUser = '<windows-user>'
$receiver = "C:/Users/$windowsUser/AppData/Local/Programs/MagicInput/receive-mac-clipboard.ps1"
$authorizedKeys = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'

New-Item -ItemType Directory -Force -Path (Split-Path $authorizedKeys) | Out-Null

$forcedCommand = 'command="powershell.exe -NoProfile -ExecutionPolicy Bypass -File ' + $receiver + '",no-agent-forwarding,no-port-forwarding,no-X11-forwarding,no-pty,no-user-rc '
[IO.File]::AppendAllText(
    $authorizedKeys,
    $forcedCommand + $publicKey + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false)
)

icacls.exe $authorizedKeys /inheritance:r /grant "*S-1-5-32-544:F" /grant "*S-1-5-18:F"
Restart-Service sshd
```

This is intentionally a restricted key. If this key is used for normal SSH or
SCP, the PC will run only the clipboard receiver instead of opening a shell or
file-transfer session.

## 3. Add A Mac SSH Alias

Edit `~/.ssh/config` on the Mac:

```sshconfig
Host pc-clip
  HostName <pc-lan-ip>
  User <windows-user>
  IdentityFile ~/.ssh/magicinput_pc_clipboard
  IdentitiesOnly yes
  IdentityAgent none
```

Test it:

```bash
printf 'hello from mac' | ssh pc-clip 'powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\<windows-user>\AppData\Local\Programs\MagicInput\receive-mac-clipboard.ps1"'
```

Then paste on Windows with `Ctrl+V` or open clipboard history with `Win+V`.

## 4. BetterTouchTool Hotkey

In BetterTouchTool, create a global keyboard shortcut and add a shell-script
action. The working one-line script is:

```zsh
/usr/bin/pbpaste | /usr/bin/ssh -o BatchMode=yes -o ConnectTimeout=5 pc-clip 'powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\<windows-user>\AppData\Local\Programs\MagicInput\receive-mac-clipboard.ps1"' && /usr/bin/osascript -e 'display notification "Sent to PC" with title "Magic Input"' || /usr/bin/osascript -e 'display notification "Clipboard send failed" with title "Magic Input"'
```

Replace `<windows-user>` with the Windows account name used by the PC install.

## Keeping SCP Working

Do not let the restricted clipboard key become the default identity for ordinary
SSH or SCP. If the key was added to the macOS agent, remove it:

```bash
ssh-add -d ~/.ssh/magicinput_pc_clipboard 2>/dev/null || true
```

Use the `pc-clip` alias only for clipboard handoff. For normal file transfer,
use a separate host alias. Password-based example:

```sshconfig
Host pc
  HostName <pc-lan-ip>
  User <windows-user>
  PubkeyAuthentication no
```

Then SCP normally:

```bash
scp pc:Desktop/example.txt ~/Desktop/
```

Alternatively, create a second unrestricted SSH key for normal PC access and
use it only under the `Host pc` alias.

## Limits And Safety Notes

- Text only.
- Maximum payload size is 4 MB.
- Clipboard contents may be sensitive. The setup is one-shot by design rather
  than continuous sync.
- The restricted SSH key should remain dedicated to this clipboard command.
- Keep the PC firewall rule scoped to the Private profile and local subnet.
