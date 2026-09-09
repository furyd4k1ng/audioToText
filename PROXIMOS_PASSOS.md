# Próximos passos

Ideias de melhoria para o projeto, levantadas a partir da leitura de
`Program.cs`. Nada aqui está implementado ainda — é uma lista de
possibilidades para priorizar depois.

## Testes

- Não existe nenhum projeto de teste no repositório. `HasVoice` e `ToWav`
  são funções puras (recebem `short[]`, devolvem `bool`/`byte[]`) e dariam
  para testar isoladamente sem precisar de microfone nem do modelo
  Whisper — bom primeiro passo para começar a cobrir o código com testes.
- A lógica de segmentação de frases (quando abrir/fechar uma frase, quando
  aplicar overlap) está toda dentro do loop do `Main`, misturada com I/O de
  áudio. Extrair essa máquina de estados para uma classe própria
  (ex: `PhraseSegmenter`) permitiria testá-la com sequências de amostras
  sintéticas, sem depender de hardware.

## Robustez / tratamento de erros

- Não há tratamento de erro na seleção do dispositivo de captura: se o
  usuário digitar algo inválido, o programa simplesmente encerra
  (`Program.cs:50-55`). Poderia pedir de novo em loop.
- Não há `CancellationToken` nem tratamento de Ctrl+C: ao encerrar o
  processo, `device`, `recorder` e `processor` não são liberados
  explicitamente, e a última frase em andamento é perdida sem aviso.
- Se `WhisperFactory.FromPath` falhar (ex: arquivo do modelo ausente ou
  corrompido), a exceção sobe sem mensagem amigável — vale um try/catch
  com uma mensagem clara de "modelo não encontrado".

## Configuração

- Idioma (`WithLanguage("en")`), sample rate, e os limiares de VAD
  (`VOICE_ON`/`VOICE_OFF`, `SILENCE_TO_CLOSE_MS`, `MAX_PHRASE_MS`, etc.)
  estão todos fixos no código. Expor isso via argumentos de linha de
  comando ou um `appsettings.json` facilitaria ajustar para outros
  idiomas/microfones sem recompilar.
- Não dá para rodar de forma não-interativa (ex: escolher o dispositivo
  por argumento) — útil para automatizar ou rodar em background/serviço.
- Caminho de saída (`conversa.txt`) e do modelo (`ggml-base.en-q8_0.bin`)
  são fixos relativos ao diretório de execução; dava para tornar
  configuráveis.

## VAD (detecção de voz)

- O VAD atual é um limiar fixo de RMS (`Program.cs:204-205`), que não se
  adapta a microfones ou ambientes diferentes (ruído de fundo mais alto
  faria o app "ouvir voz" o tempo todo, por exemplo). Uma calibração
  automática no início (medir o ruído ambiente por alguns segundos) ou
  troca por um VAD mais robusto (ex: Silero VAD, WebRTC VAD) melhoraria a
  precisão.
- A lógica de overlap ao forçar o fechamento de uma frase longa
  (`Program.cs:129-136`) pode fazer o mesmo trecho de áudio ser
  transcrito duas vezes (uma vez no final da frase anterior, outra no
  início da próxima), gerando texto duplicado em `conversa.txt`. Vale
  revisar/testar esse caso com uma fala contínua mais longa que
  `MAX_PHRASE_MS`.

## Performance

- `audioQueue` é um `ConcurrentQueue<short>` alimentado amostra por
  amostra pelo callback do `Recorder`, e consumido também amostra por
  amostra no loop principal com `Task.Delay(10)` quando vazio. Processar
  em blocos (o callback já recebe `samples` em lote) reduziria overhead de
  fila e latência.
- `currentPhraseBuffer` é um `List<short>` com `RemoveRange(0, 1600)`
  periódico durante silêncio (`Program.cs:147-148`), que é O(n) a cada
  chamada. Um buffer circular evitaria esse custo crescendo com o tempo de
  silêncio contínuo.

## Empacotamento / distribuição

- O modelo `ggml-base.en-q8_0.bin` (~80 MB) está versionado direto no
  Git, o que deixa o clone do repositório pesado. Alternativas: Git LFS,
  ou baixar o modelo automaticamente no primeiro uso (o próprio
  Whisper.net tem utilitários para isso).
- `Whisper.net.Runtime.Cuda` é referenciado incondicionalmente, mesmo para
  quem não tem GPU NVIDIA — aumenta o tamanho do build. Poderia ser um
  pacote opcional, escolhido em tempo de build ou execução.

## Funcionalidades futuras

- Suporte a múltiplos idiomas ou detecção automática de idioma (Whisper
  suporta, mas está fixado em inglês aqui).
- Comando para encerrar a escuta sem matar o processo (hoje só dá para
  parar com Ctrl+C).
- Timestamp de início de cada frase no arquivo de saída, não só a duração
  do processamento — útil para revisar depois quando cada trecho foi
  falado.
