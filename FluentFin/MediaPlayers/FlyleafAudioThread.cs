using System.Runtime.InteropServices;
using FlyleafLib.MediaPlayer;
using Serilog;

namespace FluentFin.MediaPlayers;

/// <summary>
/// Diagnoses the crash of the arm64 app on the first audio frame (issue 23): an exception raised in
/// av_buffersink_get_frame_flags on Flyleaf's audio decoder thread, right after Flyleaf set up the audio device on that
/// same thread. Logs the floating point control word of the thread before and after the audio device is set up and
/// masks the floating point exceptions again when setting up the device unmasked them.
/// </summary>
public static class FlyleafAudioThread
{
	// _controlfp exception mask bits (a set bit masks the exception): inexact, underflow, overflow, zero divide, invalid
	private const uint ExceptionMask = 0x1F;

	[DllImport("ucrtbase.dll", EntryPoint = "_controlfp", CallingConvention = CallingConvention.Cdecl)]
	private static extern uint ControlFp(uint newValue, uint mask);

	/// <summary>The control word of the audio decoder thread before and after the last setup of the audio device.</summary>
	public static (uint Before, uint After)? LastDeviceSetup { get; private set; }

	/// <summary>The floating point control word of the calling thread.</summary>
	public static uint ControlWord() => ControlFp(0, 0);

	/// <summary>Whether some floating point exceptions raise hardware exceptions on the calling thread.</summary>
	public static bool ExceptionsUnmasked(uint controlWord) => (controlWord & ExceptionMask) != ExceptionMask;

	/// <summary>Applies FLUENTFIN_FFMPEG_CPU_FLAGS (an FFmpeg av_force_cpu_flags value, e.g. 0) and logs FFmpeg's CPU flags.</summary>
	public static void ConfigureCpuFlags()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("FLUENTFIN_FFMPEG_CPU_FLAGS"), out var flags))
		{
			Flyleaf.FFmpeg.Raw.av_force_cpu_flags(flags);
			Log.Warning("FFmpeg CPU flags forced to 0x{Flags:X}", flags);
		}

		Log.Information("FFmpeg CPU flags 0x{Flags:X}, floating point control word of the UI thread 0x{ControlWord:X}", Flyleaf.FFmpeg.Raw.av_get_cpu_flags(), ControlWord());
	}

	/// <summary>Wraps the callbacks Flyleaf runs on the audio decoder thread around the setup of the audio device.</summary>
	public static void Guard(Player player)
	{
		var decoder = player.decoder.AudioDecoder;

		var formatChanged = decoder.FormatChanged;
		decoder.FormatChanged = d =>
		{
			var before = ControlWord();
			formatChanged?.Invoke(d);
			var after = ControlWord();
			LastDeviceSetup = (before, after);
			Log.Information("Flyleaf audio decoder thread: floating point control word 0x{Before:X} before and 0x{After:X} after setting up the audio device", before, after);
		};

		var codecChanged = decoder.CodecChanged;
		decoder.CodecChanged = d =>
		{
			codecChanged?.Invoke(d);
			var controlWord = ControlWord();
			if (ExceptionsUnmasked(controlWord))
			{
				ControlFp(ExceptionMask, ExceptionMask);
				Log.Warning("Flyleaf audio decoder thread: floating point exceptions were unmasked (0x{ControlWord:X}), masked them again (0x{Now:X})", controlWord, ControlWord());
			}
		};
	}
}
