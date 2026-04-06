Embedded `llama.cpp` runtime location.

Required for `ModelProvider = "LlamaCppServer"`:
- `llama-server.exe`
- all dependent runtime libraries from the same llama.cpp build
  (for CUDA builds this typically includes `llama.dll`, `ggml*.dll`, and CUDA backend DLLs)

Behavior:
- If files are already present here, they are copied to output/publish automatically.
- If files are missing and `AiSettings:LlamaCppServerAutoDownloadRuntime=true`,
  the app auto-downloads runtime ZIP from `AiSettings:LlamaCppServerRuntimeUrl` on first startup.
