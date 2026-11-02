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

	/// <summary>Everything the player does in the app, one scenario after the other, reported together: the runner is the only place to look at it.</summary>
	[Fact]
	public async Task Audio_ReachesTheAudioDevice()
	{
		var results = new List<string>();
		var silent = new List<string>();

		async Task Scenario(string name, Func<MediaPlayer, MediaPlaybackItem, Task> act)
		{
			using var player = new MediaPlayer { AutoPlay = false };
			var opened = new TaskCompletionSource();
			player.MediaOpened += (_, _) => opened.TrySetResult();
			player.MediaFailed += (_, e) => { opened.TrySetResult(); results.Add($"{name}: MEDIA FAILED {e.Error} {e.ErrorMessage} 0x{e.ExtendedErrorCode?.HResult ?? 0:X8}"); };

			var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(Media)));
			var tracksChanged = new TaskCompletionSource();
			item.AudioTracksChanged += (_, _) => tracksChanged.TrySetResult();
			player.Source = item;
			player.Play();

			await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(15)));
			await Task.WhenAny(tracksChanged.Task, Task.Delay(TimeSpan.FromSeconds(3)));
			Describe(name, item, results);

			await act(player, item);

			var peak = await Peak(TimeSpan.FromSeconds(3));
			var line = $"{name}: peak={peak:0.000}, selected={item.AudioTracks.SelectedIndex}, state={player.PlaybackSession.PlaybackState}, position={player.PlaybackSession.Position}, volume={player.Volume}, muted={player.IsMuted}";
			results.Add(line);
			output.WriteLine(line);
			if (peak < 0.01)
			{
				silent.Add(name);
			}
		}

		await Scenario("default track, nothing selected", (_, _) => Task.CompletedTask);

		await Scenario("track 2 picked 1s after playing", async (_, item) =>
		{
			await Task.Delay(1000);
			item.AudioTracks.SelectedIndex = 2;
		});

		await Scenario("track 0 picked again right away (the same index)", (_, item) =>
		{
			item.AudioTracks.SelectedIndex = 0;
			return Task.CompletedTask;
		});

		await Scenario("track 2 picked right away, then track 0", async (_, item) =>
		{
			item.AudioTracks.SelectedIndex = 2;
			await Task.Delay(300);
			item.AudioTracks.SelectedIndex = 0;
		});

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
