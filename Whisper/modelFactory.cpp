#include "stdafx.h"
#include "modelFactory.h"
#include "API/iContext.cl.h"

namespace Whisper
{
	HRESULT __stdcall loadGpuModel( const wchar_t* path, const sModelSetup& setup, const sLoadModelCallbacks* callbacks, iModel** pp );
}

HRESULT COMLIGHTCALL Whisper::loadModel( const wchar_t* path, const sModelSetup& setup, const sLoadModelCallbacks* callbacks, iModel** pp )
{
	return loadGpuModel( path, setup, callbacks, pp );
}
