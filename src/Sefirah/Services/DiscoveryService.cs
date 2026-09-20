using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.EventArguments;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;

namespace Sefirah.Services;

public class DiscoveryService(
    ILogger logger,
    IMdnsService mdnsService,
    IDeviceManager deviceManager,
    ISessionManager sessionManager
    ) : IDiscoveryService, IUdpClientProvider
{
    private MulticastClient? udpClient; 
    private const string DEFAULT_BROADCAST = "255.255.255.255";
    private LocalDeviceEntity? localDevice;
    private readonly int port = 5149;
    private List<IPEndPoint> broadcastEndpoints = [];
    private const int DiscoveryPort = 5149;
    private CancellationTokenSource? discoveryLoopCts;
    private bool isNetworkSubscribed;

    public UdpBroadcast? BroadcastMessage { get; private set; }

    public async Task StartDiscoveryAsync()
    {
        try
        {
            localDevice = await deviceManager.GetLocalDeviceAsync();
            var name = await UserInformation.GetCurrentUserNameAsync();
            BroadcastMessage = new UdpBroadcast
            {
                DeviceId = localDevice.DeviceId,
                DeviceName = name,
                Port = NetworkService.ServerPort
            };

            mdnsService.AdvertiseService(BroadcastMessage, port);
            mdnsService.StartDiscovery();
            mdnsService.DiscoveredMdnsService += OnDiscoveredMdnsService;

            UpdateBroadcastEndpoints();

            udpClient = new MulticastClient("0.0.0.0", port, this, logger)
            {
                OptionDualMode = false,
                OptionMulticast = true,
                OptionReuseAddress = true,
            };
            udpClient.SetupMulticast(true);

            if (udpClient.Connect())
            {
                udpClient.Socket.EnableBroadcast = true;
                logger.Info($"UDP Client connected successfully {port}");
                BroadcastDeviceInfoAsync(BroadcastMessage);
            }
            else
            {
                logger.Error("Failed to connect UDP client");
            }

            StartPeriodicBroadcast();

            if (!isNetworkSubscribed)
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
                isNetworkSubscribed = true;
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Discovery initialization failed: {ex.Message}", ex);
        }
    }

    private void UpdateBroadcastEndpoints()
    {
        var localAddresses = NetworkHelper.GetAllValidAddresses();
        var endpoints = localAddresses.Select(ipInfo =>
        {
            var network = new Data.Models.IPNetwork(ipInfo.Address, ipInfo.SubnetMask);
            var broadcastAddress = network.BroadcastAddress;

            return broadcastAddress.Equals(IPAddress.Broadcast) && ipInfo.Gateway is not null
                ? new IPEndPoint(ipInfo.Gateway, DiscoveryPort)
                : new IPEndPoint(broadcastAddress, DiscoveryPort);
        }).Distinct().ToList();

        endpoints.Add(new IPEndPoint(IPAddress.Parse(DEFAULT_BROADCAST), DiscoveryPort));

        var addresses = deviceManager.GetRemoteDeviceAddresses();
        foreach (var address in addresses)
        {
            if (IPAddress.TryParse(address, out var parsedIp))
            {
                endpoints.Add(new IPEndPoint(parsedIp, DiscoveryPort));
            }
        }

        broadcastEndpoints = endpoints.Distinct().ToList();
        logger.Info($"Active broadcast endpoints: {string.Join(", ", broadcastEndpoints)}");
    }

    private void StartPeriodicBroadcast()
    {
        discoveryLoopCts?.Cancel();
        discoveryLoopCts?.Dispose();
        discoveryLoopCts = new CancellationTokenSource();
        var ct = discoveryLoopCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(ct);
                    if (BroadcastMessage is not null)
                    {
                        BroadcastDeviceInfoAsync(BroadcastMessage);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.Debug($"Periodic broadcast error: {ex.Message}");
                }
            }
        }, ct);
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        try
        {
            logger.Info("Network change detected. Refreshing broadcast endpoints and re-advertising discovery...");
            UpdateBroadcastEndpoints();

            // Rebind and reconnect UDP client if needed
            if (udpClient is null || !udpClient.IsConnected || udpClient.IsSocketDisposed)
            {
                try
                {
                    udpClient?.Disconnect();
                    udpClient?.Dispose();
                }
                catch { }

                udpClient = new MulticastClient("0.0.0.0", port, this, logger)
                {
                    OptionDualMode = false,
                    OptionMulticast = true,
                    OptionReuseAddress = true,
                };
                udpClient.SetupMulticast(true);

                if (udpClient.Connect())
                {
                    udpClient.Socket.EnableBroadcast = true;
                    logger.Info($"UDP Client reconnected on network change {port}");
                }
            }

            if (BroadcastMessage is not null)
            {
                BroadcastMessage.Port = NetworkService.ServerPort;
                mdnsService.UnAdvertiseService();
                mdnsService.AdvertiseService(BroadcastMessage, port);
                BroadcastDeviceInfoAsync(BroadcastMessage);
            }

            // Check paired devices
            foreach (var device in deviceManager.PairedDevices)
            {
                if (device.IsForcedDisconnect) continue;

                // If device is connected via Wi-Fi but its address is NOT on our current local subnet,
                // that connection is dead. Disconnect it so it can reconnect to the new IP.
                if (device.IsConnected && !string.IsNullOrEmpty(device.Address) && device.Address != "127.0.0.1")
                {
                    if (!NetworkHelper.IsOnLocalSubnet(device.Address))
                    {
                        logger.Info($"Device {device.Name} is on stale subnet IP {device.Address}. Disconnecting stale connection.");
                        sessionManager.DisconnectDevice(device);
                    }
                }

                // If device is not connected, attempt auto-connect to Wi-Fi addresses matching current subnet
                if (!device.IsConnected)
                {
                    var validWifiAddrs = device.Addresses
                        .Where(a => a.IsEnabled && !string.IsNullOrEmpty(a.Address) && !a.Address.StartsWith("127.") && NetworkHelper.IsOnLocalSubnet(a.Address))
                        .ToList();

                    if (validWifiAddrs.Count > 0)
                    {
                        logger.Info($"Network changed: attempting Wi-Fi connection for {device.Name}");
                        sessionManager.Connect(device);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Error processing network change: {ex.Message}");
        }
    }

    private void OnDiscoveredMdnsService(object? sender, DiscoveredMdnsServiceArgs e)
    {
        sessionManager.Connect(e.DeviceId, e.Address, e.Port > 0 ? e.Port : 5150);
    }

    private async void BroadcastDeviceInfoAsync(UdpBroadcast udpBroadcast)
    {
        if (udpClient is null || udpBroadcast is null) return;
        
        string jsonMessage = JsonMessageSerializer.Serialize(udpBroadcast);
        byte[] messageBytes = Encoding.UTF8.GetBytes(jsonMessage);
        foreach (var endPoint in broadcastEndpoints)
        {
            try
            {
                udpClient.Socket.SendTo(messageBytes, endPoint);
            }
            catch
            {
                // ignore
            }
        }
    }

    public async void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
    {
        try
        {
            var message = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
            var address = ((IPEndPoint)endpoint).Address;
            if (JsonMessageSerializer.DeserializeMessage(message) is not UdpBroadcast broadcast) return;

            if (broadcast.DeviceId == localDevice?.DeviceId || address is null) return;

            sessionManager.Connect(broadcast.DeviceId, address.ToString(), broadcast.Port > 0 ? broadcast.Port : 5150);
        }
        catch (Exception ex)
        {
            logger.Warn($"Error processing UDP message: {ex.Message}", ex);
        }
    }

    public void StopDiscovery()
    {
        try
        {
            if (isNetworkSubscribed)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
                isNetworkSubscribed = false;
            }

            discoveryLoopCts?.Cancel();
            discoveryLoopCts?.Dispose();
            discoveryLoopCts = null;

            mdnsService.DiscoveredMdnsService -= OnDiscoveredMdnsService;
            mdnsService.UnAdvertiseService();
            udpClient?.Dispose();
            udpClient = null;
        }
        catch (Exception ex)
        {
            logger.Error($"Error disposing default UDP client: {ex.Message}", ex);
        }
    }

    public async Task<BitmapImage?> GenerateQrCodeAsync()
    {
        try
        {
            var broadcast = BroadcastMessage;
            if (broadcast is null)
            {
                return null;
            }

            var localAddresses = NetworkHelper.GetAllValidAddresses();
            var addresses = localAddresses.Select(addr => addr.Address.ToString()).ToList();

            // If a USB ADB device is connected, include loopback address first for USB-only pairing
            var adbService = Ioc.Default.GetService<IAdbService>();
            if (adbService?.AdbDevices.Any(d => d.Type == DeviceType.USB && d.IsOnline) == true)
            {
                addresses.Remove("127.0.0.1");
                addresses.Insert(0, "127.0.0.1");
            }

            var port = NetworkService.ServerPort > 0 ? NetworkService.ServerPort : broadcast.Port;

            var payload = new QrCodePayload
            {
                Addresses = addresses,
                Port = port,
                DeviceId = broadcast.DeviceId,
                DeviceName = broadcast.DeviceName
            };
            var json = JsonMessageSerializer.Serialize(payload);
            var deepLink = $"{Constants.AppInfo.ProtocolScheme}://pair?data={Uri.EscapeDataString(json)}";
            logger.Info($"Generated QR pairing deepLink for {payload.DeviceName} (IP: {addresses.FirstOrDefault()}:{payload.Port})");

            var qrCodeBytes = ImageHelper.GenerateQrCode(deepLink);
            if (qrCodeBytes is null)
            {
                return null;
            }

            return await qrCodeBytes.ToBitmapAsync(256);
        }
        catch (Exception ex)
        {
            logger.Warn($"Error generating QR code: {ex.Message}", ex);
            return null;
        }
    }

}
