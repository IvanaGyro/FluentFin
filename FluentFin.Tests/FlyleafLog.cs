using FlyleafLib;

namespace FluentFin.Tests;

/// <summary>Writes Flyleaf's log to a file, for the tests to check what Flyleaf did (the app does not log Flyleaf).</summary>
public static class FlyleafLog
{
	private static readonly object Lock = new();
	private static StreamWriter? _writer;

	/// <summary>
	/// Writes Flyleaf's log to <paramref name="path"/> synchronously, line by line. Flyleaf writes its own log file from a
	/// background task, which loses the last lines when native code crashes.
	/// </summary>
	/// <remarks>
	/// Call it after <see cref="Engine.Start"/>: <see cref="Logger"/>'s static constructor subscribes to the WPF
	/// <c>Application.Current</c>, which Flyleaf only creates in <see cref="Engine.Start"/>.
	/// </remarks>
	public static void WriteTo(string path, LogLevel level)
	{
		lock (Lock)
		{
			_writer?.Dispose();
			_writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
		}

		Logger.CustomOutput = line =>
		{
			lock (Lock)
			{
				_writer?.WriteLine(line);
			}
		};
		Engine.Config.LogLevel = level;
		Engine.Config.LogOutput = ":custom";
	}
}
