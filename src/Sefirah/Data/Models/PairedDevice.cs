using System.Collections.Specialized;
using CommunityToolkit.WinUI;
using Sefirah.Data.Models.Messages;

namespace Sefirah.Data.Models;

public partial class PairedDevice : BaseRemoteDevice
{
    private string address = string.Empty;
    public string Address
    {
        get => address;
        set
        {
            if (SetProperty(ref address, value))
                RefreshAddressConnectionStates();
        }
    }

    private ObservableCollection<AddressEntry> addresses = [];
    public ObservableCollection<AddressEntry> Addresses
    {
        get => addresses;
        set
        {
            if (SetProperty(ref addresses, value))
                RefreshAddressConnectionStates();
        }
    }

    /// <summary>
    /// Gets enabled addresses
    /// </summary>
    public List<string> GetEnabledAddresses()
    {
        var enabledAddresses = Addresses
            .Where(ip => ip.IsEnabled)
            .Select(ip => ip.Address);
        
        // If no addresses are enabled, return all addresses
        if (!enabledAddresses.Any())
        {
            return Addresses
                .Select(ip => ip.Address)
                .ToList();
        }
        
        return enabledAddresses.ToList();
    }

    /// <summary>
    /// Adds an address to the list if it is not already present.
    /// </summary>
    /// <returns>True if the address was added.</returns>
    public bool TryAddAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        address = address.Trim();
        if (Addresses.Any(a => a.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
            return false;

        var entry = new AddressEntry
        {
            Address = address,
            IsEnabled = true
        };

        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() =>
            {
                if (!Addresses.Any(a => a.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
                {
                    Addresses.Add(entry);
                }
            });
        }
        else
        {
            Addresses.Add(entry);
        }
        return true;
    }

    public int Port { get; set; } = 5150;

    public List<PhoneNumber> PhoneNumbers { get; set; } = [];

    private ImageSource? wallpaper;
    public ImageSource? Wallpaper
    {
        get => wallpaper;
        set => SetProperty(ref wallpaper, value);
    }

    private ConnectionStatus connectionStatus = new Disconnected();
    public ConnectionStatus ConnectionStatus 
    {
        get => connectionStatus;
        set
        {
            if (SetProperty(ref connectionStatus, value))
            {
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsForcedDisconnect));
                OnPropertyChanged(nameof(IsConnectedOrConnecting));
                RefreshAddressConnectionStates();

                if (value.IsDisconnected)
                    IsPlayingSound = false;
            }
        }
    }

    private void RefreshAddressConnectionStates()
    {
        foreach (var entry in Addresses)
        {
            entry.IsConnected = IsConnected
                && !string.IsNullOrEmpty(Address)
                && entry.Address.Equals(Address, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string ConnectionStatusText
    {
        get
        {
            if (IsForcedDisconnect)
            {
                return "Disconnected.Text".GetLocalizedResource();
            }

            if (ConnectionStatus is Connected)
            {
                return "Connected.Text".GetLocalizedResource();
            }

            if (HasAdbConnection)
            {
                var hasUsb = ConnectedAdbDevices.Any(d => d.Type == DeviceType.USB);
                return hasUsb ? "Connected (USB)" : "Connected (ADB)";
            }

            return ConnectionStatus switch
            {
                Connecting => "Connecting",
                Disconnected => "Disconnected.Text".GetLocalizedResource(),
                _ => "Unknown"
            };
        }
    }
    public bool IsDisconnected => (ConnectionStatus.IsDisconnected && !HasAdbConnection) || IsForcedDisconnect;
    public bool IsConnected => (ConnectionStatus.IsConnected || HasAdbConnection) && !IsForcedDisconnect;
    public bool IsForcedDisconnect => ConnectionStatus.IsForcedDisconnect;
    public bool IsConnecting => ConnectionStatus.IsConnecting && !HasAdbConnection && !IsForcedDisconnect;
    public bool IsConnectedOrConnecting => (ConnectionStatus.IsConnectedOrConnecting || HasAdbConnection) && !IsForcedDisconnect;

    private BatteryState? batteryStatus;
    public BatteryState? BatteryStatus
    {
        get => batteryStatus;
        set => SetProperty(ref batteryStatus, value);
    }

    private int ringerMode = -1;
    public int RingerMode
    {
        get => ringerMode;
        set => SetProperty(ref ringerMode, value);
    }

    private bool dndEnabled;
    public bool DndEnabled
    {
        get => dndEnabled;
        set => SetProperty(ref dndEnabled, value);
    }

    private bool isPlayingSound;
    public bool IsPlayingSound
    {
        get => isPlayingSound;
        set => SetProperty(ref isPlayingSound, value);
    }

    public IReadOnlyList<AudioStream> Streams { get; } =
    [
        new(AudioStreamType.Media),
        new(AudioStreamType.Ring),
        new(AudioStreamType.Notification),
        new(AudioStreamType.Alarm),
        new(AudioStreamType.VoiceCall)
    ];

    public void UpdateStreamLevel(AudioStreamType streamType, int level)
    {
        var stream = Streams.FirstOrDefault(s => s.StreamType == streamType);
        stream?.Level = level;
    }

    public ObservableCollection<AdbDevice> ConnectedAdbDevices { get; set; } = [];

    public ObservableCollection<MediaSession> RemotePlaybackSessions { get; } = [];

    private MediaSession? lastPlayingSession;
    public MediaSession? LastPlayingSession
    {
        get => lastPlayingSession;
        set => SetProperty(ref lastPlayingSession, value);
    }

    private bool isActiveDevice;
    public bool IsActiveDevice
    {
        get => isActiveDevice;
        set => SetProperty(ref isActiveDevice, value);
    }

    private string? callsTransportDeviceId;
    public string? CallsTransportDeviceId
    {
        get => callsTransportDeviceId;
        set => SetProperty(ref callsTransportDeviceId, value);
    }

    private string? bluetoothAddress;
    public string? BluetoothAddress
    {
        get => bluetoothAddress;
        set => SetProperty(ref bluetoothAddress, value);
    }

    private string? bluetoothClassicDeviceId;
    public string? BluetoothClassicDeviceId
    {
        get => bluetoothClassicDeviceId;
        set => SetProperty(ref bluetoothClassicDeviceId, value);
    }

    private readonly IAdbService adbService = Ioc.Default.GetRequiredService<IAdbService>();
    private readonly IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();

    private IDeviceSettingsService deviceSettings;
    public IDeviceSettingsService DeviceSettings
    {
        get => deviceSettings;
        private set => SetProperty(ref deviceSettings, value);
    }

    public PairedDevice(string deviceId)
    {
        Id = deviceId;
        adbService.AdbDevices.CollectionChanged += OnAdbDevicesChanged;
        deviceSettings = userSettingsService.GetDeviceSettings(deviceId);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Model) or nameof(Address) or nameof(Addresses))
                RefreshConnectedAdbDevices();
        };
        RefreshConnectedAdbDevices();
    }

    private static string NormalizeModelName(string? model)
    {
        if (string.IsNullOrEmpty(model)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(model, @"[^a-zA-Z0-9]", "").ToUpperInvariant();
    }

    public bool IsMatchingAdbDevice(AdbDevice adbDevice)
    {
        if (adbDevice is null || !adbDevice.IsOnline) return false;

        // 1. Match by AndroidId
        if (!string.IsNullOrEmpty(adbDevice.AndroidId))
            return adbDevice.AndroidId == Id;

        // 2. Match by IP Address (for Wi-Fi ADB devices whose serial is <IP>:<PORT>)
        var adbHost = adbDevice.Serial.Split(':')[0];
        if (!string.IsNullOrEmpty(Address) && adbHost == Address)
            return true;

        if (Addresses.Any(a => !string.IsNullOrEmpty(a.Address) && adbHost == a.Address))
            return true;

        // 3. Match by Model (normalizing away underscores, hyphens, and spaces)
        if (!string.IsNullOrEmpty(adbDevice.Model) && !string.IsNullOrEmpty(Model))
        {
            var cleanAdbModel = NormalizeModelName(adbDevice.Model);
            var cleanDeviceModel = NormalizeModelName(Model);
            if (cleanDeviceModel.Equals(cleanAdbModel, StringComparison.OrdinalIgnoreCase) ||
                cleanDeviceModel.Contains(cleanAdbModel, StringComparison.OrdinalIgnoreCase) ||
                cleanAdbModel.Contains(cleanDeviceModel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 4. Fallback: If this is the only paired device, match any online ADB device
        var devMgr = Ioc.Default.GetService<IDeviceManager>();
        if (devMgr?.PairedDevices.Count == 1 && devMgr.PairedDevices[0].Id == Id)
            return true;

        return false;
    }

    public bool HasAdbConnection
    {
        get
        {
            try
            {
                return adbService?.AdbDevices.Any(IsMatchingAdbDevice) ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    private void OnAdbDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshConnectedAdbDevices();
        OnPropertyChanged(nameof(HasAdbConnection));
    }

    public async void RefreshConnectedAdbDevices()
    {
        try
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                ConnectedAdbDevices.Clear();

                var devices = adbService.AdbDevices
                    .Where(IsMatchingAdbDevice)
                    .ToList();

                ConnectedAdbDevices.AddRange(devices);

                OnPropertyChanged(nameof(HasAdbConnection));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsConnectedOrConnecting));
                OnPropertyChanged(nameof(ConnectionStatusText));
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in RefreshConnectedAdbDevices: {ex.Message}");
        }
    }
}

