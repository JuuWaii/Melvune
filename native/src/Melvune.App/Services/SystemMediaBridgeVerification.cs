#if DEBUG
using Melvune.Core;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Playback;

namespace Melvune.App.Services;

/// <summary>Runs once from the UI dispatcher during the consolidated Debug regression.</summary>
internal static class SystemMediaBridgeVerification
{
    internal static string Stage { get; private set; } = "not-started";
    internal static async Task RunAsync(DispatcherQueue dispatcher, nint windowHandle)
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("SMTC verification requires the UI dispatcher.");
        Stage = "media-player-create";
        using var player = new MediaPlayer { AutoPlay = false };
        var hub = new PlaybackSnapshotHub();
        Stage = "bridge-construct";
        var bridge = new SystemMediaBridge(player, windowHandle, dispatcher, hub, _ => Task.CompletedTask);
        using (bridge)
        {
            Stage = "controls-retrieve";
            var controls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            Stage = "command-manager-read";
            Require(!player.CommandManager.IsEnabled, "Automatic command handling must be disabled.");
            Stage = "initial-enabled-get";
            Require(!controls.IsEnabled, "Initial stopped session must be disabled.");
            Guid first = Guid.NewGuid();
            Stage = "playing-snapshot-publish";
            hub.Publish(hub.Current with
            {
                TrackGeneration = first, State = PlaybackStatus.Playing, Title = "SMTC fixture",
                Artist = "Fixture artist", Album = "Fixture album", DurationSeconds = 180,
                PositionSeconds = 21, CanPause = true, CanNext = true, CanSeek = true
            });
            Stage = "playing-update-error-check";
            Require(bridge.LastError is null, "Real SMTC setters must succeed before getters are verified.");
            Stage = "playing-capabilities-get";
            Require(controls.IsEnabled && controls.IsPauseEnabled && controls.IsNextEnabled,
                "Active session must follow snapshot capabilities.");
            Stage = "unsupported-capabilities-get";
            Require(!controls.IsPlayEnabled && !controls.IsPreviousEnabled,
                "Unsupported snapshot actions must stay disabled.");
            Stage = "playing-status-get";
            Require(controls.PlaybackStatus == MediaPlaybackStatus.Playing, "Playing state must reach SMTC.");
            Stage = "music-title-get";
            Require(controls.DisplayUpdater.MusicProperties.Title == "SMTC fixture", "Title must reach SMTC.");
            Stage = "music-artist-get";
            Require(controls.DisplayUpdater.MusicProperties.Artist == "Fixture artist", "Artist must reach SMTC.");
            Stage = "music-album-get";
            Require(controls.DisplayUpdater.MusicProperties.AlbumTitle == "Fixture album", "Album must reach SMTC.");
            Stage = "command-policy";
            Require(SystemMediaBridge.CanExecute(hub.Current, new(PlaybackCommandKind.Seek, first, 90)),
                "Current seek should be permitted.");
            Require(!SystemMediaBridge.CanExecute(hub.Current, new(PlaybackCommandKind.Seek, first, double.NaN)),
                "Non-finite seek must be rejected.");
            Require(!SystemMediaBridge.CanExecute(hub.Current, new(PlaybackCommandKind.Previous, first)),
                "Disabled previous must be rejected.");
            Require(!SystemMediaBridge.CanExecute(hub.Current, new(PlaybackCommandKind.Next, Guid.NewGuid())),
                "Old-generation commands must be rejected.");
            Require(SystemMediaBridge.MapStatus(PlaybackStatus.Loading) == MediaPlaybackStatus.Changing
                && SystemMediaBridge.MapStatus(PlaybackStatus.Buffering) == MediaPlaybackStatus.Changing,
                "Transitional states must be mapped without parsing UI text.");

            Stage = "artwork-encode";
            byte[] bytes = await ArtworkFixture.EncodeAsync(32, 32);
            Stage = "artwork-update";
            await bridge.UpdateArtworkAsync(first, bytes);
            await DrainAsync(dispatcher);
            Stage = "artwork-update-error-check";
            Require(bridge.LastError is null, "Real SMTC artwork setter must succeed before its getter is verified.");
            Stage = "artwork-thumbnail-get";
            Require(controls.DisplayUpdater.Thumbnail is not null, "Valid local artwork should reach SMTC.");
            Stage = "artwork-generation-replace";
            var lateArtwork = bridge.UpdateArtworkAsync(first, bytes);
            Guid second = Guid.NewGuid();
            hub.Publish(hub.Current with { TrackGeneration = second, Title = "Second fixture",
                State = PlaybackStatus.Paused, CanPause = false, CanPlay = true });
            await lateArtwork;
            await DrainAsync(dispatcher);
            Stage = "late-artwork-thumbnail-get";
            Require(controls.DisplayUpdater.Thumbnail is null, "Late artwork must not cross track generations.");
            Stage = "paused-status-capabilities-get";
            Require(controls.PlaybackStatus == MediaPlaybackStatus.Paused && controls.IsPlayEnabled
                && !controls.IsPauseEnabled, "Paused state and capabilities must reach SMTC.");
            Stage = "oversize-artwork-update";
            await bridge.UpdateArtworkAsync(second, new byte[SystemMediaBridge.MaximumArtworkBytes + 1]);
            await DrainAsync(dispatcher);
            Stage = "oversize-artwork-thumbnail-get";
            Require(controls.DisplayUpdater.Thumbnail is null, "Oversize artwork must be rejected.");

            Stage = "stop-snapshot-publish";
            hub.Publish(hub.Current with { TrackGeneration = null, State = PlaybackStatus.Stopped,
                Title = "", Artist = "", Album = "", CanPlay = false, CanSeek = false });
            Stage = "stop-status-capabilities-get";
            Require(!controls.IsEnabled && controls.PlaybackStatus == MediaPlaybackStatus.Stopped,
                "Stop must disable the session.");
            Stage = "stop-thumbnail-get";
            Require(controls.DisplayUpdater.Thumbnail is null, "Stop must clear previous artwork.");
            // ClearAll resets Type to Unknown. Windows then intentionally rejects
            // MusicProperties access, so validate the supported cleared-type API.
            Stage = "stop-display-type-get";
            Require(controls.DisplayUpdater.Type == MediaPlaybackType.Unknown,
                "Stop must clear the display metadata type.");
            Stage = "bridge-last-error";
            Require(bridge.LastError is null, "SMTC verification should not report platform errors.");
        }
        Stage = "closed-controls-retrieve";
        var closedControls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
        Stage = "closed-status-capabilities-get";
        Require(!closedControls.IsEnabled
            && closedControls.PlaybackStatus == MediaPlaybackStatus.Closed,
            "Dispose must close and disable the session before player disposal.");
        Require(bridge.LastError is null, "SMTC disposal should not report platform errors.");
        Stage = "complete";
    }

    private static Task DrainAsync(DispatcherQueue dispatcher)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => completion.TrySetResult()))
            completion.TrySetException(new InvalidOperationException("UI dispatcher unavailable."));
        return completion.Task;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
#endif
