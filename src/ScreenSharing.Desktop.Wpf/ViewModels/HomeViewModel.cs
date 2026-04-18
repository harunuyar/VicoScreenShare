using System;
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
            var hello = new ClientHello(_userId, DisplayName.Trim(), ProtocolVersion.Current);
            await signaling.ConnectAsync(_settings.ServerUri, hello).ConfigureAwait(true);
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
                StatusMessage = ErrorFriendlyMessage(errorTcs.Task.Result);
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
        _ => $"Error: {err.Message}",
    };
}

public readonly record struct ServerErrorInfo(ErrorCode Code, string Message);
