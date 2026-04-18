using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client.Platform;
using ScreenSharing.Client.Services;
using ScreenSharing.Desktop.App.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Desktop.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IdentityStore _identity;
    private readonly Func<SignalingClient> _signalingFactory;
    private readonly INavigationHost _navigation;
    private readonly ClientSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICaptureProvider? _captureProvider;
    private readonly ServerStatusProbe _statusProbe = new();

    private readonly Guid _userId;

    public HomeViewModel(
        IdentityStore identity,
        Func<SignalingClient> signalingFactory,
        INavigationHost navigation,
        ClientSettings settings,
        SettingsStore settingsStore,
        ICaptureProvider? captureProvider = null)
    {
        _identity = identity;
        _signalingFactory = signalingFactory;
        _navigation = navigation;
        _settings = settings;
        _settingsStore = settingsStore;
        _captureProvider = captureProvider;

        var profile = _identity.LoadOrCreate();
        _userId = profile.UserId;
        _displayName = profile.DisplayName;

        // Seed the picker from saved settings. If nothing is saved yet
        // (fresh install) synthesize a default "Local" entry so the
        // picker has something to show and the first Create/Join works
        // out of the box against a locally-running server.
        Connections = new ObservableCollection<ConnectionEntryViewModel>();
        SeedConnectionsFromSettings();

        _ = RefreshActiveStatusAsync();
    }

    private void SeedConnectionsFromSettings()
    {
        if (_settings.Connections.Count == 0)
        {
            var local = new ServerConnection
            {
                Name = "Local",
                Uri = new Uri("ws://localhost:5000/ws"),
            };
            _settings.Connections.Add(local);
            _settings.ActiveConnectionId = local.Id;
            _settingsStore.Save(_settings);
        }

        foreach (var c in _settings.Connections)
        {
            Connections.Add(new ConnectionEntryViewModel(c));
        }
        SyncActiveFlag();
    }

    private void SyncActiveFlag()
    {
        ActiveConnection = null;
        foreach (var row in Connections)
        {
            row.IsActive = row.Id == _settings.ActiveConnectionId;
            if (row.IsActive) ActiveConnection = row;
        }
    }

    /// <summary>Rows rendered by the picker. One is marked IsActive at a time.</summary>
    public ObservableCollection<ConnectionEntryViewModel> Connections { get; }

    [ObservableProperty] private ConnectionEntryViewModel? _activeConnection;

    /// <summary>Label shown in the picker's resting state — active name + host.</summary>
    public string ActiveConnectionSummary =>
        ActiveConnection is null ? "No connection — add one" : ActiveConnection.DisplayLabel;

    partial void OnActiveConnectionChanged(ConnectionEntryViewModel? value)
        => OnPropertyChanged(nameof(ActiveConnectionSummary));

    /// <summary>Flyout open/closed state.</summary>
    [ObservableProperty] private bool _isPickerOpen;

    // --- inline editor state ---

    [ObservableProperty] private bool _isEditingConnection;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editUri = string.Empty;
    [ObservableProperty] private string _editPassword = string.Empty;
    [ObservableProperty] private string? _editError;
    private Guid? _editingConnectionId;

    [RelayCommand]
    private void TogglePicker()
    {
        IsPickerOpen = !IsPickerOpen;
        if (IsPickerOpen)
        {
            _ = RefreshAllStatusesAsync();
        }
    }

    [RelayCommand]
    private void AddConnection()
    {
        _editingConnectionId = null;
        EditName = string.Empty;
        EditUri = "ws://";
        EditPassword = string.Empty;
        EditError = null;
        IsEditingConnection = true;
    }

    [RelayCommand]
    private void EditConnection(ConnectionEntryViewModel row)
    {
        if (row is null) return;
        _editingConnectionId = row.Id;
        EditName = row.Name;
        EditUri = row.Uri.ToString();
        EditPassword = row.Password ?? string.Empty;
        EditError = null;
        IsEditingConnection = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditingConnection = false;
        _editingConnectionId = null;
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (!Uri.TryCreate(EditUri.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "ws" && parsed.Scheme != "wss"))
        {
            EditError = "Enter a valid ws:// or wss:// URL.";
            return;
        }

        var name = EditName?.Trim() ?? string.Empty;
        var password = string.IsNullOrEmpty(EditPassword) ? null : EditPassword;

        if (_editingConnectionId is Guid existingId)
        {
            var row = Connections.FirstOrDefault(c => c.Id == existingId);
            if (row is not null)
            {
                row.Name = name;
                row.Uri = parsed;
                row.Password = password;
            }
        }
        else
        {
            var fresh = new ConnectionEntryViewModel(new ServerConnection
            {
                Id = Guid.NewGuid(),
                Name = name,
                Uri = parsed,
                Password = password,
            });
            Connections.Add(fresh);
            // If there was no active connection before, the new one wins.
            if (_settings.ActiveConnectionId is null) _settings.ActiveConnectionId = fresh.Id;
        }

        PersistConnections();
        SyncActiveFlag();
        IsEditingConnection = false;
        _editingConnectionId = null;
        await RefreshActiveStatusAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SetActiveAsync(ConnectionEntryViewModel row)
    {
        if (row is null || row.IsActive) return;
        _settings.ActiveConnectionId = row.Id;
        PersistConnections();
        SyncActiveFlag();
        IsPickerOpen = false;
        await RefreshActiveStatusAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task DeleteConnectionAsync(ConnectionEntryViewModel row)
    {
        if (row is null) return;
        Connections.Remove(row);
        // If the deleted row was active, fall back to the next remaining entry.
        if (_settings.ActiveConnectionId == row.Id)
        {
            _settings.ActiveConnectionId = Connections.FirstOrDefault()?.Id;
        }
        PersistConnections();
        SyncActiveFlag();
        await RefreshActiveStatusAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private Task RefreshStatusesAsync() => RefreshAllStatusesAsync();

    private void PersistConnections()
    {
        _settings.Connections = Connections.Select(r => r.ToServerConnection()).ToList();
        _settingsStore.Save(_settings);
    }

    private async Task RefreshActiveStatusAsync()
    {
        var active = ActiveConnection;
        if (active is null) return;
        active.Status = ServerStatus.Checking;
        var result = await _statusProbe.ProbeAsync(active.Uri,
            hasSavedPassword: !string.IsNullOrEmpty(active.Password)).ConfigureAwait(true);
        active.Status = result.Status;
    }

    private async Task RefreshAllStatusesAsync()
    {
        var tasks = Connections.Select(async row =>
        {
            row.Status = ServerStatus.Checking;
            var result = await _statusProbe.ProbeAsync(row.Uri,
                hasSavedPassword: !string.IsNullOrEmpty(row.Password)).ConfigureAwait(true);
            row.Status = result.Status;
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    [ObservableProperty]
    private string _displayName;

    /// <summary>
    /// When true, the home view swaps the "Joining as {name}" footer for an
    /// inline TextBox to edit the display name.
    /// </summary>
    [ObservableProperty]
    private bool _isEditingDisplayName;

    [ObservableProperty]
    private string _joinRoomId = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Enforces the 6-char uppercase alphanumeric shape as the user types
    /// or pastes. Anything else is silently dropped so the input always
    /// holds a valid (possibly partial) room ID.
    /// </summary>
    partial void OnJoinRoomIdChanged(string value)
    {
        var sanitized = SanitizeRoomId(value);
        if (sanitized != value)
        {
            JoinRoomId = sanitized;
        }
    }

    private static string SanitizeRoomId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        Span<char> buf = stackalloc char[6];
        var n = 0;
        foreach (var c in raw)
        {
            if (n == 6) break;
            if (c >= '0' && c <= '9') buf[n++] = c;
            else if (c >= 'A' && c <= 'Z') buf[n++] = c;
            else if (c >= 'a' && c <= 'z') buf[n++] = (char)(c - 32);
        }
        return new string(buf[..n]);
    }

    [RelayCommand]
    private void PasteRoomId()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                JoinRoomId = System.Windows.Clipboard.GetText();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Paste failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void BeginEditDisplayName() => IsEditingDisplayName = true;

    [RelayCommand]
    private void CommitDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            SaveDisplayName();
        }
        IsEditingDisplayName = false;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        Views.SettingsDialog.Show(_settings, _settingsStore);
    }

    [RelayCommand]
    private void ShowCaptureTest()
    {
        var captureVm = new CaptureTestViewModel(
            _navigation,
            () => new HomeViewModel(_identity, _signalingFactory, _navigation, _settings, _settingsStore, _captureProvider));
        _navigation.NavigateTo(captureVm);
    }

    [RelayCommand]
    private Task CreateRoomAsync()
    {
        if (!ValidateDisplayName()) return Task.CompletedTask;
        SaveDisplayName();
        return RunRoomOperationAsync(signaling => signaling.CreateRoomAsync());
    }

    [RelayCommand]
    private Task JoinRoomAsync()
    {
        if (!ValidateDisplayName()) return Task.CompletedTask;
        if (JoinRoomId.Length < 6)
        {
            StatusMessage = "Enter a 6-character room ID.";
            return Task.CompletedTask;
        }
        SaveDisplayName();
        return RunRoomOperationAsync(signaling => signaling.JoinRoomAsync(JoinRoomId));
    }

    private async Task RunRoomOperationAsync(Func<SignalingClient, Task> sendRequest)
    {
        IsBusy = true;
        StatusMessage = "Connecting...";

        var signaling = _signalingFactory();

        var joinTcs = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorTcs = new TaskCompletionSource<ServerErrorInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRoomJoined(RoomJoined j) => joinTcs.TrySetResult(j);
        void OnServerError(ErrorCode c, string m) => errorTcs.TrySetResult(new ServerErrorInfo(c, m));
        void OnConnectionLost(string? reason) => disconnectTcs.TrySetResult(reason);

        signaling.RoomJoined += OnRoomJoined;
        signaling.ServerError += OnServerError;
        signaling.ConnectionLost += OnConnectionLost;

        var transferredOwnership = false;
        try
        {
            var active = _settings.ActiveConnection;
            if (active is null)
            {
                StatusMessage = "No server selected. Open the picker and add one.";
                return;
            }
            var hello = new ClientHello(_userId, DisplayName.Trim(), ProtocolVersion.Current, AccessToken: active.Password);
            await signaling.ConnectAsync(active.Uri, hello).ConfigureAwait(true);
            await sendRequest(signaling).ConfigureAwait(true);

            var completed = await Task.WhenAny(joinTcs.Task, errorTcs.Task, disconnectTcs.Task)
                .ConfigureAwait(true);

            if (completed == joinTcs.Task)
            {
                signaling.RoomJoined -= OnRoomJoined;
                signaling.ServerError -= OnServerError;
                signaling.ConnectionLost -= OnConnectionLost;
                transferredOwnership = true;

                var roomVm = new RoomViewModel(
                    signaling,
                    _navigation,
                    _identity,
                    _signalingFactory,
                    _settings,
                    _settingsStore,
                    _captureProvider,
                    joinTcs.Task.Result);
                _navigation.NavigateTo(roomVm);
                return;
            }
            if (completed == errorTcs.Task)
            {
                var err = errorTcs.Task.Result;
                StatusMessage = ErrorFriendlyMessage(err);
                if (err.Code == ErrorCode.Unauthorized && ActiveConnection is not null)
                {
                    // Flip the picker dot yellow so the user understands the
                    // follow-up action is "edit the connection and fix the password."
                    ActiveConnection.Status = ServerStatus.AuthRequired;
                }
                return;
            }

            var reason = disconnectTcs.Task.Result;
            StatusMessage = string.IsNullOrEmpty(reason)
                ? "Disconnected from server. Please try again."
                : $"Disconnected: {reason}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not reach server: {ex.Message}";
        }
        finally
        {
            if (!transferredOwnership)
            {
                signaling.RoomJoined -= OnRoomJoined;
                signaling.ServerError -= OnServerError;
                signaling.ConnectionLost -= OnConnectionLost;
                await signaling.DisposeAsync().ConfigureAwait(true);
            }
            IsBusy = false;
        }
    }

    private bool ValidateDisplayName()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            StatusMessage = "Enter a display name first.";
            return false;
        }
        return true;
    }

    private void SaveDisplayName()
    {
        _identity.Save(new UserProfile { UserId = _userId, DisplayName = DisplayName.Trim() });
    }

    private static string ErrorFriendlyMessage(ServerErrorInfo err) => err.Code switch
    {
        ErrorCode.RoomNotFound => "Room not found. Check the id and try again.",
        ErrorCode.RoomFull => "That room is full.",
        ErrorCode.AlreadyInRoom => "You are already in a room.",
        ErrorCode.Unauthorized => "Incorrect or missing server password.",
        _ => $"Error: {err.Message}",
    };
}

public readonly record struct ServerErrorInfo(ErrorCode Code, string Message);
