using System.Net;
using System.Net.Sockets;

namespace FluentFin.Tests;

/// <summary>
/// The files of a folder over http on localhost, with byte ranges (a player streams a file with them) and a delay for every request: the
/// server needs time to answer, and to start a transcode, and the player has to wait for it.
/// </summary>
internal sealed class FakeMediaServer : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly string _folder;
	private readonly int _port;

	public FakeMediaServer(string folder, TimeSpan delay)
	{
		_folder = folder;
		Delay = delay;
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		_port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		_listener.Prefixes.Add($"http://localhost:{_port}/");
		_listener.Start();
		_ = Task.Run(Serve);
	}

	/// <summary>Added to every request.</summary>
	public TimeSpan Delay { get; set; }

	/// <summary>Added to the requests of the segments of a stream (.ts) on top of <see cref="Delay"/>: a transcode of a big video takes seconds to produce them.</summary>
	public TimeSpan SegmentDelay { get; set; }

	/// <summary>Added to the request of one file, by its path relative to the folder: a segment the server is slow to produce.</summary>
	public Func<string, TimeSpan>? FileDelay { get; set; }

	public Uri Url(string relativePath) => new($"http://localhost:{_port}/{relativePath}");

	private async Task Serve()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch
			{
				return;
			}

			_ = Task.Run(() => Answer(context));
		}
	}

	private async Task Answer(HttpListenerContext context)
	{
		try
		{
			await Task.Delay(Delay);
			if (context.Request.Url!.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
			{
				await Task.Delay(SegmentDelay);
			}

			if (FileDelay?.Invoke(Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/'))) is { } fileDelay && fileDelay > TimeSpan.Zero)
			{
				await Task.Delay(fileDelay);
			}

			var relative = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
			var path = Path.GetFullPath(Path.Combine(_folder, relative));
			if (!path.StartsWith(Path.GetFullPath(_folder)) || !File.Exists(path))
			{
				context.Response.StatusCode = 404;
				return;
			}

			var bytes = await File.ReadAllBytesAsync(path);
			context.Response.ContentType = Path.GetExtension(path) switch
			{
				".m3u8" => "application/vnd.apple.mpegurl",
				".ts" => "video/mp2t",
				".mkv" => "video/x-matroska",
				_ => "application/octet-stream",
			};
			context.Response.AddHeader("Accept-Ranges", "bytes");

			var from = 0L;
			var to = bytes.Length - 1L;
			if (context.Request.Headers["Range"] is { } range && range.StartsWith("bytes="))
			{
				var parts = range["bytes=".Length..].Split('-');
				if (long.TryParse(parts[0], out var start))
				{
					from = start;
				}

				if (parts.Length > 1 && long.TryParse(parts[1], out var end))
				{
					to = Math.Min(end, bytes.Length - 1L);
				}

				context.Response.StatusCode = 206;
				context.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{bytes.Length}");
			}

			var length = (int)(to - from + 1);
			context.Response.ContentLength64 = length;
			await context.Response.OutputStream.WriteAsync(bytes.AsMemory((int)from, length));
		}
		catch
		{
		}
		finally
		{
			try
			{
				context.Response.Close();
			}
			catch
			{
			}
		}
	}

	public void Dispose() => _listener.Close();
}
