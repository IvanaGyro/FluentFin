#:package LibVLCSharp@3.10.2
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

Core.Initialize(args[0]);
using var libVLC = new LibVLC("--quiet");
Console.WriteLine($"{RuntimeInformation.ProcessArchitecture} libvlc {libVLC.Version}");
Console.WriteLine("audio outputs: " + string.Join(",", libVLC.AudioOutputs.Select(a => a.Name)));
using var player = new MediaPlayer(libVLC);
Console.WriteLine("MediaPlayer created OK");
