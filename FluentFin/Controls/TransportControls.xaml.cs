using System.Reactive;
using System.Reactive.Linq;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ReactiveMarbles.ObservableEvents;
using ReactiveUI;


namespace FluentFin.Controls;

#nullable disable
public sealed partial class TransportControls : UserControl
{
	private bool _isTrickplayWanted;
	private readonly SymbolIcon _playSymbol = new(Symbol.Play);
	private readonly SymbolIcon _pauseSymbol = new(Symbol.Pause);

	[GeneratedDependencyProperty]
	public partial bool IsSkipButtonVisible { get; set; }

	[GeneratedDependencyProperty]
	public partial PlaylistViewModel Playlist { get; set; }


	[GeneratedDependencyProperty]
	public partial ICommand SkipCommand { get; set; }

	[GeneratedDependencyProperty]
	public partial TrickplayViewModel Trickplay { get; set; }

	[GeneratedDependencyProperty]
	public partial IJellyfinClient JellyfinClient { get; set; }

	public IMediaPlayerController Player
	{
		get
		{
			try
			{
				return (IMediaPlayerController)GetValue(PlayerProperty);
			}
			catch
			{
				return null;
			}
		}
		set { SetValue(PlayerProperty, value); }
	}

	public static readonly DependencyProperty PlayerProperty =
		DependencyProperty.Register("Player", typeof(IMediaPlayerController), typeof(TransportControls), new PropertyMetadata(null, OnPlayerChanged));

	private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var tc = (TransportControls)d;
		if (e.NewValue is not IMediaPlayerController controller)
		{
			return;
		}

		TimeSpan duration = TimeSpan.Zero;
		controller.DurationChanged.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(e =>
		{
			tc.TimeSlider.Maximum = e.TotalMilliseconds;
			duration = e;

			// a value given before the length was known is clamped to the old length, the position is given again
			try
			{
				tc.TimeSlider.Value = Math.Min(tc.TimeSlider.Maximum, controller.Position.TotalMilliseconds);
			}
			catch { }
		});
		controller.PositionChanged.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(e =>
		{
			try
			{
				tc.TimeSlider.Value = e.TotalMilliseconds;
				tc.TxtCurrentTime.Text = Converters.Converters.TimeSpanToString(e);
				tc.TxtRemainingTime.Text = TimeRemaining(e, duration);
			}
			catch { }
		});
		controller.Playing.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => tc.PlayPauseButton.Content = tc._pauseSymbol);
		controller.Paused.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => tc.PlayPauseButton.Content = tc._playSymbol);
		controller.VolumeChanged
			.Where(e => e >= 0)
			.Throttle(TimeSpan.FromMilliseconds(200))
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(e => tc.VolumeSlider.Value = Math.Floor(e));
		controller.SubtitleText.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(text => tc.Subtitles.Text = text);
	}

	public IObservable<Unit> OnDynamicSkip { get; }

	public TransportControls()
	{
		InitializeComponent();

		OnDynamicSkip = DynamicSkipIntroButton.Events().Click.Select(_ => Unit.Default);

		// TeachingTip flips IsOpen back if it changes while its open animation is playing, so retry the close until it sticks.
		TrickplayTip.RegisterPropertyChangedCallback(TeachingTip.IsOpenProperty, async (_, _) =>
		{
			if (TrickplayTip.IsOpen && !_isTrickplayWanted)
			{
				await Task.Delay(100);
				if (!_isTrickplayWanted)
				{
					TrickplayTip.IsOpen = false;
				}
			}
		});

		TimeSlider
			.Events()
			.ValueChanged
			.Where(x => Math.Abs(x.NewValue - Player.Position.TotalMilliseconds) > 5000)
			.Subscribe(x =>
			{
				try
				{
					var currentState = Player.State;
					if (currentState is MediaPlayerState.Playing)
					{
						Player.Pause();
					}

					Player.SeekTo(TimeSpan.FromMilliseconds(x.NewValue));

					if (currentState is MediaPlayerState.Playing)
					{
						Player.Play();
					}
				}
				catch { }
			});

		VolumeSlider.Events()
			.ValueChanged
			.Where(_ => Player is not null)
			.Subscribe(x => Player.Volume = (int)x.NewValue);
	}

	private static string TimeRemaining(TimeSpan currentTime, TimeSpan duration)
	{
		return (duration - currentTime).ToString("hh\\:mm\\:ss");
	}

	private async void PlaybackInfoButton_Click(object sender, RoutedEventArgs e)
	{
		if (PlaybackInfoBorder.Visibility == Visibility.Visible)
		{
			PlaybackInfoBorder.Visibility = Visibility.Collapsed;
			return;
		}

		PlaybackInfoBorder.Visibility = Visibility.Visible;
		var item = Playlist?.SelectedItem;
		ShowPlaybackInfo(item, null);

		// The transcoded stream is only known to the server, ask for the details of this session.
		if (item?.Media?.PlayMethod == Jellyfin.Sdk.Generated.Models.PlaybackProgressInfo_PlayMethod.Transcode && JellyfinClient is not null)
		{
			var sessions = await JellyfinClient.GetControllableSessions();
			var transcoding = sessions.FirstOrDefault(x => x.Id == SessionInfo.SessionId)?.TranscodingInfo;
			if (transcoding is not null && PlaybackInfoBorder.Visibility == Visibility.Visible && ReferenceEquals(item, Playlist?.SelectedItem))
			{
				ShowPlaybackInfo(item, transcoding);
			}
		}
	}

	private void ShowPlaybackInfo(PlaylistItem item, Jellyfin.Sdk.Generated.Models.TranscodingInfo transcoding)
	{
		PlaybackInfoPanel.Children.Clear();

		if (item?.Media is not { } media)
		{
			PlaybackInfoPanel.Children.Add(new TextBlock { Text = "No media is playing." });
			return;
		}

		var audio = item.Selection?.AudioStreamIndex ?? media.MediaSourceInfo.DefaultAudioStreamIndex;
		var subtitle = item.Selection?.SubtitleStreamIndex ?? media.MediaSourceInfo.DefaultSubtitleStreamIndex;

		foreach (var section in PlaybackInfo.Build(media, audio, subtitle, Player?.GetType().Name.Replace("MediaPlayerController", ""), transcoding))
		{
			var panel = new StackPanel { Spacing = 2 };
			panel.Children.Add(new TextBlock { Text = section.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });

			var grid = new Grid { ColumnSpacing = 16, RowSpacing = 2 };
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			for (var i = 0; i < section.Rows.Count; i++)
			{
				grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				var label = new TextBlock { Text = section.Rows[i].Label, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Top };
				var value = new TextBlock { Text = section.Rows[i].Value, TextWrapping = TextWrapping.WrapWholeWords, IsTextSelectionEnabled = true };
				Grid.SetRow(label, i);
				Grid.SetRow(value, i);
				Grid.SetColumn(value, 1);
				grid.Children.Add(label);
				grid.Children.Add(value);
			}

			panel.Children.Add(grid);
			PlaybackInfoPanel.Children.Add(panel);
		}
	}

	private async void SkipBackwardButton_Click(object sender, RoutedEventArgs e) => await SkipBackward();

	private async void SkipForwardButton_Click(object sender, RoutedEventArgs e) => await SkipForward();

	private async void PlayPauseButton_Click(object sender, RoutedEventArgs e) => await TogglePlayPause();

	private async void CastButton_Click(object sender, RoutedEventArgs e)
	{
		if (JellyfinClient is null)
		{
			return;
		}

		var sessions = await JellyfinClient.GetControllableSessions();

		if (sessions.FirstOrDefault(x => x.Id == SessionInfo.SessionId) is { } session && session.NowPlayingItem is { } dto)
		{
			Player.Stop();
			App.Dialogs.PlayOnSessionCommand.Execute(dto);
		}
	}

	private void TimeSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		if (Trickplay is null)
		{
			return;
		}

		if (Trickplay.Item?.Trickplay?.AdditionalData?.Count is not > 0)
		{
			return;
		}

		// Handled synchronously: deferring to the dispatcher let a queued move reopen the tip after PointerExited closed it.
		const int teachingTipMargin = 12;
		_isTrickplayWanted = true;
		TrickplayTip.IsOpen = true;

		var navView = this.FindAscendantOrSelf<NavigationView>();
		var offset = navView?.IsPaneOpen == true ? navView.OpenPaneLength : 0;
		var trickplayWidth = TrickplayScrollViewer.Width + 2 * teachingTipMargin;
		var halfTrickplayWidth = trickplayWidth / 2;

		var point = e.GetCurrentPoint(TimeSlider);
		var globalPoint = e.GetCurrentPoint(this);
		Trickplay.Position = TimeSpan.FromMilliseconds((point.Position.X / TimeSlider.ActualWidth) * TimeSlider.Maximum);

		var minMargin = Math.Max(teachingTipMargin + offset, globalPoint.Position.X + offset - halfTrickplayWidth);
		var margin = Math.Min(minMargin, ActualWidth + offset - trickplayWidth - 10);
		TrickplayTip.PlacementMargin = new Thickness(margin, 0, 0, Bar.ActualHeight);
	}

	private void TimeSlider_PointerExited(object sender, PointerRoutedEventArgs e) => CloseTrickplay();

	private void Grid_PointerMoved(object sender, PointerRoutedEventArgs e) => CloseTrickplay();

	private void CloseTrickplay()
	{
		_isTrickplayWanted = false;
		TrickplayTip.IsOpen = false;
	}

	private async Task SkipBackward()
	{
		await Player.SeekBackward(JellyfinClient, TimeSpan.FromSeconds(10));
	}

	private async Task SkipForward()
	{
		await Player.SeekForward(JellyfinClient, TimeSpan.FromSeconds(30));
	}

	private async Task TogglePlayPause()
	{
		await Player.TogglePlayPlause(JellyfinClient);
	}
}

#nullable restore