using System.Collections.Concurrent;
using System.Threading.Channels;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Structs;
using SoundFlow.Components;
using Whisper.net;

class Program
{
    // Buffer de áudio bruto vindo do microfone
    static readonly ConcurrentQueue<short> audioQueue = new();
    static string path = Path.Combine(Environment.CurrentDirectory, "conversa.txt");
    // Canal para enviar frases prontas para transcrição sem bloquear a captura
    static readonly Channel<short[]> phraseChannel = Channel.CreateUnbounded<short[]>();

    const int SAMPLE_RATE = 16000;

    // Tunáveis
    const int SILENCE_TO_CLOSE_MS = 700;
    const int MAX_PHRASE_MS = 8000;
    const int MIN_PHRASE_SAMPLES = (int)(SAMPLE_RATE * 1.2);
    const int OVERLAP_MS = 1500;
    const int OVERLAP_SAMPLES = SAMPLE_RATE * OVERLAP_MS / 1000;

    static async Task Main()
    {
        // -------- CONFIGURAÇÃO WHISPER --------
        var factory = WhisperFactory.FromPath("ggml-base.en-q8_0.bin");
        var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2)) // Otimiza threads
            .Build();

        // Inicia o trabalhador de transcrição em segundo plano
        _ = Task.Run(() => TranscriptionWorker(processor));

        // -------- CONFIGURAÇÃO ÁUDIO --------
        var engine = new MiniAudioEngine();
        if (engine.CaptureDevices.Length == 0)
        {
            Console.WriteLine("----> Nenhum dispositivo de captura encontrado.");
            return;
        }

        Console.WriteLine("----> Selecione a fonte de áudio:\n");
        for (int i = 0; i < engine.CaptureDevices.Length; i++)
            Console.WriteLine($"{i}: {engine.CaptureDevices[i].Name}");

        Console.WriteLine("\n----> Digite o número do dispositivo: ");
        if (!int.TryParse(Console.ReadLine(), out int index) ||
            index < 0 || index >= engine.CaptureDevices.Length)
        {
            Console.WriteLine("----> Seleção inválida.");
            return;
        }

        var format = new AudioFormat
        {
            SampleRate = SAMPLE_RATE,
            Channels = 1,
            Format = SoundFlow.Enums.SampleFormat.F32
        };

        var device = engine.InitializeCaptureDevice(engine.CaptureDevices[index], format, null);

        var recorder = new Recorder(device, (samples, _) =>
        {
            // Conversão rápida para short e enfileiramento
            foreach (var f in samples)
            {
                audioQueue.Enqueue((short)Math.Clamp(f * short.MaxValue, short.MinValue, short.MaxValue));
            }
        });

        device.Start();
        recorder.StartRecording();

        Console.WriteLine("----> Ouvindo (Processamento Assíncrono Ativado)...");

        // -------- LOOP DE DETECÇÃO (VAD) --------
        List<short> currentPhraseBuffer = new();
        bool isSpeaking = false;
        DateTime lastVoiceTime = DateTime.MinValue;
        DateTime phraseStart = DateTime.MinValue;

        while (true)
        {
            // Processa o que estiver na fila de áudio
            if (audioQueue.TryDequeue(out short sample))
            {
                // Agrupamos em pequenos chunks para o VAD não rodar a cada sample
                // mas aqui vamos simplificar para manter a lógica do usuário adaptada
                currentPhraseBuffer.Add(sample);

                // A cada 100ms de áudio (1600 samples), verificamos o estado
                if (currentPhraseBuffer.Count % 1600 == 0)
                {
                    var lastChunk = currentPhraseBuffer.Skip(currentPhraseBuffer.Count - 1600).ToArray();
                    bool hasVoice = HasVoice(lastChunk, isSpeaking);

                    if (hasVoice)
                    {
                        if (!isSpeaking)
                        {
                            phraseStart = DateTime.UtcNow;
                            Console.WriteLine("----> Voz detectada");
                        }
                        isSpeaking = true;
                        lastVoiceTime = DateTime.UtcNow;
                    }
                    else if (isSpeaking)
                    {
                        var silenceMs = (DateTime.UtcNow - lastVoiceTime).TotalMilliseconds;
                        var phraseMs = (DateTime.UtcNow - phraseStart).TotalMilliseconds;

                        bool forceClose = phraseMs > MAX_PHRASE_MS;
                        bool silenceClose = silenceMs > SILENCE_TO_CLOSE_MS;

                        if (forceClose || silenceClose)
                        {
                            // Fecha a frase e envia para o canal de transcrição
                            if (currentPhraseBuffer.Count >= MIN_PHRASE_SAMPLES)
                            {
                                var phraseToProcess = currentPhraseBuffer.ToArray();
                                phraseChannel.Writer.TryWrite(phraseToProcess);
                            }

                            // Lógica de Overlap se for fechamento forçado
                            if (forceClose && currentPhraseBuffer.Count > OVERLAP_SAMPLES)
                            {
                                var tail = currentPhraseBuffer.GetRange(currentPhraseBuffer.Count - OVERLAP_SAMPLES, OVERLAP_SAMPLES);
                                currentPhraseBuffer.Clear();
                                currentPhraseBuffer.AddRange(tail);
                                phraseStart = DateTime.UtcNow;
                                // isSpeaking continua true
                            }
                            else
                            {
                                currentPhraseBuffer.Clear();
                                isSpeaking = false;
                            }
                        }
                    }
                    else
                    {
                        // Silêncio contínuo, limpa buffer para não acumular lixo
                        if (currentPhraseBuffer.Count > 3200) // Mantém um pequeno histórico
                            currentPhraseBuffer.RemoveRange(0, 1600);
                    }
                }
            }
            else
            {
                // Se não há áudio novo, espera um pouco para não fritar a CPU
                await Task.Delay(10);
            }
        }
    }

    // -------- TRABALHADOR DE TRANSCRIÇÃO (RODA EM PARALELO) --------
    static async Task TranscriptionWorker(WhisperProcessor processor)
    {
        await foreach (var pcmData in phraseChannel.Reader.ReadAllAsync())
        {
            try
            {
                var startTime = DateTime.UtcNow;
                var wavData = ToWav(pcmData, SAMPLE_RATE);
                using var ms = new MemoryStream(wavData);

                string finalText = "";
                await foreach (var segment in processor.ProcessAsync(ms))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                        finalText += segment.Text;
                }

                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    Console.WriteLine($"[Transcrição] ({duration:F0}ms): {finalText.Trim()}");
                    File.AppendAllText(
                        path,
                        $"{Environment.NewLine}[Transcrição] ({duration:F0}ms): {finalText.Trim()}"
                    );
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"----> Erro na transcrição: {ex.Message}");
            }
        }
    }

    // -------- VAD OTIMIZADO --------
    static bool HasVoice(short[] pcm, bool currentlySpeaking)
    {
        long sum = 0;
        for (int i = 0; i < pcm.Length; i++)
            sum += (long)pcm[i] * pcm[i];

        var rms = Math.Sqrt(sum / (double)pcm.Length);

        const double VOICE_ON = 750;
        const double VOICE_OFF = 550;

        if (rms > VOICE_ON) return true;
        if (rms < VOICE_OFF) return false;
        return currentlySpeaking;
    }

    // -------- WAV (MANTIDO PARA COMPATIBILIDADE, MAS EM WORKER) --------
    static byte[] ToWav(short[] pcm, int sampleRate)
    {
        var buffer = new byte[44 + pcm.Length * 2];
        using var ms = new MemoryStream(buffer);
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF".ToCharArray());
        bw.Write(36 + pcm.Length * 2);
        bw.Write("WAVEfmt ".ToCharArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data".ToCharArray());
        bw.Write(pcm.Length * 2);

        for (int i = 0; i < pcm.Length; i++)
            bw.Write(pcm[i]);

        return buffer;
    }
}
