using Whisper;
using VoiceTyper;

string modelPath = args.Length > 0 ? args[0] : @"C:\Users\admin\Downloads\ggml-large-v3-turbo.bin";
string wavPath = args.Length > 1 ? args[1] : @"d:\OneDrive\Документы\Codex\Whisper\SampleClips\jfk.wav";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Model: {modelPath}");
Console.WriteLine($"Wav:   {wavPath}");

using iMediaFoundation mf = Library.initMediaFoundation();
Console.WriteLine("Loading model...");
using iModel model = Library.loadModel(modelPath);
Console.WriteLine($"Model loaded. multilingual={model.isMultilingual()}");

using Context ctx = model.createContext();
eLanguage lang = args.Length > 2 ? (Library.languageFromCode(args[2]) ?? eLanguage.Ukrainian) : eLanguage.Ukrainian;
ctx.parameters.language = lang;
Console.WriteLine($"Forcing language = {lang} ({lang.getCode()})");
ctx.parameters.setFlag(eFullParamsFlags.PrintTimestamps, false);

using iAudioBuffer fileBuf = mf.loadAudioFile(wavPath, false);
int n = fileBuf.countSamples();
Console.WriteLine($"Audio samples: {n}  ({n / 16000.0:0.0}s)");

// Copy the decoded PCM into a managed float[] and feed it back through a C#-implemented
// iAudioBuffer (the same pattern VoiceTyper uses for live microphone capture).
float[] mono = new float[n];
System.Runtime.InteropServices.Marshal.Copy(fileBuf.getPcmMono(), mono, 0, n);
using iAudioBuffer buf = new ManagedPcmBuffer(mono);
Console.WriteLine($"Managed buffer samples: {buf.countSamples()}");

ctx.runFull(buf);

var res = ctx.results();
Console.WriteLine($"Segments: {res.segments.Length}");
foreach (var seg in res.segments)
    Console.WriteLine($"  >> {seg.text}");

Console.WriteLine("DONE");
