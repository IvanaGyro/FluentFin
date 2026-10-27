#:property PublishAot=false
using System.Runtime.InteropServices;

// usage: dotnet run dump.cs <dir with ffmpeg dlls> <out file>
var dir = args[0];
var names = Directory.GetFiles(dir, "*.dll").Select(Path.GetFileName).ToList();
string Find(string p) => Path.Combine(dir, names.First(n => n!.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase))!);
var h = new Dictionary<string, nint>();
// same set and order Flyleaf's "Main" load profile uses (no avdevice / avfilter)
foreach (var p in new[] { "avutil", "swresample", "swscale", "avcodec", "avformat" })
    h[p] = NativeLibrary.Load(Find(p));

T Fn<T>(string lib, string name) where T : Delegate =>
    Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(h[lib], name));

var lines = new List<string>();
foreach (var l in new[] { "avutil", "swresample", "swscale", "avcodec", "avformat" })
{
    var v = Fn<Ver>(l, l + "_version")();
    lines.Add($"version {l} {v >> 16}.{(v >> 8) & 0xff}.{v & 0xff}");
}

var iterCodec = Fn<Iter>("avcodec", "av_codec_iterate");
var isDec = Fn<IsDec>("avcodec", "av_codec_is_decoder");
nint opaque = 0, c;
while ((c = iterCodec(ref opaque)) != 0)
    if (isDec(c) != 0) lines.Add("decoder " + Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(c)));

var iterDemux = Fn<Iter>("avformat", "av_demuxer_iterate");
opaque = 0;
while ((c = iterDemux(ref opaque)) != 0) lines.Add("demuxer " + Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(c)));

var iterParser = Fn<Iter>("avcodec", "av_parser_iterate");
opaque = 0;
while ((c = iterParser(ref opaque)) != 0) { } // parsers expose codec ids only; skipped

var enumProto = Fn<Iter2>("avformat", "avio_enum_protocols");
opaque = 0;
while ((c = enumProto(ref opaque, 0)) != 0) lines.Add("protocol " + Marshal.PtrToStringAnsi(c));

var iterHw = Fn<HwIter>("avutil", "av_hwdevice_iterate_types");
var hwName = Fn<HwName>("avutil", "av_hwdevice_get_type_name");
int t = 0;
while ((t = iterHw(t)) != 0) lines.Add("hwdevice " + Marshal.PtrToStringAnsi(hwName(t)));

lines.Sort(StringComparer.Ordinal);
File.WriteAllLines(args[1], lines);
Console.WriteLine($"{RuntimeInformation.ProcessArchitecture}: {lines.Count} entries");

delegate uint Ver();
delegate nint Iter(ref nint opaque);
delegate nint Iter2(ref nint opaque, int output);
delegate int IsDec(nint codec);
delegate int HwIter(int prev);
delegate nint HwName(int type);
