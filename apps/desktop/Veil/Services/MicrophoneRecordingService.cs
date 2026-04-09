using System.Runtime.InteropServices;
using Veil.Diagnostics;

namespace Veil.Services;

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMmDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role,
        [MarshalAs(UnmanagedType.Interface)] out IAudioMmDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMmDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    int GetPeakValue(out float peak);
    int GetMeteringChannelCount(out int channelCount);
    int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);
    int QueryHardwareSupport(out int hardwareSupportMask);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class AudioMmDeviceEnumerator
{
}

internal sealed class MicrophoneRecordingService : IDisposable
{
    private const int SpectrumBarCount = 12;
    private readonly float[] _smoothedLevels = new float[SpectrumBarCount];
    private static readonly Guid AudioMeterInformationIid = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private global::Windows.Media.Audio.AudioGraph? _audioGraph;
    private global::Windows.Media.Audio.AudioDeviceInputNode? _inputNode;
    private global::Windows.Media.Audio.AudioFileOutputNode? _fileOutputNode;
    private global::Windows.Media.Audio.AudioFrameOutputNode? _frameOutputNode;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private string? _currentRecordingPath;
    private bool _isRecording;
    private float _adaptiveGain = 10f;
    private float _noiseFloor = 0.0045f;
    private uint _frameBitsPerSample = 32;
    private uint _frameChannelCount = 1;
    private string _frameSubtype = "Float";
    private IAudioMeterInformation? _captureMeter;
    private int _meterPhase;

    internal bool IsRecording => _isRecording;

    internal event Action<IReadOnlyList<float>>? SpectrumChanged;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_isRecording)
            {
                return;
            }

            string directoryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Veil",
                "temp",
                "dictation");
            Directory.CreateDirectory(directoryPath);

            string recordingPath = Path.Combine(directoryPath, $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.wav");
            await File.WriteAllBytesAsync(recordingPath, [], cancellationToken);

            try
            {
                var storageFile = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(recordingPath);

                var graphSettings = new global::Windows.Media.Audio.AudioGraphSettings(
                    global::Windows.Media.Render.AudioRenderCategory.Speech)
                {
                    QuantumSizeSelectionMode = global::Windows.Media.Audio.QuantumSizeSelectionMode.SystemDefault
                };

                var graphResult = await global::Windows.Media.Audio.AudioGraph.CreateAsync(graphSettings);
                if (graphResult.Status != global::Windows.Media.Audio.AudioGraphCreationStatus.Success)
                {
                    throw new InvalidOperationException($"AudioGraph creation failed: {graphResult.Status}");
                }

                _audioGraph = graphResult.Graph;

                var inputResult = await _audioGraph.CreateDeviceInputNodeAsync(global::Windows.Media.Capture.MediaCategory.Speech);
                if (inputResult.Status != global::Windows.Media.Audio.AudioDeviceNodeCreationStatus.Success)
                {
                    throw new InvalidOperationException($"Microphone input creation failed: {inputResult.Status}");
                }

                _inputNode = inputResult.DeviceInputNode;
                _frameOutputNode = _audioGraph.CreateFrameOutputNode();
                var frameEncoding = _frameOutputNode.EncodingProperties;
                _frameBitsPerSample = frameEncoding.BitsPerSample;
                _frameChannelCount = Math.Max(1u, frameEncoding.ChannelCount);
                _frameSubtype = frameEncoding.Subtype ?? "Float";

                var profile = global::Windows.Media.MediaProperties.MediaEncodingProfile.CreateWav(
                    global::Windows.Media.MediaProperties.AudioEncodingQuality.Auto);
                profile.Audio = global::Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(16000, 1, 16);

                var fileResult = await _audioGraph.CreateFileOutputNodeAsync(storageFile, profile);
                if (fileResult.Status != global::Windows.Media.Audio.AudioFileNodeCreationStatus.Success)
                {
                    throw new InvalidOperationException($"Audio file output creation failed: {fileResult.Status}");
                }

                _fileOutputNode = fileResult.FileOutputNode;

                _inputNode.AddOutgoingConnection(_fileOutputNode);
                _inputNode.AddOutgoingConnection(_frameOutputNode);
                _audioGraph.QuantumStarted += OnQuantumStarted;
                _currentRecordingPath = recordingPath;
                _isRecording = true;
                Array.Fill(_smoothedLevels, 0);
                _adaptiveGain = 10f;
                _noiseFloor = 0.0045f;
                _meterPhase = 0;
                InitializeCaptureMeter();
                _audioGraph.Start();
            }
            catch
            {
                DisposeAudioGraph();
                if (File.Exists(recordingPath))
                {
                    File.Delete(recordingPath);
                }
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task<string?> StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!_isRecording)
            {
                return _currentRecordingPath;
            }

            _isRecording = false;

            try
            {
                StopAudioGraph();
                await FinalizeOutputNodeAsync();
            }
            finally
            {
                DisposeCaptureMeter();
                DisposeAudioGraph();
                Array.Fill(_smoothedLevels, 0);
                SpectrumChanged?.Invoke(CreateSilenceLevels());
            }

            return _currentRecordingPath;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnQuantumStarted(global::Windows.Media.Audio.AudioGraph sender, object args)
    {
        var frameOutputNode = _frameOutputNode;
        if (!_isRecording || frameOutputNode is null)
        {
            return;
        }

        try
        {
            float[] levels = GetMeterLevels();
            if (levels.All(static value => value <= 0f))
            {
                using var frame = frameOutputNode.GetFrame();
                levels = ComputeSpectrumLevels(frame, _frameBitsPerSample, _frameChannelCount, _frameSubtype);
            }

            SpectrumChanged?.Invoke(levels);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidComObjectException)
        {
        }
        catch (COMException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to sample live dictation spectrum.", ex);
        }
    }

    private unsafe float[] ComputeSpectrumLevels(
        global::Windows.Media.AudioFrame frame,
        uint bitsPerSample,
        uint channelCount,
        string subtype)
    {
        using var buffer = frame.LockBuffer(global::Windows.Media.AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        if (!TryGetBuffer(reference, out byte* dataInBytes, out _))
        {
            return CreateSilenceLevels();
        }

        uint lengthInBytes = buffer.Length;
        int channels = Math.Max(1, (int)channelCount);
        bool isFloatPcm = subtype.Contains("Float", StringComparison.OrdinalIgnoreCase) || bitsPerSample == 32;
        int sampleFrames = isFloatPcm
            ? (int)(lengthInBytes / sizeof(float)) / channels
            : bitsPerSample == 16
                ? (int)(lengthInBytes / sizeof(short)) / channels
                : 0;

        if (sampleFrames < SpectrumBarCount / 2)
        {
            return CreateSilenceLevels();
        }

        float[] levels = new float[SpectrumBarCount];
        int mirroredBarCount = SpectrumBarCount / 2;
        int samplesPerBar = Math.Max(1, (int)Math.Ceiling(sampleFrames / (double)mirroredBarCount));

        double globalEnergy = 0;
        float globalPeak = 0;
        int globalCount = 0;

        if (isFloatPcm)
        {
            float* samples = (float*)dataInBytes;
            for (int frameIndex = 0; frameIndex < sampleFrames; frameIndex++)
            {
                int frameOffset = frameIndex * channels;
                float monoSample = 0;
                for (int channelIndex = 0; channelIndex < channels; channelIndex++)
                {
                    monoSample += samples[frameOffset + channelIndex];
                }

                monoSample /= channels;
                globalEnergy += monoSample * monoSample;
                globalPeak = Math.Max(globalPeak, Math.Abs(monoSample));
                globalCount++;
            }
        }
        else if (bitsPerSample == 16)
        {
            short* samples = (short*)dataInBytes;
            for (int frameIndex = 0; frameIndex < sampleFrames; frameIndex++)
            {
                int frameOffset = frameIndex * channels;
                float monoSample = 0;
                for (int channelIndex = 0; channelIndex < channels; channelIndex++)
                {
                    monoSample += samples[frameOffset + channelIndex] / 32768f;
                }

                monoSample /= channels;
                globalEnergy += monoSample * monoSample;
                globalPeak = Math.Max(globalPeak, Math.Abs(monoSample));
                globalCount++;
            }
        }
        else
        {
            return CreateSilenceLevels();
        }

        if (globalCount == 0)
        {
            return CreateSilenceLevels();
        }

        float globalRms = (float)Math.Sqrt(globalEnergy / globalCount);
        bool mostlySilent = globalRms < (_noiseFloor * 2.4f) && globalPeak < 0.045f;
        float noiseTarget = mostlySilent ? globalRms : Math.Min(_noiseFloor, globalRms);
        _noiseFloor = Lerp(_noiseFloor, Math.Clamp(noiseTarget, 0.0025f, 0.018f), mostlySilent ? 0.16f : 0.03f);

        float signalStrength = Math.Max(0f, globalPeak - _noiseFloor);
        float targetGain = Math.Clamp(0.92f / Math.Max(signalStrength, 0.018f), 1.8f, 26f);
        _adaptiveGain = Lerp(_adaptiveGain, targetGain, mostlySilent ? 0.06f : 0.26f);

        for (int barIndex = 0; barIndex < mirroredBarCount; barIndex++)
        {
            int start = barIndex * samplesPerBar;
            int end = barIndex == mirroredBarCount - 1
                ? sampleFrames
                : Math.Min(sampleFrames, start + samplesPerBar);

            double chunkEnergy = 0;
            int chunkCount = 0;
            float chunkPeak = 0;

            if (isFloatPcm)
            {
                float* samples = (float*)dataInBytes;
                for (int frameIndex = start; frameIndex < end; frameIndex++)
                {
                    int frameOffset = frameIndex * channels;
                    float monoSample = 0;
                    for (int channelIndex = 0; channelIndex < channels; channelIndex++)
                    {
                        monoSample += samples[frameOffset + channelIndex];
                    }

                    monoSample /= channels;
                    chunkEnergy += monoSample * monoSample;
                    chunkPeak = Math.Max(chunkPeak, Math.Abs(monoSample));
                    chunkCount++;
                }
            }
            else
            {
                short* samples = (short*)dataInBytes;
                for (int frameIndex = start; frameIndex < end; frameIndex++)
                {
                    int frameOffset = frameIndex * channels;
                    float monoSample = 0;
                    for (int channelIndex = 0; channelIndex < channels; channelIndex++)
                    {
                        monoSample += samples[frameOffset + channelIndex] / 32768f;
                    }

                    monoSample /= channels;
                    chunkEnergy += monoSample * monoSample;
                    chunkPeak = Math.Max(chunkPeak, Math.Abs(monoSample));
                    chunkCount++;
                }
            }

            if (chunkCount == 0)
            {
                SetMirroredLevel(levels, barIndex, 0f, mostlySilent);
                continue;
            }

            float rms = (float)Math.Sqrt(chunkEnergy / chunkCount);
            float activity = Math.Max(0f, ((rms * 0.9f) + (chunkPeak * 0.7f)) - _noiseFloor);
            float normalized = Math.Clamp(activity * _adaptiveGain, 0f, 1f);
            normalized = MathF.Pow(normalized, 0.52f);

            if (mostlySilent)
            {
                normalized *= 0.18f;
            }

            SetMirroredLevel(levels, barIndex, normalized, mostlySilent);
        }

        if (mostlySilent)
        {
            for (int index = 0; index < levels.Length; index++)
            {
                levels[index] = Math.Min(levels[index], 0.045f);
            }
        }

        return levels;
    }

    private static float[] CreateSilenceLevels()
    {
        return new float[SpectrumBarCount];
    }

    private float[] GetMeterLevels()
    {
        try
        {
            if (_captureMeter is null)
            {
                return CreateSilenceLevels();
            }

            int hr = _captureMeter.GetPeakValue(out float peak);
            if (hr != 0)
            {
                return CreateSilenceLevels();
            }

            peak = Math.Clamp(peak, 0f, 1f);
            float normalizedPeak = MathF.Pow(peak, 0.62f);
            return CreateMeterLevels(normalizedPeak);
        }
        catch
        {
            return CreateSilenceLevels();
        }
    }

    private float[] CreateMeterLevels(float peak)
    {
        if (peak < 0.01f)
        {
            for (int index = 0; index < _smoothedLevels.Length; index++)
            {
                _smoothedLevels[index] = Lerp(_smoothedLevels[index], 0f, 0.16f);
            }

            return _smoothedLevels.ToArray();
        }

        _meterPhase++;
        float[] levels = new float[SpectrumBarCount];

        for (int index = 0; index < SpectrumBarCount; index++)
        {
            float distanceFromCenter = Math.Abs(index - ((SpectrumBarCount - 1) / 2f));
            float centerBias = 1f - (distanceFromCenter / (SpectrumBarCount / 2f));
            float wave = 0.58f + (0.42f * MathF.Abs(MathF.Sin((_meterPhase * 0.38f) + (index * 0.72f))));
            float target = Math.Clamp((peak * (0.45f + (centerBias * 0.8f))) * wave, 0f, 1f);
            float lerpAmount = target > _smoothedLevels[index] ? 0.7f : 0.22f;
            _smoothedLevels[index] = Lerp(_smoothedLevels[index], target, lerpAmount);
            levels[index] = _smoothedLevels[index];
        }

        return levels;
    }

    private static unsafe bool TryGetBuffer(object reference, out byte* buffer, out uint capacity)
    {
        buffer = null;
        capacity = 0;

        IntPtr unknownPointer = IntPtr.Zero;
        IntPtr accessPointer = IntPtr.Zero;

        try
        {
            unknownPointer = Marshal.GetIUnknownForObject(reference);
            Guid iid = typeof(IMemoryBufferByteAccess).GUID;
            int hr = Marshal.QueryInterface(unknownPointer, in iid, out accessPointer);
            if (hr != 0 || accessPointer == IntPtr.Zero)
            {
                return false;
            }

            var access = (IMemoryBufferByteAccess)Marshal.GetObjectForIUnknown(accessPointer);
            access.GetBuffer(out buffer, out capacity);
            return buffer is not null && capacity > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (accessPointer != IntPtr.Zero)
            {
                Marshal.Release(accessPointer);
            }

            if (unknownPointer != IntPtr.Zero)
            {
                Marshal.Release(unknownPointer);
            }
        }
    }

    private void InitializeCaptureMeter()
    {
        DisposeCaptureMeter();

        try
        {
            var enumerator = (IAudioMmDeviceEnumerator)(object)new AudioMmDeviceEnumerator();
            int hr = enumerator.GetDefaultAudioEndpoint(1, 1, out IAudioMmDevice device);
            if (hr != 0)
            {
                return;
            }

            Guid iid = AudioMeterInformationIid;
            hr = device.Activate(ref iid, 23, IntPtr.Zero, out object meterObject);
            if (hr != 0)
            {
                return;
            }

            _captureMeter = meterObject as IAudioMeterInformation;
        }
        catch
        {
            _captureMeter = null;
        }
    }

    private void SetMirroredLevel(float[] levels, int sourceIndex, float normalized, bool mostlySilent)
    {
        int leftIndex = (SpectrumBarCount / 2) - 1 - sourceIndex;
        int rightIndex = (SpectrumBarCount / 2) + sourceIndex;
        float attack = mostlySilent ? 0.08f : normalized > _smoothedLevels[leftIndex] ? 0.62f : 0.18f;
        float smoothed = Lerp(_smoothedLevels[leftIndex], normalized, attack);
        _smoothedLevels[leftIndex] = smoothed;
        _smoothedLevels[rightIndex] = smoothed;
        levels[leftIndex] = smoothed;
        levels[rightIndex] = smoothed;
    }

    internal void DeleteRecording(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private void DisposeAudioGraph()
    {
        if (_audioGraph is not null)
        {
            _audioGraph.QuantumStarted -= OnQuantumStarted;
        }

        DisposeNode(_inputNode);
        _inputNode = null;

        DisposeNode(_frameOutputNode);
        _frameOutputNode = null;

        DisposeNode(_fileOutputNode);
        _fileOutputNode = null;

        DisposeNode(_audioGraph);
        _audioGraph = null;
    }

    public void Dispose()
    {
        DisposeCaptureMeter();
        DisposeAudioGraph();
        _isRecording = false;
        Array.Fill(_smoothedLevels, 0);
        _lifecycleGate.Dispose();
    }

    private static float Lerp(float from, float to, float amount)
    {
        return from + ((to - from) * amount);
    }

    private async Task FinalizeOutputNodeAsync()
    {
        try
        {
            if (_fileOutputNode is not null)
            {
                await _fileOutputNode.FinalizeAsync();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidComObjectException)
        {
        }
        catch (COMException)
        {
        }
    }

    private void StopAudioGraph()
    {
        try
        {
            _audioGraph?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidComObjectException)
        {
        }
        catch (COMException)
        {
        }
    }

    private static void DisposeNode(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidComObjectException)
        {
        }
        catch (COMException)
        {
        }
    }

    private void DisposeCaptureMeter()
    {
        if (_captureMeter is null)
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(_captureMeter);
        }
        catch
        {
        }
        finally
        {
            _captureMeter = null;
        }
    }
}
