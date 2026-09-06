using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using PiPlay;
using PiPlay.Models;
using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Wpf)]
public class MainWindowLifecycleTests : IDisposable
{
    [Fact]
    public void Source_restore_raises_a_stale_short_placement_to_the_declared_minimum()
    {
        var saved = new PlacementData
        {
            X = 240,
            Y = 135,
            Width = 760,
            Height = 321,
            Maximized = true,
            MonitorDeviceName = @"\\.\DISPLAY2",
            MonitorWorkArea = new RectData { X = 1920, Y = 0, Width = 2560, Height = 1440 },
            DpiScale = 1.0,
        };

        var normalized = MainWindow.NormalizeSourcePlacementForRestoreForTests(saved);

        Assert.NotNull(normalized);
        Assert.Equal(240, normalized!.X);
        Assert.Equal(135, normalized.Y);
        Assert.Equal(760, normalized.Width);
        Assert.Equal(480, normalized.Height);
        Assert.True(normalized.Maximized);
        Assert.Equal(@"\\.\DISPLAY2", normalized.MonitorDeviceName);
        Assert.Equal(1.0, normalized.DpiScale);
        Assert.NotNull(normalized.MonitorWorkArea);
        Assert.Equal(1920, normalized.MonitorWorkArea!.X);
        Assert.Equal(2560, normalized.MonitorWorkArea.Width);
        Assert.Equal(1440, normalized.MonitorWorkArea.Height);
    }

    [Fact]
    public void Return_in_progress_blocks_a_new_popout_and_keeps_the_primary_action_truthful()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            var button = (Button)window.FindName("PopOutButton")!;
            var label = (TextBlock)window.FindName("PopOutButtonText")!;
            var settings = (Button)window.FindName("SettingsButton")!;

            window.SetBrowserReadyForTests(true);
            Assert.True(window.CanStartVideoPopoutForTests);
            Assert.True(button.IsEnabled);
            Assert.True(settings.IsEnabled);

            window.BeginReturnForTests();

            Assert.True(window.ReturnInProgressForTests);
            Assert.False(window.CanStartVideoPopoutForTests);
            Assert.False(button.IsEnabled);
            Assert.False(settings.IsEnabled);
            Assert.Contains("Returning", label.Text, StringComparison.OrdinalIgnoreCase);

            window.CompleteReturnForTests();

            Assert.False(window.ReturnInProgressForTests);
            Assert.True(window.CanStartVideoPopoutForTests);
            Assert.True(button.IsEnabled);
            Assert.True(settings.IsEnabled);
            Assert.DoesNotContain("Returning", label.Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Returning_from_popout_restores_a_minimized_Source_without_changing_Pin(bool pinned)
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow(new AppSettings
            {
                MainWindow = new WindowSettings { Topmost = pinned },
            })
            {
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            _ = new WindowInteropHelper(window).EnsureHandle();
            window.WindowState = WindowState.Minimized;
            Assert.Equal(WindowState.Minimized, window.WindowState);

            window.RestoreSourceAfterReturnForTests();

            Assert.True(window.IsVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(pinned, window.Topmost);
        });
    }

    [Fact]
    public void Source_pin_is_suspended_for_the_popout_without_losing_the_saved_preference()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow(new AppSettings
            {
                MainWindow = new WindowSettings { Topmost = true },
            });
            var pin = (ToggleButton)window.FindName("PinToggle")!;

            Assert.True(window.Topmost);
            Assert.True(pin.IsChecked);
            Assert.True(pin.IsEnabled);
            Assert.Contains("Unpin", (string)pin.ToolTip);

            window.SuspendSourcePinForPopoutForTests();

            Assert.False(window.Topmost);
            Assert.False(pin.IsChecked);
            Assert.False(pin.IsEnabled);
            Assert.True(window.SavedSourceTopmostForTests);
            Assert.Contains("Pin Source", (string)pin.ToolTip);

            window.CaptureSourceTopmostPreferenceForCloseForTests();
            Assert.True(window.SavedSourceTopmostForTests);

            window.RestoreSourcePinAfterPopoutForTests();

            Assert.True(window.Topmost);
            Assert.True(pin.IsChecked);
            Assert.True(pin.IsEnabled);
            Assert.Contains("Unpin", (string)pin.ToolTip);
        });
    }

    [Fact]
    public void Profile_derived_Source_pin_survives_popout_suspend_restore_and_mid_popout_close_capture()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow(new AppSettings
            {
                MainWindow = new WindowSettings { Topmost = false },
                Profiles =
                {
                    new Profile
                    {
                        Name = "Pinned profile",
                        Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                        Topmost = true,
                    },
                },
            });
            var profiles = (ComboBox)window.FindName("ProfilesCombo")!;

            profiles.SelectedIndex = 0;
            Assert.True(window.Topmost);
            Assert.False(window.SavedSourceTopmostForTests);

            window.SuspendSourcePinForPopoutForTests();
            Assert.False(window.Topmost);

            window.CaptureSourceTopmostPreferenceForCloseForTests();
            Assert.True(window.SavedSourceTopmostForTests);

            window.RestoreSourcePinAfterPopoutForTests();
            Assert.True(window.Topmost);
        });
    }

    [Fact]
    public void Source_navigation_commands_are_disabled_while_the_placeholder_hides_the_browser()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow(new AppSettings
            {
                ActiveProfileName = "Saved",
                Profiles =
                {
                    new Profile
                    {
                        Name = "Saved",
                        Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                    },
                },
            });

            var back = (Button)window.FindName("BackButton")!;
            var reload = (Button)window.FindName("ReloadButton")!;
            var home = (Button)window.FindName("HomeButton")!;
            var url = (TextBox)window.FindName("UrlBox")!;
            var profiles = (ComboBox)window.FindName("ProfilesCombo")!;
            var profileActions = (Button)window.FindName("ProfileActionsButton")!;
            var saveProfile = (MenuItem)window.FindName("SaveProfileMenuItem")!;
            var editProfile = (MenuItem)window.FindName("EditProfileMenuItem")!;
            var deleteProfile = (MenuItem)window.FindName("DeleteProfileMenuItem")!;

            Assert.True(back.IsEnabled);
            Assert.True(reload.IsEnabled);
            Assert.True(home.IsEnabled);
            Assert.True(url.IsEnabled);
            Assert.True(profiles.IsEnabled);
            Assert.True(profileActions.IsEnabled);
            Assert.True(saveProfile.IsEnabled);
            Assert.True(editProfile.IsEnabled);
            Assert.True(deleteProfile.IsEnabled);

            window.ShowSourcePlaceholder(true);

            Assert.False(back.IsEnabled);
            Assert.False(reload.IsEnabled);
            Assert.False(home.IsEnabled);
            Assert.False(url.IsEnabled);
            Assert.False(profiles.IsEnabled);
            Assert.False(profileActions.IsEnabled);
            Assert.False(saveProfile.IsEnabled);
            Assert.False(editProfile.IsEnabled);
            Assert.False(deleteProfile.IsEnabled);

            window.ShowSourcePlaceholder(false);

            Assert.True(back.IsEnabled);
            Assert.True(reload.IsEnabled);
            Assert.True(home.IsEnabled);
            Assert.True(url.IsEnabled);
            Assert.True(profiles.IsEnabled);
            Assert.True(profileActions.IsEnabled);
            Assert.True(saveProfile.IsEnabled);
            Assert.True(editProfile.IsEnabled);
            Assert.True(deleteProfile.IsEnabled);
        });
    }

    // --- Incoming links (REQ-APP-01, ADR-0009, review 2026-09-05 PP-01) ---

    private const string LinkA = "https://www.youtube.com/watch?v=AAAAAAAAAAA";
    private const string LinkB = "https://www.youtube.com/watch?v=BBBBBBBBBBB";
    private const string LinkC = "https://youtu.be/CCCCCCCCCCC?t=42";
    private const string PlaylistLink = "https://www.youtube.com/playlist?list=PL0123456789";

    private static PlayerWindow NewHeadlessPlayer(string url = LinkA)
    {
        var player = new PlayerWindow(environment: null!, url: url, topmost: false, placement: null,
            defaultWidth: 960, defaultHeight: 540, fadeEnabled: true);
        player.TrackReturnIdentity(url);   // what the live page would have reported
        return player;
    }

    [Fact]
    public void Incoming_link_before_browser_readiness_is_queued_for_the_source()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();

            window.NavigateTo(LinkA);

            Assert.Equal(IncomingLinkAction.QueueUntilReady, window.AcceptIncomingLink(LinkA).Action);
            Assert.Equal("https://www.youtube.com/watch?v=AAAAAAAAAAA", window.PendingUrlForTests);
            Assert.Null(window.RetainedIncomingTargetForTests);
        });
    }

    [Fact]
    public void Incoming_link_with_a_ready_source_and_no_popout_navigates_the_source()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);

            var decision = window.AcceptIncomingLink(LinkC);

            Assert.Equal(IncomingLinkAction.NavigateSource, decision.Action);
            // Headless: the Source has no core yet, so the navigation lands in the pending slot.
            Assert.Equal("https://www.youtube.com/watch?v=CCCCCCCCCCC&t=42s", window.PendingUrlForTests);
            Assert.Null(window.RetainedIncomingTargetForTests);
        });
    }

    [Fact]
    public void Incoming_video_link_retargets_the_active_popout_and_leaves_the_hidden_source_alone()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            var player = NewHeadlessPlayer();
            window.AttachPlayerForTests(player);
            window.SeedPopoutReturnForTests("AAAAAAAAAAA", sourceWasPlayingAtPopout: true);
            window.ShowSourcePlaceholder(true);

            var decision = window.ActivateFromSecondInstance(LinkB);

            Assert.Equal(IncomingLinkAction.RetargetPopout, decision.Action);
            Assert.True(decision.Accepted);
            Assert.Equal("BBBBBBBBBBB", player.ReturnVideoIdForTests);
            Assert.StartsWith("https://www.youtube.com/watch?v=BBBBBBBBBBB", player.CurrentUrlForTests);
            Assert.Null(window.PendingUrlForTests);                      // Source stayed on A
            Assert.Null(window.RetainedIncomingTargetForTests);
            Assert.False(window.IsVisible);                              // the Popout is the visible owner
            Assert.Single(Application.Current.Windows.OfType<PlayerWindow>());
        });
    }

    [Fact]
    public void Return_after_an_incoming_link_navigates_the_source_to_B_and_arms_auto_dedup()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            var player = NewHeadlessPlayer();
            window.AttachPlayerForTests(player);
            window.SeedPopoutReturnForTests("AAAAAAAAAAA", sourceWasPlayingAtPopout: true);
            Assert.Equal(IncomingLinkAction.RetargetPopout, window.AcceptIncomingLink(LinkB).Action);

            window.AttachPlayerForTests(null);   // the player closed and reported B
            window.ApplyReturnActionAsync(new PlayerReturnState
                {
                    VideoId = "BBBBBBBBBBB",
                    LastKnownSeconds = 42,
                    Paused = false,
                })
                .GetAwaiter().GetResult();   // Navigate completes synchronously (no core to script)

            Assert.StartsWith("https://www.youtube.com/watch?v=BBBBBBBBBBB", window.PendingUrlForTests);
            Assert.Contains("t=42s", window.PendingUrlForTests);
            Assert.Equal("BBBBBBBBBBB", window.AutoLastHandledVideoIdForTests);
            Assert.NotNull(window.PendingReturnReplayForTests);
        });
    }

    [Fact]
    public void Links_arriving_during_return_are_retained_latest_wins_and_applied_when_it_completes()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            var urlBox = (TextBox)window.FindName("UrlBox")!;
            window.SetBrowserReadyForTests(true);
            window.BeginReturnForTests();   // also the state during an awaited return replay

            Assert.Equal(IncomingLinkAction.Retain, window.AcceptIncomingLink(LinkA).Action);
            Assert.Equal(IncomingLinkAction.Retain, window.AcceptIncomingLink(LinkB).Action);

            Assert.Equal("BBBBBBBBBBB", window.RetainedIncomingTargetForTests!.VideoId);
            Assert.Null(window.PendingUrlForTests);
            Assert.Contains("BBBBBBBBBBB", urlBox.Text);   // pending status

            window.CompleteReturnForTests();

            Assert.Null(window.RetainedIncomingTargetForTests);
            Assert.Equal("https://www.youtube.com/watch?v=BBBBBBBBBBB", window.PendingUrlForTests);
        });
    }

    [Fact]
    public void Links_arriving_during_clear_are_retained_and_applied_when_the_clear_finishes()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            window.SetClearingBrowserDataForTests(true);

            Assert.Equal(IncomingLinkAction.Retain, window.AcceptIncomingLink(LinkC).Action);
            Assert.Null(window.PendingUrlForTests);

            // Still clearing: applying re-retains instead of navigating underneath the clear.
            window.ApplyRetainedIncomingLinkForTests();
            Assert.Equal("CCCCCCCCCCC", window.RetainedIncomingTargetForTests!.VideoId);

            window.SetClearingBrowserDataForTests(false);
            window.ApplyRetainedIncomingLinkForTests();

            Assert.Null(window.RetainedIncomingTargetForTests);
            Assert.Equal("https://www.youtube.com/watch?v=CCCCCCCCCCC&t=42s", window.PendingUrlForTests);
        });
    }

    [Fact]
    public void Playlist_link_during_an_active_popout_waits_with_a_visible_note_until_return()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            var note = (TextBlock)window.FindName("PlaceholderNoteText")!;
            window.SetBrowserReadyForTests(true);
            var player = NewHeadlessPlayer();
            window.AttachPlayerForTests(player);
            window.ShowSourcePlaceholder(true);

            var decision = window.ActivateFromSecondInstance(PlaylistLink);

            Assert.Equal(IncomingLinkAction.Retain, decision.Action);
            Assert.Equal("PL0123456789", window.RetainedIncomingTargetForTests!.PlaylistId);
            Assert.Equal("AAAAAAAAAAA", player.ReturnVideoIdForTests);   // Popout untouched
            Assert.Null(window.PendingUrlForTests);                      // hidden Source untouched
            Assert.Equal(Visibility.Visible, note.Visibility);
            Assert.Equal(MainWindow.PendingIncomingLinkNote, note.Text);

            // Bring video back: the Source owns playback again and takes the playlist.
            window.AttachPlayerForTests(null);
            window.BeginReturnForTests();
            window.ShowSourcePlaceholder(false);
            window.CompleteReturnForTests();

            Assert.Null(window.RetainedIncomingTargetForTests);
            Assert.Equal("https://www.youtube.com/playlist?list=PL0123456789", window.PendingUrlForTests);
        });
    }

    [Fact]
    public void A_link_retained_while_bringing_video_back_is_applied_when_the_return_finishes_synchronously()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            var player = NewHeadlessPlayer();
            window.AttachPlayerWithReturnForTests(player);

            // The link lands after Bring video back started (the policy sees PopoutInProgress).
            window.SetPopoutInProgressForTests(true);
            Assert.Equal(IncomingLinkAction.Retain, window.AcceptIncomingLink(LinkB).Action);
            Assert.Equal("BBBBBBBBBBB", window.RetainedIncomingTargetForTests!.VideoId);
            window.SetPopoutInProgressForTests(false);

            // Headless: the capture and the return complete synchronously, so the return
            // transition runs INSIDE BringVideoBackAsync while _popoutInProgress is still set.
            window.BringVideoBackForTestsAsync().GetAwaiter().GetResult();

            Assert.Null(window.RetainedIncomingTargetForTests);
            Assert.Equal(LinkB, window.PendingUrlForTests);
            Assert.False(window.IncomingLinkStateForTests.PopoutInProgress);
            Assert.False(window.IncomingLinkStateForTests.ReturnInProgress);
        });
    }

    [Fact]
    public void A_closing_source_answers_unavailable_and_does_not_come_forward()
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow();
            window.SetBrowserReadyForTests(true);
            window.SetMainWindowClosingForTests(true);

            var decision = window.ActivateFromSecondInstance(LinkA);

            Assert.Equal(IncomingLinkAction.Unavailable, decision.Action);
            Assert.False(decision.Accepted);
            // App.OnExit has not run yet, so the process-wide flag is still false; the window's
            // own state must be enough for the sender to elect a replacement.
            Assert.Equal(HandoffAck.Unavailable, SingleInstanceHandoffPolicy.AckFor(decision, shuttingDown: false));
            Assert.False(window.IsVisible);
            Assert.Null(window.PendingUrlForTests);
            Assert.Null(window.RetainedIncomingTargetForTests);
        });
    }

    [Theory]
    [InlineData(null, IncomingLinkAction.ActivateOnly, true)]
    [InlineData("", IncomingLinkAction.ActivateOnly, true)]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ", IncomingLinkAction.Reject, false)]
    [InlineData("%E2%82%", IncomingLinkAction.Reject, false)]
    public void Activation_without_a_usable_link_still_brings_the_source_forward(
        string? payload, IncomingLinkAction expected, bool accepted)
    {
        StaTestThread.Invoke(() =>
        {
            var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
            window.SetBrowserReadyForTests(true);

            var decision = window.ActivateFromSecondInstance(payload);

            Assert.Equal(expected, decision.Action);
            Assert.Equal(accepted, decision.Accepted);
            Assert.True(window.IsVisible);
            Assert.Null(window.PendingUrlForTests);
            Assert.Null(window.RetainedIncomingTargetForTests);
        });
    }

    [Fact]
    public void A_refused_settings_save_shows_the_title_bar_hint_once_without_a_modal()
    {
        StaTestThread.Invoke(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "PiPlayTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"schemaVersion\":3}");
            try
            {
                var unreadable = new SettingsService(path, _ => throw new IOException("locked"));
                unreadable.Load();
                var window = new MainWindow();
                window.ReplaceSettingsServiceForTests(unreadable);
                Assert.False(window.IsSettingsUnsavedHintVisibleForTests);

                window.SaveSettingsForTests();
                Assert.True(window.IsSettingsUnsavedHintVisibleForTests);
                window.SaveSettingsForTests();   // no second signal; the hint stays
                Assert.True(window.IsSettingsUnsavedHintVisibleForTests);
                Assert.Equal("{\"schemaVersion\":3}", File.ReadAllText(path));
            }
            finally
            {
                new SettingsService(path).Load();   // lift the process-wide unread flag for later tests
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        });
    }

    public void Dispose() => StaTestThread.Invoke(() =>
    {
        foreach (var window in Application.Current.Windows.Cast<Window>().ToArray())
        {
            try { window.Close(); }
            catch { /* Never-shown test windows can already be tearing down. */ }
        }
    });
}
