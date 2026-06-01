#include "../stdafx.h"
#include "WhisperCppBackend.h"
#include "../../third_party/whisper.cpp/include/whisper.h"
#include "../API/iMediaFoundation.cl.h"
#include "../MF/PcmReader.h"
#include "../Whisper/audioConstants.h"
#include "../Whisper/voiceActivityDetection.h"
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <filesystem>
#include <mutex>
#include <stdio.h>

namespace
{
	using namespace Whisper;

	struct WhisperApi
	{
		HMODULE dll = nullptr;

		decltype( &whisper_context_default_params ) context_default_params = nullptr;
		decltype( &whisper_init_from_file_with_params ) init_from_file_with_params = nullptr;
		decltype( &whisper_free ) free_context = nullptr;
		decltype( &whisper_init_state ) init_state = nullptr;
		decltype( &whisper_free_state ) free_state = nullptr;
		decltype( &whisper_full_default_params ) full_default_params = nullptr;
		decltype( &whisper_full_with_state ) full_with_state = nullptr;
		decltype( &whisper_full_n_segments_from_state ) full_n_segments_from_state = nullptr;
		decltype( &whisper_full_get_segment_t0_from_state ) full_get_segment_t0_from_state = nullptr;
		decltype( &whisper_full_get_segment_t1_from_state ) full_get_segment_t1_from_state = nullptr;
		decltype( &whisper_full_get_segment_text_from_state ) full_get_segment_text_from_state = nullptr;
		decltype( &whisper_full_n_tokens_from_state ) full_n_tokens_from_state = nullptr;
		decltype( &whisper_full_get_token_text_from_state ) full_get_token_text_from_state = nullptr;
		decltype( &whisper_full_get_token_data_from_state ) full_get_token_data_from_state = nullptr;
		decltype( &whisper_tokenize ) tokenize = nullptr;
		decltype( &whisper_is_multilingual ) is_multilingual = nullptr;
		decltype( &whisper_token_eot ) token_eot = nullptr;
		decltype( &whisper_token_sot ) token_sot = nullptr;
		decltype( &whisper_token_prev ) token_prev = nullptr;
		decltype( &whisper_token_solm ) token_solm = nullptr;
		decltype( &whisper_token_not ) token_not = nullptr;
		decltype( &whisper_token_beg ) token_beg = nullptr;
		decltype( &whisper_token_translate ) token_translate = nullptr;
		decltype( &whisper_token_transcribe ) token_transcribe = nullptr;
		decltype( &whisper_token_to_str ) token_to_str = nullptr;
		decltype( &whisper_print_timings ) print_timings = nullptr;
		decltype( &whisper_reset_timings ) reset_timings = nullptr;
		decltype( &whisper_print_system_info ) print_system_info = nullptr;

		~WhisperApi()
		{
			if( dll )
				FreeLibrary( dll );
		}

		template<class T>
		HRESULT loadProc( T& dst, const char* name )
		{
			dst = reinterpret_cast<T>( GetProcAddress( dll, name ) );
			if( dst )
				return S_OK;
			logError( u8"Unable to find whisper.cpp export '%s'", name );
			return HRESULT_FROM_WIN32( ERROR_PROC_NOT_FOUND );
		}

		HRESULT load()
		{
			if( dll )
				return S_OK;

			dll = LoadLibraryW( L"whisper.dll" );
			if( !dll )
			{
				logError( u8"Unable to load whisper.dll. Build whisper.cpp with CMake and copy whisper.dll and ggml*.dll next to the application." );
				return HRESULT_FROM_WIN32( GetLastError() );
			}

			CHECK( loadProc( context_default_params, "whisper_context_default_params" ) );
			CHECK( loadProc( init_from_file_with_params, "whisper_init_from_file_with_params" ) );
			CHECK( loadProc( free_context, "whisper_free" ) );
			CHECK( loadProc( init_state, "whisper_init_state" ) );
			CHECK( loadProc( free_state, "whisper_free_state" ) );
			CHECK( loadProc( full_default_params, "whisper_full_default_params" ) );
			CHECK( loadProc( full_with_state, "whisper_full_with_state" ) );
			CHECK( loadProc( full_n_segments_from_state, "whisper_full_n_segments_from_state" ) );
			CHECK( loadProc( full_get_segment_t0_from_state, "whisper_full_get_segment_t0_from_state" ) );
			CHECK( loadProc( full_get_segment_t1_from_state, "whisper_full_get_segment_t1_from_state" ) );
			CHECK( loadProc( full_get_segment_text_from_state, "whisper_full_get_segment_text_from_state" ) );
			CHECK( loadProc( full_n_tokens_from_state, "whisper_full_n_tokens_from_state" ) );
			CHECK( loadProc( full_get_token_text_from_state, "whisper_full_get_token_text_from_state" ) );
			CHECK( loadProc( full_get_token_data_from_state, "whisper_full_get_token_data_from_state" ) );
			CHECK( loadProc( tokenize, "whisper_tokenize" ) );
			CHECK( loadProc( is_multilingual, "whisper_is_multilingual" ) );
			CHECK( loadProc( token_eot, "whisper_token_eot" ) );
			CHECK( loadProc( token_sot, "whisper_token_sot" ) );
			CHECK( loadProc( token_prev, "whisper_token_prev" ) );
			CHECK( loadProc( token_solm, "whisper_token_solm" ) );
			CHECK( loadProc( token_not, "whisper_token_not" ) );
			CHECK( loadProc( token_beg, "whisper_token_beg" ) );
			CHECK( loadProc( token_translate, "whisper_token_translate" ) );
			CHECK( loadProc( token_transcribe, "whisper_token_transcribe" ) );
			CHECK( loadProc( token_to_str, "whisper_token_to_str" ) );
			CHECK( loadProc( print_timings, "whisper_print_timings" ) );
			CHECK( loadProc( reset_timings, "whisper_reset_timings" ) );
			CHECK( loadProc( print_system_info, "whisper_print_system_info" ) );

			logInfo( u8"Loaded whisper.cpp backend: %s", print_system_info() );
			return S_OK;
		}
	};

	std::shared_ptr<WhisperApi> loadApi()
	{
		static std::mutex mtx;
		static std::weak_ptr<WhisperApi> weak;
		std::lock_guard lock{ mtx };
		std::shared_ptr<WhisperApi> api = weak.lock();
		if( api )
			return api;
		api = std::make_shared<WhisperApi>();
		weak = api;
		return api;
	}

	std::string utf8( const wchar_t* text )
	{
		if( !text )
			return {};
		const int len = WideCharToMultiByte( CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr );
		if( len <= 1 )
			return {};
		std::string result( len - 1, '\0' );
		WideCharToMultiByte( CP_UTF8, 0, text, -1, result.data(), len, nullptr, nullptr );
		return result;
	}

	void languageFromKey( uint32_t key, char ( &dst )[ 5 ] )
	{
		memcpy( dst, &key, 4 );
		dst[ 4 ] = '\0';
		if( dst[ 0 ] == '\0' )
			strcpy_s( dst, "auto" );
	}

	int64_t whisperTimeToTicks( int64_t t )
	{
		return MFllMulDiv( t, 10'000'000, 100, 0 );
	}

	class RuntimeAudioBuffer : public ComLight::ObjectRoot<iAudioBuffer>
	{
		uint32_t COMLIGHTCALL countSamples() const override final
		{
			return (uint32_t)pcm.mono.size();
		}
		const float* COMLIGHTCALL getPcmMono() const override final
		{
			return pcm.mono.empty() ? nullptr : pcm.mono.data();
		}
		const float* COMLIGHTCALL getPcmStereo() const override final
		{
			return pcm.stereo.empty() ? nullptr : pcm.stereo.data();
		}
		HRESULT COMLIGHTCALL getTime( int64_t& rdi ) const override final
		{
			rdi = MFllMulDiv( sampleOffset, 10'000'000, SAMPLE_RATE, 0 );
			return S_OK;
		}

	public:
		AudioBuffer pcm;
		int64_t sampleOffset = 0;
	};

	class RuntimeAudioBufferObj : public ComLight::Object<RuntimeAudioBuffer>
	{
		uint32_t COMLIGHTCALL Release() override final
		{
			return RefCounter::implRelease();
		}
	};

	class WhisperCppModel;

	class WhisperCppContext : public ComLight::ObjectRoot<iContext>
	{
		std::shared_ptr<WhisperApi> api;
		whisper_context* ctx = nullptr;
		whisper_state* state = nullptr;
		ComLight::CComPtr<iModel> modelPtr;
		mutable TranscribeResultStatic results;
		mutable std::vector<std::string> tokenText;
		int64_t mediaTimeOffset = 0;

		HRESULT COMLIGHTCALL getModel( iModel** pp ) override final
		{
			if( !pp )
				return E_POINTER;
			*pp = modelPtr;
			( *pp )->AddRef();
			return S_OK;
		}

		HRESULT fillParams( const sFullParams& src, whisper_full_params& dst, char ( &language )[ 5 ] ) const
		{
			dst = api->full_default_params( (whisper_sampling_strategy)src.strategy );
			dst.n_threads = src.cpuThreads;
			dst.n_max_text_ctx = src.n_max_text_ctx;
			dst.offset_ms = src.offset_ms;
			dst.duration_ms = src.duration_ms;
			dst.translate = src.flag( eFullParamsFlags::Translate );
			dst.no_context = src.flag( eFullParamsFlags::NoContext );
			dst.single_segment = src.flag( eFullParamsFlags::SingleSegment );
			dst.print_special = src.flag( eFullParamsFlags::PrintSpecial );
			dst.print_progress = src.flag( eFullParamsFlags::PrintProgress );
			dst.print_realtime = src.flag( eFullParamsFlags::PrintRealtime );
			dst.print_timestamps = src.flag( eFullParamsFlags::PrintTimestamps );
			dst.no_timestamps = false;
			dst.token_timestamps = src.flag( eFullParamsFlags::TokenTimestamps );
			dst.thold_pt = src.thold_pt;
			dst.thold_ptsum = src.thold_ptsum;
			dst.max_len = src.max_len;
			dst.max_tokens = src.max_tokens;
			dst.audio_ctx = src.audio_ctx;
			dst.prompt_tokens = src.prompt_tokens;
			dst.prompt_n_tokens = src.prompt_n_tokens;
			languageFromKey( src.language, language );
			dst.language = language;
			dst.detect_language = 0 == _stricmp( language, "auto" );
			if( src.strategy == eSamplingStrategy::BeamSearch )
				dst.beam_search.beam_size = src.beam_search.beam_width > 0 ? src.beam_search.beam_width : 5;
			return S_OK;
		}

		HRESULT copyResults( eResultFlags flags ) const
		{
			results.segments.clear();
			results.tokens.clear();
			results.segmentsText.clear();
			tokenText.clear();

			const bool includeTokens = flags & eResultFlags::Tokens;
			const int nSegments = api->full_n_segments_from_state( state );
			if( nSegments < 0 )
				return E_FAIL;

			results.segments.resize( nSegments );
			for( int i = 0; i < nSegments; i++ )
			{
				const char* text = api->full_get_segment_text_from_state( state, i );
				results.segmentsText.emplace_back( text ? text : "" );

				sSegment& segment = results.segments[ i ];
				segment.text = results.segmentsText.back().c_str();
				segment.time.begin = mediaTimeOffset + whisperTimeToTicks( api->full_get_segment_t0_from_state( state, i ) );
				segment.time.end = mediaTimeOffset + whisperTimeToTicks( api->full_get_segment_t1_from_state( state, i ) );
				segment.firstToken = (uint32_t)results.tokens.size();
				segment.countTokens = 0;

				if( !includeTokens )
					continue;

				const int nTokens = api->full_n_tokens_from_state( state, i );
				segment.countTokens = (uint32_t)nTokens;
				for( int j = 0; j < nTokens; j++ )
				{
					const whisper_token_data data = api->full_get_token_data_from_state( state, i, j );
				const char* tt = api->full_get_token_text_from_state( ctx, state, i, j );
				tokenText.emplace_back( tt ? tt : "" );
					sToken token{};
					token.text = tokenText.back().c_str();
					token.time.begin = mediaTimeOffset + whisperTimeToTicks( data.t0 );
					token.time.end = mediaTimeOffset + whisperTimeToTicks( data.t1 );
					token.probability = data.p;
					token.probabilityTimestamp = data.pt;
					token.ptsum = data.ptsum;
					token.vlen = data.vlen;
					token.id = data.id;
					token.flags = data.id >= api->token_eot( ctx ) ? eTokenFlags::Special : eTokenFlags::None;
					results.tokens.push_back( token );
				}
			}
			return S_OK;
		}

		HRESULT COMLIGHTCALL runFull( const sFullParams& params, const iAudioBuffer* buffer ) override final
		{
			if( !buffer )
				return E_POINTER;

			int64_t time = 0;
			CHECK( buffer->getTime( time ) );
			mediaTimeOffset = time;

			char language[ 5 ];
			whisper_full_params wparams;
			CHECK( fillParams( params, wparams, language ) );

			const uint32_t count = buffer->countSamples();
			const float* pcm = buffer->getPcmMono();
			if( count && !pcm )
				return E_POINTER;

			const int rc = api->full_with_state( ctx, state, wparams, pcm, (int)count );
			if( rc != 0 )
			{
				logError( u8"whisper_full_with_state failed with code %i", rc );
				return E_FAIL;
			}
			return copyResults( eResultFlags::Tokens | eResultFlags::Timestamps );
		}

		HRESULT COMLIGHTCALL runStreamed( const sFullParams& params, const sProgressSink& progress, const iAudioReader* reader ) override final
		{
			if( !reader )
				return E_POINTER;

			PcmReader pcmReader{ reader };
			RuntimeAudioBufferObj buffer;
			PcmMonoChunk mono;
			PcmStereoChunk stereo;
			const bool wantStereo = pcmReader.outputsStereo();
			const size_t chunks = pcmReader.getLength();

			for( size_t i = 0; i < chunks; i++ )
			{
				CHECK( pcmReader.readChunk( mono, wantStereo ? &stereo : nullptr ) );
				buffer.pcm.mono.insert( buffer.pcm.mono.end(), mono.mono.begin(), mono.mono.end() );
				if( wantStereo )
					buffer.pcm.stereo.insert( buffer.pcm.stereo.end(), stereo.stereo.begin(), stereo.stereo.end() );
				if( progress.pfn )
				{
					const HRESULT hr = progress.pfn( chunks ? (double)( i + 1 ) / (double)chunks : 1.0, this, progress.pv );
					CHECK( hr );
				}
			}
			return runFull( params, &buffer );
		}

		HRESULT COMLIGHTCALL runCapture( const sFullParams& params, const sCaptureCallbacks& callbacks, const iAudioCapture* capture ) override final;

		HRESULT COMLIGHTCALL getResults( eResultFlags flags, iTranscribeResult** pp ) const noexcept override final
		{
			if( !pp )
				return E_POINTER;
			HRESULT hr = copyResults( flags );
			if( FAILED( hr ) )
				return hr;
			*pp = &results;
			( *pp )->AddRef();
			return S_OK;
		}

		HRESULT COMLIGHTCALL detectSpeaker( const sTimeInterval&, eSpeakerChannel& result ) const noexcept override final
		{
			result = eSpeakerChannel::NoStereoData;
			return S_FALSE;
		}

		HRESULT COMLIGHTCALL fullDefaultParams( eSamplingStrategy strategy, sFullParams* rdi ) override final
		{
			if( !rdi )
				return E_POINTER;
			memset( rdi, 0, sizeof( *rdi ) );
			const whisper_full_params src = api->full_default_params( (whisper_sampling_strategy)strategy );
			rdi->strategy = strategy;
			rdi->cpuThreads = src.n_threads;
			rdi->n_max_text_ctx = src.n_max_text_ctx;
			rdi->offset_ms = src.offset_ms;
			rdi->duration_ms = src.duration_ms;
			rdi->language = makeLanguageKey( src.language ? src.language : "auto" );
			rdi->thold_pt = src.thold_pt;
			rdi->thold_ptsum = src.thold_ptsum;
			rdi->max_len = src.max_len;
			rdi->max_tokens = src.max_tokens;
			rdi->audio_ctx = src.audio_ctx;
			if( src.translate ) rdi->flags |= eFullParamsFlags::Translate;
			if( src.no_context ) rdi->flags |= eFullParamsFlags::NoContext;
			if( src.single_segment ) rdi->flags |= eFullParamsFlags::SingleSegment;
			if( src.print_special ) rdi->flags |= eFullParamsFlags::PrintSpecial;
			if( src.print_progress ) rdi->flags |= eFullParamsFlags::PrintProgress;
			if( src.print_realtime ) rdi->flags |= eFullParamsFlags::PrintRealtime;
			if( src.print_timestamps ) rdi->flags |= eFullParamsFlags::PrintTimestamps;
			if( src.token_timestamps ) rdi->flags |= eFullParamsFlags::TokenTimestamps;
			rdi->beam_search.beam_width = src.beam_search.beam_size;
			return S_OK;
		}

		HRESULT COMLIGHTCALL timingsPrint() override final
		{
			api->print_timings( ctx );
			return S_OK;
		}
		HRESULT COMLIGHTCALL timingsReset() override final
		{
			api->reset_timings( ctx );
			return S_OK;
		}

	public:
		WhisperCppContext( std::shared_ptr<WhisperApi> api, whisper_context* ctx, iModel* model ) :
			api( std::move( api ) ),
			ctx( ctx ),
			modelPtr( model )
		{ }

		HRESULT RuntimeClassInitialize()
		{
			state = api->init_state( ctx );
			return state ? S_OK : E_OUTOFMEMORY;
		}

		void FinalRelease()
		{
			if( state )
			{
				api->free_state( state );
				state = nullptr;
			}
		}
	};

	class WhisperCppModel : public ComLight::ObjectRoot<iModel>
	{
		std::shared_ptr<WhisperApi> api;
		whisper_context* ctx = nullptr;
		std::string path;

		HRESULT COMLIGHTCALL createContext( iContext** pp ) override final
		{
			ComLight::CComPtr<ComLight::Object<WhisperCppContext>> obj;
			iModel* self = this;
			CHECK( ComLight::Object<WhisperCppContext>::create( obj, api, ctx, self ) );
			CHECK( obj->RuntimeClassInitialize() );
			obj.detach( pp );
			return S_OK;
		}

		HRESULT COMLIGHTCALL tokenize( const char* text, pfnDecodedTokens pfn, void* pv ) override final
		{
			if( !pfn )
				return E_POINTER;
			if( !text )
				text = "";
			const int needed = -api->tokenize( ctx, text, nullptr, 0 );
			if( needed <= 0 )
			{
				pfn( nullptr, 0, pv );
				return S_OK;
			}
			std::vector<whisper_token> tokens( needed );
			const int actual = api->tokenize( ctx, text, tokens.data(), needed );
			if( actual < 0 )
				return E_FAIL;
			pfn( tokens.data(), actual, pv );
			return S_OK;
		}

		HRESULT COMLIGHTCALL isMultilingual() override final
		{
			return api->is_multilingual( ctx ) ? S_OK : S_FALSE;
		}

		HRESULT COMLIGHTCALL getSpecialTokens( SpecialTokens& rdi ) override final
		{
			rdi.TranscriptionEnd = api->token_eot( ctx );
			rdi.TranscriptionStart = api->token_sot( ctx );
			rdi.PreviousWord = api->token_prev( ctx );
			rdi.SentenceStart = api->token_solm( ctx );
			rdi.Not = api->token_not( ctx );
			rdi.TranscriptionBegin = api->token_beg( ctx );
			rdi.TaskTranslate = api->token_translate( ctx );
			rdi.TaskTranscribe = api->token_transcribe( ctx );
			return S_OK;
		}

		const char* COMLIGHTCALL stringFromToken( whisper_token token ) override final
		{
			return api->token_to_str( ctx, token );
		}

		HRESULT COMLIGHTCALL clone( iModel** rdi ) override final
		{
			const std::wstring wide = std::filesystem::path( path ).wstring();
			return Whisper::loadWhisperCppModel( wide.c_str(), sModelSetup{}, nullptr, rdi );
		}

	public:
		WhisperCppModel( std::shared_ptr<WhisperApi> api, std::string path ) :
			api( std::move( api ) ),
			path( std::move( path ) )
		{ }

		HRESULT RuntimeClassInitialize()
		{
			CHECK( api->load() );

			whisper_context_params params = api->context_default_params();
			params.use_gpu = true;
			params.flash_attn = true;
			params.gpu_device = 0;

			ctx = api->init_from_file_with_params( path.c_str(), params );
			if( ctx )
				return S_OK;

			logWarning( u8"CUDA/GPU initialization failed for whisper.cpp backend. Falling back to CPU." );
			params.use_gpu = false;
			params.flash_attn = false;
			ctx = api->init_from_file_with_params( path.c_str(), params );
			return ctx ? S_OK : E_FAIL;
		}

		void FinalRelease()
		{
			if( ctx )
			{
				api->free_context( ctx );
				ctx = nullptr;
			}
		}
	};

	HRESULT WhisperCppContext::runCapture( const sFullParams& params, const sCaptureCallbacks& callbacks, const iAudioCapture* capture )
	{
		if( !capture )
			return E_POINTER;

		CComPtr<IMFSourceReader> reader;
		CHECK( capture->getReader( &reader ) );
		CHECK( reader->SetStreamSelection( MF_SOURCE_READER_ALL_STREAMS, FALSE ) );
		CHECK( reader->SetStreamSelection( MF_SOURCE_READER_FIRST_AUDIO_STREAM, TRUE ) );

		CComPtr<IMFMediaType> nativeType;
		CHECK( reader->GetNativeMediaType( MF_SOURCE_READER_FIRST_AUDIO_STREAM, MF_SOURCE_READER_CURRENT_TYPE_INDEX, &nativeType ) );
		UINT32 channels = 0;
		CHECK( nativeType->GetUINT32( MF_MT_AUDIO_NUM_CHANNELS, &channels ) );
		const bool sourceMono = channels < 2;
		const bool wantStereo = 0 != ( capture->getParams().flags & (uint32_t)eCaptureFlags::Stereo );

		CComPtr<IMFMediaType> mt;
		CHECK( MFCreateMediaType( &mt ) );
		CHECK( mt->SetGUID( MF_MT_MAJOR_TYPE, MFMediaType_Audio ) );
		CHECK( mt->SetGUID( MF_MT_SUBTYPE, MFAudioFormat_Float ) );
		CHECK( mt->SetUINT32( MF_MT_AUDIO_SAMPLES_PER_SECOND, SAMPLE_RATE ) );
		CHECK( mt->SetUINT32( MF_MT_AUDIO_NUM_CHANNELS, sourceMono ? 1 : 2 ) );
		CHECK( mt->SetUINT32( MF_MT_AUDIO_BITS_PER_SAMPLE, 32 ) );
		CHECK( reader->SetCurrentMediaType( MF_SOURCE_READER_FIRST_AUDIO_STREAM, nullptr, mt ) );

		AudioBuffer pcm;
		auto append = AudioBuffer::appendSamplesFunc( sourceMono, wantStereo );
		VAD vad;
		RuntimeAudioBufferObj buffer;
		int64_t sampleOffset = 0;

		const sCaptureParams& cp = capture->getParams();
		const size_t minSamples = (size_t)( cp.minDuration * SAMPLE_RATE );
		const size_t maxSamples = (size_t)( cp.maxDuration * SAMPLE_RATE );

		if( callbacks.captureStatus )
			CHECK( callbacks.captureStatus( callbacks.pv, eCaptureStatus::Listening ) );

		while( true )
		{
			if( callbacks.shouldCancel )
			{
				HRESULT hr = callbacks.shouldCancel( callbacks.pv );
				CHECK( hr );
				if( hr != S_OK )
					return S_OK;
			}

			DWORD flags = 0;
			CComPtr<IMFSample> sample;
			CHECK( reader->ReadSample( MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, nullptr, &flags, nullptr, &sample ) );
			if( flags & MF_SOURCE_READERF_ENDOFSTREAM )
				return S_OK;
			if( !sample )
				continue;

			CComPtr<IMFMediaBuffer> mediaBuffer;
			CHECK( sample->ConvertToContiguousBuffer( &mediaBuffer ) );
			float* data = nullptr;
			DWORD cb = 0;
			CHECK( mediaBuffer->Lock( (BYTE**)&data, nullptr, &cb ) );
			try
			{
				( pcm.*append )( data, cb / sizeof( float ) );
			}
			catch( const std::bad_alloc& )
			{
				mediaBuffer->Unlock();
				return E_OUTOFMEMORY;
			}
			CHECK( mediaBuffer->Unlock() );

			const size_t voiceFrame = vad.detect( pcm.mono.data(), pcm.mono.size() );
			if( voiceFrame )
			{
				if( callbacks.captureStatus )
					CHECK( callbacks.captureStatus( callbacks.pv, eCaptureStatus::Voice ) );
				if( pcm.mono.size() < maxSamples )
					continue;
			}
			else if( pcm.mono.size() < minSamples )
				continue;

			if( pcm.mono.size() < minSamples )
				continue;

			if( callbacks.captureStatus )
				CHECK( callbacks.captureStatus( callbacks.pv, eCaptureStatus::Transcribing ) );

			buffer.sampleOffset = sampleOffset;
			pcm.swap( buffer.pcm );
			CHECK( runFull( params, &buffer ) );
			sampleOffset += buffer.pcm.mono.size();
			buffer.pcm.clear();
			vad.clear();
			if( callbacks.captureStatus )
				CHECK( callbacks.captureStatus( callbacks.pv, eCaptureStatus::Listening ) );
		}
	}
}

HRESULT COMLIGHTCALL Whisper::loadWhisperCppModel( const wchar_t* path, const sModelSetup&, const sLoadModelCallbacks*, iModel** pp )
{
	if( !path || !pp )
		return E_POINTER;

	auto api = loadApi();
	const std::string modelPath = utf8( path );
	ComLight::CComPtr<ComLight::Object<WhisperCppModel>> obj;
	CHECK( ComLight::Object<WhisperCppModel>::create( obj, api, modelPath ) );
	CHECK( obj->RuntimeClassInitialize() );
	obj.detach( pp );
	logInfo16( L"Loaded model \"%s\" through whisper.cpp backend", path );
	return S_OK;
}
