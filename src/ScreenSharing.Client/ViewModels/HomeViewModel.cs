using System;
using System.Threading;
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

    private Guid _userId;
    private TaskCompletionSource<RoomJoined>? _joinWaiter;
    private TaskCompletionSource<string>? _createWaiter;
    private TaskCompletionSource<ServerErrorInfo>? _errorWaiter;

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
    private async Task CreateRoomAsync()
    {
        if (!ValidateDisplayName()) return;
        SaveDisplayName();

        IsBusy = true;
        StatusMessage = "Connecting...";

        try
        {
            var password = string.IsNullOrWhiteSpace(CreatePassword) ? null : CreatePassword;
            await ConnectIfNeededAsync();

            _createWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _joinWaiter = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
            _errorWaiter = new TaskCompletionSource<ServerErrorInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

            await _signaling.CreateRoomAsync(password);

            var outcome = await Task.WhenAny(_joinWaiter.Task, _errorWaiter.Task).ConfigureAwait(true);
            if (outcome == _errorWaiter.Task)
            {
                var err = await _errorWaiter.Task.ConfigureAwait(true);
                StatusMessage = $"Error: {err.Message}";
                return;
            }

            var joined = await _joinWaiter.Task.ConfigureAwait(true);
            NavigateToRoom(joined);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create room: {ex.Message}";
        }
        finally
        {
            ClearWaiters();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (!ValidateDisplayName()) return;
        if (string.IsNullOrWhiteSpace(JoinRoomId))
        {
            StatusMessage = "Enter a room id to join.";
            return;
        }
        SaveDisplayName();

        IsBusy = true;
        StatusMessage = "Connecting...";

        try
        {
            var password = string.IsNullOrWhiteSpace(JoinPassword) ? null : JoinPassword;
            await ConnectIfNeededAsync();

            _joinWaiter = new TaskCompletionSource<RoomJoined>(TaskCreationOptions.RunContinuationsAsynchronously);
            _errorWaiter = new TaskCompletionSource<ServerErrorInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

            await _signaling.JoinRoomAsync(JoinRoomId.Trim().ToUpperInvariant(), password);

            var outcome = await Task.WhenAny(_joinWaiter.Task, _errorWaiter.Task).ConfigureAwait(true);
            if (outcome == _errorWaiter.Task)
            {
                var err = await _errorWaiter.Task.ConfigureAwait(true);
                StatusMessage = ErrorFriendlyMessage(err);
                return;
            }

            var joined = await _joinWaiter.Task.ConfigureAwait(true);
            NavigateToRoom(joined);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to join room: {ex.Message}";
        }
        finally
        {
            ClearWaiters();
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

        _signaling.RoomCreated += OnRoomCreated;
        _signaling.RoomJoined += OnRoomJoined;
        _signaling.ServerError += OnServerError;

        var hello = new ClientHello(_userId, DisplayName.Trim(), ProtocolVersion.Current);
        await _signaling.ConnectAsync(_settings.ServerUri, hello);
    }

    private void OnRoomCreated(string roomId) => _createWaiter?.TrySetResult(roomId);

    private void OnRoomJoined(RoomJoined joined) => _joinWaiter?.TrySetResult(joined);

    private void OnServerError(ErrorCode code, string message) =>
        _errorWaiter?.TrySetResult(new ServerErrorInfo(code, message));

    private void NavigateToRoom(RoomJoined joined)
    {
        _signaling.RoomCreated -= OnRoomCreated;
        _signaling.RoomJoined -= OnRoomJoined;
        _signaling.ServerError -= OnServerError;

        var roomVm = new RoomViewModel(_signaling, _navigation, joined);
        _navigation.NavigateTo(roomVm);
    }

    private void ClearWaiters()
    {
        _createWaiter = null;
        _joinWaiter = null;
        _errorWaiter = null;
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
