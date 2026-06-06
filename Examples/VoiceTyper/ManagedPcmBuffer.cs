using System.Runtime.InteropServices;
using Whisper;

namespace VoiceTyper
{
	/// <summary>
	/// Managed implementation of <see cref="iAudioBuffer"/> over a block of mono 16 kHz float PCM.
	/// The samples are copied into an unmanaged buffer so the native engine can read them directly.
	/// </summary>
	sealed class ManagedPcmBuffer : iAudioBuffer
	{
		IntPtr pcm;
		readonly int samples;

		public ManagedPcmBuffer( ReadOnlySpan<float> mono16k )
		{
			samples = mono16k.Length;
			pcm = Marshal.AllocHGlobal( Math.Max( 1, samples ) * sizeof( float ) );
			unsafe
			{
				fixed( float* src = mono16k )
				{
					Buffer.MemoryCopy( src, (void*)pcm, (long)samples * sizeof( float ), (long)samples * sizeof( float ) );
				}
			}
		}

		public int countSamples() => samples;

		public IntPtr getPcmMono() => pcm;

		public IntPtr getPcmStereo() => IntPtr.Zero;

		public void getTime( out TimeSpan time ) => time = TimeSpan.Zero;

		public void Dispose()
		{
			if( pcm != IntPtr.Zero )
			{
				Marshal.FreeHGlobal( pcm );
				pcm = IntPtr.Zero;
			}
			GC.SuppressFinalize( this );
		}

		~ManagedPcmBuffer()
		{
			if( pcm != IntPtr.Zero )
				Marshal.FreeHGlobal( pcm );
		}
	}
}
