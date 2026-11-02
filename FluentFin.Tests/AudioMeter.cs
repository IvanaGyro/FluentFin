using NAudio.CoreAudioApi;

namespace FluentFin.Tests;

internal static class AudioMeter
{
	/// <summary>The highest value of the peak meter of the default audio device (0 to 1) over the duration.</summary>
	public static async Task<float> Peak(TimeSpan duration)
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
