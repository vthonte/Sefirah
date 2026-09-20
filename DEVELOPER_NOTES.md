# Sefirah AI Developer Notes & Context Guide

This document is written for future developers and AI assistants continuing work on the **Sefirah** multi-device integration ecosystem (Windows Desktop WinUI 3 App + Android Jetpack Compose App).

---

## 1. System Architecture Overview

Sefirah is a cross-device continuity app (similar to KDE Connect / Microsoft Phone Link) connecting Android devices and Windows PCs.

- **Desktop App Repository**: `j:\mydata\work\Sefirah` (WinUI 3 / .NET 10 preview / C#)
- **Android App Repository**: `j:\mydata\work\Sefirah-Android` (Kotlin, Jetpack Compose, Coroutines/Flow, Hilt, KSP)
- **Active Git Branch**: `feature/ai` on both repositories.

---

## 2. Network Topology & Ports

| Port | Protocol | Usage | Direction / Binding |
|------|----------|-------|---------------------|
| **5150** | TCP (TLSv1.2) | Sefirah Core Protocol | **Desktop** binds `0.0.0.0:5150`. **Android** binds `0.0.0.0:5150` for its own server. |
| **5149** | UDP | Device Discovery Broadcast | Multicast/broadcast on all active network adapters |
| **5151** | TCP (SFTP) | SFTP Remote Storage | **Android** runs SSH/SFTP server on port 5151 for Browse Files |
| **5152** | TCP (Loopback) | **Phone -> Desktop USB Tunnel** | `adb reverse tcp:5152 tcp:5150`. Android connects to `127.0.0.1:5152` which tunnels to Desktop port 5150. |
| **5153** | TCP (Loopback) | **Desktop -> Phone USB Tunnel** | `adb forward tcp:5153 tcp:5150`. Desktop connects to `127.0.0.1:5153` which tunnels to Android port 5150. |
| **5555** | TCP | Wireless ADB Daemon | Standard Android ADB over Wi-Fi port |

### ⚠️ CRITICAL NETWORKING RULES & PITFALLS
1. **DO NOT run `adb forward tcp:5150 tcp:5150`!**
   - Sefirah Desktop binds `0.0.0.0:5150`.
   - Running `adb forward tcp:5150 tcp:5150` makes the Windows `adb.exe` daemon bind `127.0.0.1:5150`, hijacking localhost port 5150.
   - Any connection to `127.0.0.1:5150` or from `adb reverse tcp:5152 tcp:5150` loops back into ADB itself, causing complete failure.
   - Use **`tcp:5153`** for Desktop -> Phone forward tunnel (`adb forward tcp:5153 tcp:5150`).
2. **DO NOT set Port to 5152 in the QR Code payload!**
   - The QR code payload (`QrCodePayload`) has a single `Port` field (`Port: 5150`).
   - If `Port: 5152` is written into the QR payload, Wi-Fi connections fail because Desktop has no server listening on 5152.
   - Port MUST always be `5150`. Android `NetworkService.kt` specifically routes `127.0.0.1` to port `5152`.
3. **DO NOT attempt wireless ADB (`adb connect 127.0.0.1:5555`)!**
   - In `AutoSetupWirelessAdbAsync`, always filter out `127.0.0.1` and `127.*` addresses.
   - Running `adb tcpip 5555` on a loopback target causes the USB connection to reset/drop in a loop. Only connect to real Wi-Fi IPs (`192.168.x.x`).

---

## 3. Pairing & Discovery Mechanisms

### QR Code Pairing
- Generated in `DiscoveryService.cs` (`GenerateQrCodeAsync`).
- Deep link format: `sefirah-ai://pair?data=<UrlEncodedJsonPayload>`.
- `addresses`: List of valid IPs. When USB is connected, `127.0.0.1` is inserted at index 0.
- Android parser: `QrCodeParser.kt` decodes the deep link.
- In `QrConnectionDialog.kt`, users can tap any discovered IP or enter a custom IP.

### Automatic USB Discovery
- In Android `NetworkDiscovery.kt`:
  `probeUsbDevice()` connects to `127.0.0.1:5152` (adb reverse).
  If open, it calls `networkManager.connectTo(...)` (or `connectPaired` if already paired).
  This allows Desktop to show up under **Available Devices** on the phone over pure USB without Wi-Fi or QR scanning!

### Wi-Fi Discovery
- Android and Desktop exchange `UdpBroadcast` packets on UDP port `5149` and advertise via mDNS / NSD.

---

## 4. Connection State & Status Unification

### Desktop (`PairedDevice.cs`)
- `ConnectionStatus` (represents the **TCP protocol session**): `Connected`, `Connecting`, `Disconnected`.
- `HasAdbConnection`: Boolean indicating if an online ADB device matches this paired device.
- `IsConnected`: `ConnectionStatus.IsConnected || HasAdbConnection`. (True if EITHER TCP or ADB is established).
- `IsDisconnected`: `ConnectionStatus.IsDisconnected && !HasAdbConnection`.
- `ConnectionStatusText`: Returns `"Connected (USB)"` or `"Connected (ADB)"` when `HasAdbConnection` is true, or `"Connected"` if TCP is connected.
- **IMPORTANT**: In `NetworkService.cs` `Connect()`, check `existingDevice.ConnectionStatus.IsConnectedOrConnecting`, NOT `existingDevice.IsConnectedOrConnecting`! Otherwise, having an ADB connection blocks Desktop from ever establishing the TCP protocol connection!

### ADB Device Matching (`IsMatchingAdbDevice`)
Matching order:
1. `adbDevice.AndroidId == Id` (retrieved via `cat /storage/emulated/0/Android/data/com.castle.sefirah.ai/files/device_info.txt`).
2. IP address matching (for Wi-Fi ADB serial `<IP>:<PORT>`).
3. Normalized model name matching (strips non-alphanumeric chars; `SM-S918B` matches `SM_S918B`).
4. **Single-device fallback**: If `PairedDevices.Count == 1`, any online ADB device belongs to this device.
5. In `DeviceControlCenter.xaml.cs`, `PaneFlyout_Opened` calls `ViewModel.Device?.RefreshConnectedAdbDevices()` to ensure the list is always populated when clicked.

---

## 5. Storage / Browse Files (SFTP)

- Android starts SFTP server on port `5151` (`SftpService`).
- Windows connects to `127.0.0.1:5151` (via `adb forward tcp:5151 tcp:5151` when USB is connected) or Wi-Fi IP.
- Mount point: `%USERPROFILE%\RemoteDevices\<DeviceName>`.
- `BrowseAsync` in `SftpFeature.cs` ensures directory creation via `Directory.CreateDirectory(deviceDirectory)` and opens in File Explorer (`explorer.exe "<folderPath>"`).

---

## 6. Scrcpy & Screen Mirroring

- `ScreenMirrorService.cs`:
  - `FlexDisplay` default set to `true` in `DeviceSettingsService.cs`.
  - Scrcpy executable: `C:\Users\HP\Downloads\scrcpy-win64-v4.1\scrcpy-win64-v4.1\scrcpy.exe`.
  - Automatically selects USB device (`-s <Serial>`) when available (`ScrcpyDevicePreferenceType.Auto`).

---

## 7. Notification Handling (WhatsApp / Upload Spam Fix)

- In Android `NotificationFeature.kt`:
  - Ongoing progress notifications (e.g. sending file, downloading media) have `EXTRA_PROGRESS >= 0`.
  - Updated notifications with existing keys send `NotificationInfoType.Active` instead of `NotificationInfoType.New`.
  - This prevents Windows from creating repetitive toast popup notifications while files are uploading.

---

## 8. Build, Upgrade, & Installation Workflow

### Desktop Build & In-Place Upgrade
- **Script**: `powershell -ExecutionPolicy Bypass -File scripts\Build-Local.ps1 -Install`
- **In-Place Upgrades**: Uses `Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion`.
  - **DO NOT USE `Remove-AppxPackage`**: Uninstalling deletes the SQLite database (`%LOCALAPPDATA%\Packages\vthonte.Sefirah-AI_9yhjgvpvzzxz2\LocalState\sefirah.db`) and user certificates, forcing the user to re-pair!
  - Always bump `<Identity Version="x.y.z.0" ... />` in `src/Sefirah/Package.appxmanifest` for every build.

### Android Build & Install
- `.\gradlew.bat assembleDebug`
- `& "C:\Users\HP\Downloads\scrcpy-win64-v4.1\scrcpy-win64-v4.1\adb.exe" -s RZCXC020ZME install -r "app\build\outputs\apk\debug\app-debug.apk"`

---

## 9. Current Device Hardware & Testing Setup
- **PC**: Windows 11 Desktop (x64), Package Name `vthonte.Sefirah-AI_9yhjgvpvzzxz2`.
- **Phone**: Samsung Galaxy S23 Ultra (`SM-S918B`), Serial `RZCXC020ZME`, Package ID `com.castle.sefirah.ai`.
- **ADB Path**: `C:\Users\HP\Downloads\scrcpy-win64-v4.1\scrcpy-win64-v4.1\adb.exe`.

---

## 10. USB Connection Stability & Controls Guide

### ⚠️ NEVER RUN `adb tcpip 5555` AUTOMATICALLY
- In previous versions, whenever USB connected or `TryConnectTcp` failed, `EnableTcpipMode` ran `adb tcpip 5555`.
- **`adb tcpip 5555` restarts the Android `adbd` daemon, dropping the physical USB interface.**
- This caused:
  - Windows USB disconnect chime ("da-dum").
  - `scrcpy` crashes.
  - Sockets and port forwards (`reverse`/`forward`) disconnecting.
  - Infinite reconnect/disconnect loops every time the phone connected, synced, or unlocked.
- **Rule**: `TryConnectTcp` only attempts `ConnectWireless` if a valid non-loopback Wi-Fi IP is available. It MUST NEVER run `adb tcpip 5555` automatically.

### DND & Volume Control
- Setting ringer mode, DND (`setInterruptionFilter`), or volume requires `android.permission.ACCESS_NOTIFICATION_POLICY` and `ACCESS_NOTIFICATIONS` appop.
- Desktop's `AdbService.GrantSensitiveNotificationAsync` automatically grants these via ADB whenever the device is connected.

### Call Logs Synchronization
- `CallLogFeature` extends `BoundFeature` and registers a `ContentObserver` on `CallLog.Calls.CONTENT_URI`.
- On connection, initial call logs are synced.
- When any call is made/received, `ContentObserver.onChange` pushes the latest calls to Desktop in real-time.

### Play Sound (Find My Phone)
- Rings the phone using `RingtoneManager.getActualDefaultRingtoneUri` with fallbacks.
- Desktop button toggles between Play and Stop.
- Can be stopped either from the phone screen or by clicking the Desktop button again.

---

## 11. Bluetooth Auto-Enable, 5-Second Pings, & Disconnect Override

### Bluetooth Auto-Enable
- On Desktop: `BluetoothRadioManager.TryEnableAsync()` uses `Radio.SetStateAsync(RadioState.On)`. The Bluetooth setup dialog includes a direct "Turn on Bluetooth" button and auto-attempts enabling when starting setup.
- On Android: `BluetoothPairingHandler` does not prematurely reject pairing requests when Bluetooth is off. `BluetoothDiscoverableActivity` tries `adapter.enable()` and falls back to `Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE)` to present the standard system prompt before proceeding to discoverable mode.

### 5-Second Keep-Alive Pings
- Both Desktop and Android support `Ping` and `Pong` `SocketMessage` types.
- Desktop runs a 5-second periodic keep-alive loop (`NetworkService.StartKeepAliveLoop`).
- If no message or Pong is received from a connected device within 15 seconds (3 missed pings), Desktop drops the dead socket and initiates clean reconnection.
- Android's `MessageHandler` automatically replies with `Pong` upon receiving `Ping`.

### Connection Priority & Fast Wi-Fi Detection
- **Priority**: USB loopback (`127.0.0.1:5153` forward / `5152` reverse) is always prioritized first before Wi-Fi IPs.
- **Fast Wi-Fi**: Both Desktop (`NetworkChange.NetworkAddressChanged`) and Android (`ConnectivityManager.NetworkCallback`) detect Wi-Fi network changes immediately.
- A 10-second periodic UDP broadcast loop runs on both sides to discover network IPs rapidly.
- When connected via USB (`127.0.0.1`) and discovered on Wi-Fi, the connection is seamlessly upgraded to the Wi-Fi IP so wireless ADB and all network features activate without delay.

### Mutual Manual Disconnect Override & Protocol Architecture
1. **The Core Issue with Auto-Reconnection**:
   - Previously, clicking Disconnect on PC simply closed the socket without sending a `Disconnect` packet. Android received socket EOF (`onClose`), which reset Android's connection state to `Disconnected(forcedDisconnect = false)`.
   - Android's background probe loops (`probeUsbDevice`, `probePairedDevices`, UDP broadcasts) immediately saw `isForcedDisconnect == false` and re-established the TLS socket within 1 second.
   - Conversely, Android's `disconnect()` launched an asynchronous coroutine to send `Disconnect`, and immediately cancelled the coroutine scope and closed the socket before bytes flushed to the network.
   - Furthermore, `PairedDevice.IsConnected` on PC returned `ConnectionStatus.IsConnected || HasAdbConnection`. Because physical USB was plugged in (`HasAdbConnection == true`), Desktop UI considered the device perpetually connected, hiding the Connect button and breaking the disconnect UX.

2. **Clean Disconnect Protocol**:
   - **PC -> Phone**: When user clicks Disconnect in Desktop UI, Desktop synchronously sends `new Disconnect()` over `Session.Send()` or `Client.Send()` before calling `DisconnectSession`/`DisconnectClient`. PC marks `ConnectionStatus = new Disconnected(forcedDisconnect: true)`.
   - **Phone -> PC**: When user taps Disconnect in Android app, Android calls `connections[id]?.sendMessageSync(Disconnect)` using a synchronous mutex lock and stream flush before tearing down the connection, marking `connectionState = ConnectionState.Disconnected(forcedDisconnect = true)`.
   - **Involuntary Socket Drop Preservation**: In both Android's `onClose` and PC's `SetDisconnected`, the forced-disconnect status is preserved:
     `val isForced = forcedDisconnect || device.connectionState.isForcedDisconnect`.
     A network drop or socket close will **never** clear a user's intentional forced disconnect!
   - **Desktop UI State Fix**: In `PairedDevice.cs`:
     `IsConnected => (ConnectionStatus.IsConnected || HasAdbConnection) && !IsForcedDisconnect;`
     `IsDisconnected => (ConnectionStatus.IsDisconnected && !HasAdbConnection) || IsForcedDisconnect;`
     `ConnectionStatusText => IsForcedDisconnect ? "Disconnected" : ...`
     When forced disconnected, the device shows "Disconnected" and the flyout displays the "Connect" button even if the physical USB cable is plugged in.

3. **Mutual Manual Reconnect Override Handshake**:
   - While `isForcedDisconnect == true`, all background probes (USB probe, UDP broadcast, mDNS) are suppressed and rejected by both sides.
   - When the user explicitly clicks **Connect** or **Refresh** on Laptop:
     - Laptop resets `IsForcedDisconnect = false` and initiates connection with `IsManualReconnect = true` in the `Authentication` payload.
     - Phone's TLS server receives `Authentication(isManualReconnect = true)`.
     - Phone recognizes this as an intentional manual reconnect, clears `isForcedDisconnect`, and transitions to `Connected`.
   - When the user explicitly taps **Connect** or **Sync** on Phone:
     - Phone sets `isManualReconnect = true` in its `Authentication` payload.
     - Laptop receives the incoming connection, sees `authMessage.IsManualReconnect == true`, clears `IsForcedDisconnect`, and transitions to `Connected`.
   - Result: Users can disconnect from either device, and reconnect from either device at any time, with zero manual intervention on the other device.

---

## 12. Dynamic Multi-Network Failover, Auto-Promotion, & Immediate Wireless ADB

### Dynamic Multi-Network Connection Pool
- Any common network between Desktop and Android (Wi-Fi, Ethernet, Mobile Hotspot, USB reverse/forward tunnel) is automatically registered into `device.Addresses` / `device.addresses`.
- Newly discovered IPs from UDP broadcast (port 5149), mDNS, or incoming TLS handshakes are immediately saved via `TryAddAddress` on Desktop and merged into database on Android.

### Speed & Priority Hierarchy (Auto-Promotion)
- **Priority 1: USB Loopback (`127.0.0.1`)** — highest speed (~480Mbps+), <1ms latency, zero wireless interference.
- **Priority 2: Matching Local Subnet Wi-Fi / Hotspot / LAN** — high bandwidth, local network.
- **Priority 3: Other reachable networks.**
- **Seamless Auto-Promotion**: When connected via Wi-Fi and USB is plugged in, both sides detect the USB tunnels:
  - On Desktop, `AdbService` fires `UsbDeviceReady` upon completing `SetupUsbPortForwardingAsync`, and `NetworkService` calls `Connect(pairedDevice, "127.0.0.1")`.
  - On Android, `probeUsbDevice()` detects port 5152 responsive and invokes `connectPaired` if `pairedUsb.address != "127.0.0.1"`.
  - The new loopback session/client is assigned *before* disconnecting the old Wi-Fi socket, ensuring uninterrupted connected status in the UI.

### Sub-Second Failover (Zero-Drop)
- When USB is unplugged or the active network drops unexpectedly:
  - If `!forcedDisconnect` and `!device.IsForcedDisconnect`, the app **does not drop to "Disconnected"**.
  - On Desktop (`DisconnectSession` / `DisconnectClient`), it instantly extracts cached candidate addresses on the local subnet and initiates `ConnectCore(device, fallbackAddrs)`. Handshake timeout is 3s for fast traversal.
  - On Android (`startListeningForDevice` `onClose`), it immediately attempts fallback Wi-Fi addresses before declaring disconnection.
  - The device transitions to `Connecting` -> `Connected` seamlessly without the user seeing a disconnect. Only if all candidates fail does it enter `Disconnected`.

### Network Subnet Migration
- When switching Wi-Fi networks (e.g. from Home Wi-Fi to Hotspot or Office network):
  - In `DiscoveryService.cs` on Desktop and `NetworkDiscovery.kt` on Android, if the device is currently connected to an IP from a stale/dead subnet, but discovery receives a broadcast on the new active subnet, the system dynamically migrates and reconnects to the new matching subnet IP.

### Immediate Wireless ADB
- As soon as a USB device connects, `AdbService.AutoSetupWirelessAdbAsync` immediately enables TCP mode (`adb -s <serial> tcpip 5555`), queries the device's Wi-Fi IP via `ip -o -4 addr show wlan0`, and connects to `<target_ip>:5555` with zero polling delay.
- When the devices connect over Wi-Fi (even without USB), `NetworkService` invokes `EnsureWirelessAdbForPairedDeviceAsync(device)`, which immediately connects to the device's port 5555 if wireless debugging / tcpip mode is already active.
