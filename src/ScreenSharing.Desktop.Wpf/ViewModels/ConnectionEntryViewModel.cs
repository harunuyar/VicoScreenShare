using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ScreenSharing.Client.Services;

namespace ScreenSharing.Desktop.App.ViewModels;

/// <summary>
/// Row in the HomeView connection picker. Observable mirror of one
/// <see cref="ServerConnection"/> plus UI-only state (status dot, active flag).
/// The parent <see cref="HomeViewModel"/> owns the collection and the
/// commands that mutate it — this VM is a passive display shell.
/// </summary>
public sealed partial class ConnectionEntryViewModel : ObservableObject
{
    public ConnectionEntryViewModel(ServerConnection source)
    {
        Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id;
        _name = source.Name ?? string.Empty;
        _uri = source.Uri;
        _password = source.Password;
    }

    public Guid Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private Uri _uri;
    [ObservableProperty] private string? _password;

    /// <summary>True when this is the currently-selected connection.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Latest probe result for the row's status dot.</summary>
    [ObservableProperty] private ServerStatus _status = ServerStatus.Unknown;

    /// <summary>
    /// Display label — the user-supplied name if any, else the URI host.
    /// Recomputed on every Name / Uri change.
    /// </summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Name) ? Uri.Host : Name;

    /// <summary>Short URI summary for the secondary label in the picker row.</summary>
    public string UriSummary => $"{Uri.Scheme}://{Uri.Authority}{Uri.AbsolutePath}";

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));
    partial void OnUriChanged(Uri value)
    {
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(UriSummary));
    }

    /// <summary>Persist this VM's fields back to a settings-layer record.</summary>
    public ServerConnection ToServerConnection() => new()
    {
        Id = Id,
        Name = Name ?? string.Empty,
        Uri = Uri,
        Password = string.IsNullOrEmpty(Password) ? null : Password,
    };
}
