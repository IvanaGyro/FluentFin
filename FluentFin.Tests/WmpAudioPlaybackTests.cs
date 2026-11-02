using NAudio.CoreAudioApi;
using Windows.Media.Core;
using Windows.Media.Playback;
using Xunit.Abstractions;

namespace FluentFin.Tests;

/// <summary>
/// Windows Media Player plays no sound in the app until an audio track is picked in the menu. These tests play
/// <c>Media/audio-tracks.mkv</c> (E-AC-3 5.1, AC-3 5.1, AAC 5.1 and E-AC-3 stereo audio, about -18 dB at the peak) with the
/// WinRT MediaPlayer the way WindowsMediaPlayerController does and read the peak meter of the default audio device, so
/// what reaches the device is measured, not what the player claims. The workflows install a virtual audio device.
/// </summary>
[Collection(AudioDevice.Name)]
public sealed class WmpAudioPlaybackTests(ITestOutputHelper output)
{
	private static readonly string Media = Path.Combine(AppContext.BaseDirectory, "Media", "audio-tracks.mkv");

	/// <summary>
	/// The same player is used for every item of the app, and the track is chosen while the player is still opening the media.
	/// Each scenario is one way of choosing the track, reported together: the runner is the only place to look at it.
	/// The tracks 0, 1 and 3 are E-AC-3 and AC-3, which Windows decodes only with the Dolby extension, track 2 is AAC.
	/// </summary>
	[Fact]
	public async Task Audio_ReachesTheAudioDevice()
	{
		var results = new List<string>();
		var silent = new List<string>();
		using var shared = new MediaPlayer { AutoPlay = false };

		async Task Scenario(string name, bool reuse, int expectedTrack, Action<MediaPlaybackItem>? onTracks, Action<MediaPlaybackItem>? onOpened, Func<MediaPlaybackItem, Task>? later)
		{
			var player = reuse ? shared : new MediaPlayer { AutoPlay = false };
			try
			{
				if (reuse)
				{
					// what the app does between two items
					player.Pause();
					player.Source = null;
				}

				var opened = new TaskCompletionSource();
				void OnOpened(MediaPlayer sender, object args) => opened.TrySetResult();
				void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs e)
				{
					opened.TrySetResult();
					results.Add($"{name}: MEDIA FAILED {e.Error} {e.ErrorMessage} 0x{e.ExtendedErrorCode?.HResult ?? 0:X8}");
				}

				player.MediaOpened += OnOpened;
				player.MediaFailed += OnFailed;
				var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(Media)));
				var tracksChanged = new TaskCompletionSource();
				item.AudioTracksChanged += (_, _) =>
				{
					onTracks?.Invoke(item);
					tracksChanged.TrySetResult();
				};
				var openedHandler = new Windows.Foundation.TypedEventHandler<MediaPlayer, object>((_, _) => onOpened?.Invoke(item));
				player.MediaOpened += openedHandler;

				player.Source = item;
				player.Play();

				await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(15)));
				await Task.WhenAny(tracksChanged.Task, Task.Delay(TimeSpan.FromSeconds(3)));
				if (later is not null)
				{
					await later(item);
				}

				var peak = await Peak(TimeSpan.FromSeconds(3));
				var line = $"{name}: peak={peak:0.000}, selected={item.AudioTracks.SelectedIndex} (expected {expectedTrack}), state={player.PlaybackSession.PlaybackState}, position={player.PlaybackSession.Position:g}, decoder of the selected={(item.AudioTracks.SelectedIndex >= 0 ? item.AudioTracks[item.AudioTracks.SelectedIndex].SupportInfo.DecoderStatus.ToString() : "-")}";
				results.Add(line);
				output.WriteLine(line);
				if (name.StartsWith("silent expected"))
				{
					return;
				}

				if (peak < 0.01)
				{
					silent.Add(name);
				}

				player.MediaOpened -= OnOpened;
				player.MediaOpened -= openedHandler;
				player.MediaFailed -= OnFailed;
			}
			finally
			{
				if (!reuse)
				{
					player.Dispose();
				}
			}
		}

		// the track 0 is E-AC-3: what the app plays without a choice, and what the player claims to play
		await Scenario("silent expected: default track 0 (E-AC-3)", false, 0, null, null, null);

		// the menu of ivana-main: the track picked while the media plays
		await Scenario("track 2 picked 1s after playing", false, 2, null, null, async item => { await Task.Delay(1000); item.AudioTracks.SelectedIndex = 2; });

		// the app: the choice is made when the tracks are announced
		await Scenario("track 2 picked when the tracks are announced", false, 2, item => item.AudioTracks.SelectedIndex = 2, null, null);
		await Scenario("track 2 picked when the tracks are announced, reused player", true, 2, item => item.AudioTracks.SelectedIndex = 2, null, null);

		// this branch: the choice is made again when the media is open
		await Scenario("track 2 picked when announced and when opened", false, 2, item => item.AudioTracks.SelectedIndex = 2, item => item.AudioTracks.SelectedIndex = 2, null);
		await Scenario("track 2 picked when announced and when opened, reused player", true, 2, item => item.AudioTracks.SelectedIndex = 2, item => item.AudioTracks.SelectedIndex = 2, null);
		await Scenario("track 2 picked when opened, reused player", true, 2, null, item => item.AudioTracks.SelectedIndex = 2, null);

		// the choice, and then the same one again a moment later
		await Scenario("track 2 picked when announced and again 500ms later", true, 2, item => item.AudioTracks.SelectedIndex = 2, null, async item => { await Task.Delay(500); item.AudioTracks.SelectedIndex = 2; });

		Assert.True(silent.Count == 0, "Silent: " + string.Join("; ", silent) + Environment.NewLine + string.Join(Environment.NewLine, results));
		output.WriteLine(string.Join(Environment.NewLine, results));
	}

	private static void Describe(string scenario, MediaPlaybackItem item, List<string> results)
	{
		results.Add($"{scenario}: {item.AudioTracks.Count} audio tracks, selected={item.AudioTracks.SelectedIndex}");
		for (var i = 0; i < item.AudioTracks.Count; i++)
		{
			var track = item.AudioTracks[i];
			try
			{
				var encoding = track.GetEncodingProperties();
				results.Add($"  #{i}: language={track.Language}, label={track.Label}, subtype={encoding.Subtype}, channels={encoding.ChannelCount}, rate={encoding.SampleRate}, decoder={track.SupportInfo.DecoderStatus}, source={track.SupportInfo.MediaSourceStatus}");
			}
			catch (Exception ex)
			{
				results.Add($"  #{i}: {ex.GetType().Name} {ex.Message}");
			}
		}
	}

	/// <summary>The highest value of the peak meter of the default audio device (0 to 1) over the duration.</summary>
	private static async Task<float> Peak(TimeSpan duration)
	{
		using var enumerator = new MMDeviceEnumerator();
		using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		var peak = 0f;
		var end = DateTime.UtcNow + duration;
		while (DateTime.UtcNow < end)
		{
			peak = Math.Max(peak, device.AudioMeterInformation.MasterPeakValue);
			await Task.Delay(50);
		}

		return peak;
	}
}
