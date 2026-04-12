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
    private readonly Func<SignalingClient> _signalingFactory;
    private readonly NavigationService _navigation;
    private readonly ClientSettings _settings;

    private readonly Guid _userId;

    public HomeViewModel(
        IdentityStore identity,
        Func<SignalingClient> signalingFactory,
        NavigationService navigation,
        ClientSettings settings)
    {
        _identity = identity;
        _signalingFactory = signalingFactory;
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
        return RunRoomOperationAsync(signaling => signaling.CreateRoomAsync(password));
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
        return RunRoomOperationAsync(signaling => signaling.JoinRoomAsync(roomId, password));
    }

    /// <summary>
    /// Unified create/join lifecycle. Builds a fresh <see cref="SignalingClient"/>
    /// per operation via the injected factory so there is no cross-operation event
    /// surface. On successful room join, ownership of the signaling instance is
    /// transferred to the new <see cref="RoomViewModel"/>; on any other exit path
    /// the instance is disposed. <see cref="IsBusy"/> is guaranteed to return to
    /// <c>false</c> via the try/finally regardless of outcome.
    /// </summary>
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
                // Hand the signaling instance to the RoomViewModel. Unsubscribe our
                // own handlers first so the RoomViewModel starts from a clean slate.
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
                    joinTcs.Task.Result);
                _navigation.NavigateTo(roomVm);
                return;
            }
            if (completed == errorTcs.Task)
            {
                StatusMessage = ErrorFriendlyMessage(errorTcs.Task.Result);
                return;
            }

            // disconnectTcs won
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
        ErrorCode.InvalidPassword => "Wrong password.",
        ErrorCode.AlreadyInRoom => "You are already in a room.",
        _ => $"Error: {err.Message}",
    };
}

public readonly record struct ServerErrorInfo(ErrorCode Code, string Message);
