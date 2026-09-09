# speechToText

Console app em C# que transcreve áudio do microfone em tempo real usando
[Whisper.net](https://github.com/sandrohanea/whisper.net) (bindings do
whisper.cpp) e [SoundFlow](https://github.com/LSXPrime/SoundFlow) para
captura de áudio.

O programa escuta o microfone, detecta trechos de fala (VAD simples baseado
em energia RMS), corta a fala em frases e transcreve cada frase em segundo
plano, sem bloquear a captura. O texto transcrito é impresso no console e
também é anexado ao arquivo `conversa.txt`.

## Como funciona

- **Captura de áudio**: `SoundFlow` abre o dispositivo de captura escolhido
  pelo usuário e entrega amostras em `float`, que são convertidas para
  `short` (PCM 16-bit, mono, 16 kHz) e enfileiradas.
- **Detecção de voz (VAD)**: a cada ~100 ms de áudio, calcula-se o RMS do
  bloco. Acima de um limiar o trecho é considerado voz; abaixo de outro
  limiar é considerado silêncio (histerese entre os dois evita "flicker").
- **Segmentação em frases**: enquanto há voz, as amostras acumulam num
  buffer. A frase é fechada quando há silêncio suficiente
  (`SILENCE_TO_CLOSE_MS`) ou quando ela fica longa demais
  (`MAX_PHRASE_MS`), mantendo uma sobreposição (`OVERLAP_MS`) entre frases
  cortadas à força para não perder contexto.
- **Transcrição assíncrona**: cada frase fechada é enviada por um
  `Channel` para um worker em background, que converte o PCM para WAV em
  memória e roda o modelo Whisper (`ggml-base.en-q8_0.bin`, incluído no
  repo) sobre ele.
- **Saída**: cada transcrição é impressa no console com o tempo de
  processamento e anexada a `conversa.txt`.

O idioma de transcrição está fixado em inglês (`WithLanguage("en")` em
`Program.cs:30`).

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download) (versão travada em
  `global.json` do repositório pai: `10.0.202`)
- Um dispositivo de captura de áudio (microfone) disponível no sistema
- Windows, Linux ou macOS (a captura usa o backend MiniAudio do
  SoundFlow, que é multiplataforma)
- Opcionalmente, uma GPU NVIDIA com CUDA para acelerar a inferência do
  Whisper (o pacote `Whisper.net.Runtime.Cuda` é referenciado, mas o app
  funciona em CPU sem GPU)

O modelo `ggml-base.en-q8_0.bin` (~80 MB) já está versionado no repositório
e é carregado a partir do diretório de execução — não é necessário baixar
nada à parte.

## Como rodar

```bash
dotnet restore
dotnet run
```

Ao iniciar, o programa lista os dispositivos de captura disponíveis:

```
----> Selecione a fonte de áudio:

0: Microfone (Realtek Audio)
1: ...

----> Digite o número do dispositivo:
```

Digite o número do dispositivo desejado e pressione Enter. O app passa a
ouvir continuamente; ao detectar fala e um silêncio subsequente, ele
transcreve o trecho e imprime algo como:

```
----> Voz detectada
[Transcrição] (580ms): Small grammatical words just pass by.
```

As mesmas linhas são anexadas a `conversa.txt`. Para encerrar, interrompa o
processo (Ctrl+C) — não há um comando de saída dentro do app.

## Testes

Não há projeto de testes automatizados neste repositório. `dotnet build`
compila sem erros; a verificação de funcionamento real do pipeline de
áudio/transcrição depende de rodar `dotnet run` manualmente com um
microfone conectado.
