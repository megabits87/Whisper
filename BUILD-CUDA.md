# Windows 11 CUDA build

This branch uses upstream `whisper.cpp` as the inference backend. Build the
backend with modern ggml CUDA flags:

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64 `
  -DGGML_CUDA=ON `
  -DWHISPER_BUILD_EXAMPLES=OFF `
  -DWHISPER_BUILD_TESTS=OFF `
  -DWHISPER_BUILD_SERVER=OFF

cmake --build build --config Release
```

Do not use deprecated `WHISPER_CUBLAS` or old `WHISPER_CUDA` options.

Copy the generated `whisper.dll` and `ggml*.dll` files from `build\bin\Release`
next to `Whisper.dll` or the desktop executable, then build the existing Visual
Studio solution:

```powershell
msbuild WhisperCpp.sln /m /p:Configuration=Release /p:Platform=x64
```

The runtime attempts CUDA first with `use_gpu=true`, `flash_attn=true`, and
`gpu_device=0`. If the CUDA backend cannot initialize, it falls back to CPU and
logs a warning.

Required model coverage target:

- base
- small
- medium
- large-v3
- large-v3-turbo

Use CUDA 12 or newer for RTX 30-series GPUs such as RTX 3060.
