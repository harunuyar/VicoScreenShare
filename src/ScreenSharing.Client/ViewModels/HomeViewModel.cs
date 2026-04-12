using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSharing.Client.Services;
using ScreenSharing.Protocol;
using ScreenSharing.Protocol.Messages;

namespace ScreenSharing.Client.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IdentityStore _identity;
    private readonly SignalingClient _signaling;
    private readonly NavigationService _navigation;
    private readonly ClientSettings _settings;

    private readonly Guid _userId;

    public HomeViewModel(
        IdentityStore identity,
        SignalingClient signaling,
        NavigationService navigation,
        ClientSettings settings)
    {
        _identity = identity;
        _signaling = signaling;
        _navigation = navigation;
        _settings = settings;

        var profile = _identity.LoadOrCreate();
        _userId = profile.UserId;
        _displayName = profile.DisplayName;
    }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _createPassword = string.Empty;

    [ObservableProperty]
    private string _joinRoomId = string.Empty;

    [ObservableProperty]
    private string _joinPassword = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private Task CreateRoomAsync()
    {
        if (!ValidateDisplayName()) return Task.CompletedTask;
        SaveDisplayName();
        var password = string.IsNullOrWhiteSpace(CreatePassword) ? null : CreatePassword;
        return RunRoomOperationAsync(() => _signaling.CreateRoomAsync(password));
    }

    [RelayCommand]
    private Task JoinRoomAsync()
    {
        if (!ValidateDisplayName()) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(JoinRoomId))
        {
            StatusMessage = "Enter a room id to join.";
            return Task.CompletedTask;
        }
        SaveDisplayName();
        var password = string.IsNullOrWhiteSpace(JoinPassword) ? null : JoinPassword;
        var roomId = JoinRoomId.Trim().ToUpperInvariant();
        return RunRoomOperationAsync(() => _signaling.JoinRoomAsync(roomId, password));
    }

    /// <summary>
    /// Unified create/join lifecycle. Subscribes to <see cref="SignalingClient"/> events
    /// for the lifetime of this operation, completes on RoomJoined / ServerError /
    /// ConnectionLost (whichever comes first), and guarantees <see cref="IsBusy"/>
    /// returns to false via a try/finally. Subscriptions are always cleared in finally
    /// so a retry after failure starts from a clean slate.
    /// </summary>
    private async Task RunRoomOperationAsync(Func<Task> sendRequest)
    {
        IsBusy = true;
        StatusMessage = "Connecting...";

        var joinTcs = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorTcs = new TaskCompletionSource<ServerErrorInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRoomJoined(RoomJoined j) => joinTcs.TrySetResult(j);
        void OnServerError(ErrorCode c, string m) => errorTcs.TrySetResult(new ServerErrorInfo(c, m));
        void OnConnectionLost(string? reason) => disconnectTcs.TrySetResult(reason);

        _signaling.RoomJoined += OnRoomJoined;
        _signaling.ServerError += OnServerError;
        _signaling.ConnectionLost += OnConnectionLost;

        try
        {
            await ConnectIfNeededAsync().ConfigureAwait(true);
            await sendRequest().ConfigureAwait(true);

            var completed = await Task.WhenAny(joinTcs.Task, errorTcs.Task, disconnectTcs.Task)
                .ConfigureAwait(true);

            if (completed == joinTcs.Task)
            {
                NavigateToRoom(joinTcs.Task.Result);
                return;
            }
            if (completed == errorTcs.Task)
            {
                StatusMessage = ErrorFriendlyMessage(errorTcs.Task.Result);
                return;
            }
            // disconnectTcs
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
            _signaling.RoomJoined -= OnRoomJoined;
            _signaling.ServerError -= OnServerError;
            _signaling.ConnectionLost -= OnConnectionLost;
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

    private async Task ConnectIfNeededAsync()
    {
        if (_signaling.IsConnected) return;
        var hello = new ClientHello(_userId, DisplayName.Trim(), ProtocolVersion.Current);
        await _signaling.ConnectAsync(_settings.ServerUri, hello).ConfigureAwait(true);
    }

    private void NavigateToRoom(RoomJoined joined)
    {
        var roomVm = new RoomViewModel(_signaling, _navigation, joined);
        _navigation.NavigateTo(roomVm);
    }

    private static string ErrorFriendlyMessage(ServerErrorInfo err) => err.Code switch
    {
        ErrorCode.RoomNotFound => "Room not found. Check the id and try again.",
        ErrorCode.RoomFull => "That room is full.",
        ErrorCode.InvalidPassword => "Wrong password.",
        ErrorCode.AlreadyInRoom => "You are already in a room.",
        _ => $"Error: {err.Message}",
    };
}

public readonly record struct ServerErrorInfo(ErrorCode Code, string Message);
