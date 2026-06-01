#pragma once
#include "../API/iContext.cl.h"
#include "../ComLightLib/comLightServer.h"
#include "../MF/AudioBuffer.h"
#include "../Whisper/TranscribeResult.h"
#include <memory>

namespace Whisper
{
	HRESULT COMLIGHTCALL loadWhisperCppModel( const wchar_t* path, const sModelSetup& setup, const sLoadModelCallbacks* callbacks, iModel** pp );
}
