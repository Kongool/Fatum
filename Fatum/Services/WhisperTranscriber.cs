using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Fatum.Services;

/// <summary>
/// Wraps Whisper.net. The factory (model) is loaded lazily on first use and reused.
/// Transcription runs on a background thread so the game thread never blocks.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private WhisperFactory? factory;
    private string loadedModelPath = string.Empty;
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private static bool nativeLoaded;

    public bool IsModelLoaded => factory is not null;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    /// <summary>
    /// Dalamud loads plugins in a custom context whose base directory is the game .exe
    /// folder, not the plugin folder, so Whisper.net can't find its native libs on its
    /// own. Preload them by full path (in dependency order) from the plugin directory;
    /// once loaded, Whisper.net's own LoadLibrary("whisper") resolves to these.
    /// </summary>
    private static void EnsureNativeLibraries()
    {
        if (nativeLoaded) return;

        var dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrEmpty(dir))
        {
            Plugin.Log.Warning("[Fatum] Could not resolve plugin directory for native Whisper libs.");
            return;
        }

        // Point Whisper.net's own loader at the plugin folder. Its loader does
        // Path.GetDirectoryName(LibraryPath) and then searches <dir>/runtimes/win-x64/,
        // so LibraryPath must be a FILE path inside the plugin folder (not the folder
        // itself, or GetDirectoryName would resolve to the parent). Use our own DLL.
        RuntimeOptions.LibraryPath = Plugin.PluginInterface.AssemblyLocation.FullName;

        // Belt-and-suspenders: add the folder to the DLL search path and preload the
        // natives in dependency order so whisper.dll's siblings resolve.
        SetDllDirectory(dir);

        foreach (var name in new[]
                 {
                     "ggml-base-whisper.dll",
                     "ggml-cpu-whisper.dll",
                     "ggml-whisper.dll",
                     "whisper.dll",
                 })
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _))
                Plugin.Log.Debug($"[Fatum] Preloaded native lib: {name}");
            else if (!File.Exists(path))
                Plugin.Log.Warning($"[Fatum] Native lib missing: {path}");
        }

        nativeLoaded = true;
    }

    private async Task EnsureFactoryAsync(string modelPath)
    {
        if (factory is not null && loadedModelPath == modelPath)
            return;

        await loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (factory is not null && loadedModelPath == modelPath)
                return;

            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                throw new FileNotFoundException(
                    "Whisper model not found. Set a valid ggml model path in /fatum settings.", modelPath);

            EnsureNativeLibraries();

            factory?.Dispose();
            factory = WhisperFactory.FromPath(modelPath);
            loadedModelPath = modelPath;
        }
        finally
        {
            loadLock.Release();
        }
    }

    /// <summary>Transcribe 16 kHz mono float samples to text.</summary>
    public async Task<string> TranscribeAsync(
        float[] samples, string modelPath, string language, CancellationToken ct = default)
    {
        await EnsureFactoryAsync(modelPath).ConfigureAwait(false);

        var builder = factory!.CreateBuilder();
        builder = string.IsNullOrWhiteSpace(language) || language == "auto"
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language);

        using var processor = builder.Build();

        var sb = new StringBuilder();
        var segments = 0;
        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            segments++;
            sb.Append(segment.Text);
        }

        var result = sb.ToString().Trim();
        Plugin.Log.Info($"[Fatum] Whisper: lang='{language}', {segments} segment(s), text='{result}'");
        return result;
    }

    public void Dispose()
    {
        factory?.Dispose();
        factory = null;
    }
}
