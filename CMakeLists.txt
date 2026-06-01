cmake_minimum_required(VERSION 3.24)

project(ConstMeWhisperModernized LANGUAGES C CXX)

option(GGML_CUDA "Enable the modern ggml CUDA backend for whisper.cpp" ON)
option(WHISPER_BUILD_EXAMPLES "Build upstream whisper.cpp examples" OFF)
option(WHISPER_BUILD_TESTS "Build upstream whisper.cpp tests" OFF)
option(WHISPER_BUILD_SERVER "Build upstream whisper.cpp server" OFF)

add_subdirectory(third_party/whisper.cpp)

message(STATUS "Const-me/Whisper uses the existing Visual Studio solution for the GUI and COM wrapper.")
message(STATUS "Build this CMake tree with -DGGML_CUDA=ON, then copy whisper.dll and ggml*.dll next to Whisper.dll or the GUI executable.")
