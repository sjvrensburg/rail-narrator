# ADR-0010: Claude as an option for maths

**Status:** accepted · **Date:** 2026-11-19 · **Follows:** ADR-0001 (Core 0.94.0)

## Decision

The reader chose (2026-11-19) that Claude should be an option for both maths endpoints: the
language model that interprets maths, and the vision model that transcribes equation images for
the older reading modes. Each endpoint has its own choice of service, `MathLlmProvider` and
`VlmProvider`: `OpenAiCompatible`, the default, or `Anthropic`. A settings file written before the
choice existed keeps its OpenAI-compatible endpoint.

- **The vision model** goes through RailReaderCore, as before: Core's `AnthropicVlmClient`
  (`RailReader.Core.Vlm.Anthropic`, new in 0.94.0) in place of its `OpenAIVlmClient`, chosen with
  `VlmEndpointConfig.Provider`.
- **Interpretation, the mechanical reading and verbalisation** do not go through Core, so they get
  clients here: `ClaudeMathInterpreter` (also the `IMathReader`) and `ClaudeMathVerbaliser`, in
  `RailNarrator.Inference`, over the official Anthropic .NET SDK (`Anthropic` 13.10.0, the version
  Core 0.94.0 builds against). They use the same prompts, the same validators
  (`SpokenMathsValidator`, `MathResponseValidator`) and the same reading by rule
  (`MathTranscriptionReading`, `LocalLatexSpeaker`) as the OpenAI-compatible clients. Only the
  transport differs.
- **Choosing Claude is the configuration.** The address is optional (a gateway that speaks the
  Messages API; empty means Anthropic's own API), an empty model means `claude-opus-5-5`, and a key
  that is neither stored nor named is left to the SDK to find (`ANTHROPIC_API_KEY`, or a profile
  from `ant auth login`). A start-up note says that passages are sent to Anthropic and to which
  address.

## How the Messages API is used

- **Thinking is not switched.** Current Claude models decide their own thinking and reject a
  request that disables it. `MathThinking` therefore sets `output_config.effort`: medium where the
  other endpoint's model would think (display equations and their lead-ins by default), low
  elsewhere. A model that does not know the field is asked again without it, and remembered.
- **No sampling parameters** are sent; current models reject them.
- **`max_tokens` is 16000**, because thinking counts against it. Only generated tokens are billed.
- **The repair turn is a new request.** An answer with notation gets one repair, as before, but as
  a fresh request that carries the rejected answer as text, not as a continued conversation. A
  continued conversation would have to replay the model's thinking blocks unchanged.
- **Refusals.** A passage Claude declines (`stop_reason: refusal`) is rejected at once, not asked
  again. Against Anthropic's own API, a model that supports it is sent `fallbacks: "default"` (beta
  `server-side-fallback-2026-07-01`), so a declined passage may be answered by the model Anthropic
  names for that refusal within the same call. A gateway gets the plain request.
- **Verbalisation asks for structured output** (`output_config.format`) with the response schema
  less its string-length keywords, which structured output does not take. The answer is still
  validated here against the whole contract. A gateway that refuses the schema is asked without it.
- **The Settings test** asks the Models API for the model and the list. It costs no tokens.
- **Caching.** The interpreter's revision is `…|claude|<address>|<model>`, so Claude's answers are
  cached apart from another endpoint's and a change of model asks again.

## What it costs

- A remote data path. With Claude chosen, each passage with maths leaves the machine: its page
  image (a crop per page it touches), its text layer, and up to 1,500 characters of the text
  before it and 400 after. The Settings dialogue says so beside the choice, and nothing is sent
  unless Claude is chosen.
- About 12 MB in a release: `Anthropic.dll` (11.2 MB) and `Microsoft.Extensions.AI.Abstractions`
  (0.7 MB).
- Money per passage, which a local model does not cost. It was not measured.

## Verified, and not

**Verified** with the SDK's transport stubbed below it, so the requests checked are the ones the
SDK sends (`ClaudeTests`, 19 tests): the request's shape (image first, the prompt as the system
text, no sampling or thinking fields), effort by thinking policy, `fallbacks` only against
Anthropic's own API and only for a model that takes it, the repair as a new request, notation
after the repair refused and never passed through, a declined passage not asked again, a cut-off
answer refused, the effort field dropped for a model that rejects it, a refused key ending the
passage without the key in any message, the reading by rule from Claude's transcription,
structured output for verbalisation, the clients built and answering sensibly with no key
anywhere, and the Settings test's four outcomes.

**Seen in the running application** (headless compositor, isolated settings, 2026-11-19): with
Claude chosen and no key, the application starts as usual; Settings ▸ Maths shows the Service
row, its description and the Claude placeholders; and Test, which asks Anthropic's real Models
API, answers "api.anthropic.com wants an API key, and none was found".

**Verified against Anthropic's real API** (`ClaudeIntegrationTests`, five tests,
`RN_CLAUDE_INTEGRATION=1`, run 2026-11-19 with `claude-opus-5-5`): an equation drawn as a page crop
is interpreted from its image into words the validator accepts ("The response y equals the
intercept a, plus the slope b times the regressor x, plus the error e.", in about a second); the
same crop is transcribed and read by rule ("y equals a plus b x plus e"); an expression is
verbalised through structured output ("beta hat equals the inverse of the quantity X transpose X,
times X transpose, times y."); Core's client transcribes the crop for the older modes; and the
Settings test finds the model. In those runs no optional field was refused: the effort, the
refusal fallback and the response schema were accepted as sent.

**Not verified:** Claude on a real paper. `rail-narrator-mathbench --provider claude` is there for
it. Not measured: how its interpretations read against the local model's on the GARCH paper's 224
passages, the time and the cost per passage, and whether medium effort is more than an equation
needs. Nobody has listened to it.
