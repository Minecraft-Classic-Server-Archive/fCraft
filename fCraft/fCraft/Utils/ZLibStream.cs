﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Reflection;


namespace fCraft {
    public sealed class ZLibStream : Stream {

        #region const, structs, and defs

        public const int BufferSize = 1024 * 1024;

        private enum ZLibReturnCode {
            Ok = 0,
            StreamEnd = 1,
            NeedDictionary = 2,
            Errno = -1,
            StreamError = -2,
            DataError = -3,
            MemoryError = -4,
            BufferError = -5,
            VersionError = -6
        }

        private enum ZLibFlush {
            NoFlush = 0,
            PartialFlush = 1,
            SyncFlush = 2,
            FullFlush = 3,
            Finish = 4
        }

        public enum ZLibCompressionLevel {
            None = 0,
            Fast = 1,
            Level2 = 2,
            Level3 = 3,
            Level4 = 4,
            Level5 = 5,
            Default = 6,
            Level7 = 7,
            Level8 = 8,
            Best = 9
        }

        private enum ZLibCompressionStrategy {
            Filtered = 1,
            HuffmanOnly = 2,
            DefaultStrategy = 0
        }

        private enum ZLibCompressionMethod {
            Deflated = 8
        }

        private enum ZLibDataType {
            Binary = 0,
            Ascii = 1,
            Unknown = 2,
        }

        private enum ZLibHeaderType {
            ZLib = 15,
            GZip = 15 + 16,
            Both = 15 + 32,
        }

        public enum ZLibMemLevel {
            Minimum = 1,
            Level2 = 2,
            Level3 = 3,
            Level4 = 4,
            Level5 = 5,
            Level6 = 6,
            Level7 = 7,
            Default = 8,
            Maximum = 9
        }

        [StructLayoutAttribute( LayoutKind.Sequential )]
        private struct z_stream {
            public IntPtr next_in;  /* next input byte */
            public uint avail_in;  /* number of bytes available at next_in */
            public uint total_in;  /* total nb of input bytes read so far */

            public IntPtr next_out; /* next output byte should be put there */
            public uint avail_out; /* remaining free space at next_out */
            public uint total_out; /* total nb of bytes output so far */

            public IntPtr msg;      /* last error message, NULL if no error */
            public IntPtr state; /* not visible by applications */

            public IntPtr zalloc;  /* used to allocate the internal state */
            public IntPtr zfree;   /* used to free the internal state */
            public IntPtr opaque;  /* private data object passed to zalloc and zfree */

            public ZLibDataType data_type;  /* best guess about the data type: ascii or binary */
            public uint adler;      /* adler32 value of the uncompressed data */
            public uint reserved;   /* reserved for future use */
        };
        #endregion


        #region Initialization

        private static string ZLibVersion; // set by static constructor (below)
        private static IntPtr VersionPointer;
        public static string GetVersion() {
            return ZLibVersion;
        }

        static ZLibPInvoke Native;

        public static bool Init() {


            try {
                Native = new ZLibPInvokeWin32();
                ZLibVersion = Native.Version();
                VersionPointer = Marshal.StringToCoTaskMemAnsi( ZLibStream.ZLibVersion );
                Test();
                Console.WriteLine( "> Using zlib32.dll (Win32)" );
                return true;
            } catch( Exception ) {
                Console.WriteLine( "> Could not load zlib32.dll (Win32), trying next." );
            }

            try {
                Native = new ZLibPInvokeWin64();
                ZLibVersion = Native.Version();
                VersionPointer = Marshal.StringToCoTaskMemAnsi( ZLibStream.ZLibVersion );
                Test();
                Console.WriteLine( "> Using zlib64.dll (Win64)" );
                return true;
            } catch( Exception ) {
                Console.WriteLine( "> Could not load zlib64.dll (Win64), trying next." );
            }

            try {
                Native = new ZLibPInvokeUnix();
                ZLibVersion = Native.Version();
                VersionPointer = Marshal.StringToCoTaskMemAnsi( ZLibStream.ZLibVersion );
                Test();
                Console.WriteLine( "> Using libz (*nix)" );
                return true;
            } catch( Exception ) {
                Console.WriteLine( "> Could not load libz (*nix). Fail." );
            }

            Native = null;
            return false;
        }

        static void Test() {
            z_stream zs = new z_stream();
            zs.zalloc = IntPtr.Zero;
            zs.zfree = IntPtr.Zero;
            zs.opaque = IntPtr.Zero;
            Native.DeflateInit2( ref zs, ZLibCompressionLevel.Default, ZLibHeaderType.GZip,
                                ZLibMemLevel.Default, ZLibCompressionStrategy.DefaultStrategy, 1 );
            Native.DeflateEnd( ref zs );
        }
        #endregion


        private Stream stream;
        private CompressionMode mode;
        private GZipStream fallback;
        private z_stream zstream = new z_stream();
        private byte[] buffer;
        private GCHandle bufferHandle;


        #region Constructors

        public static ZLibStream MakeCompressor( Stream stream, int bufferSize ) {
            return new ZLibStream( stream, CompressionMode.Compress, bufferSize, ZLibCompressionLevel.Default, ZLibMemLevel.Default );
        }

        public static ZLibStream MakeCompressor( Stream stream, int bufferSize, ZLibCompressionLevel compressionLevel, ZLibMemLevel memLevel ) {
            return new ZLibStream( stream, CompressionMode.Compress, bufferSize, compressionLevel, memLevel );
        }

        public static ZLibStream MakeDecompressor( Stream stream, int bufferSize ) {
            return new ZLibStream( stream, CompressionMode.Decompress, bufferSize, ZLibCompressionLevel.Default, ZLibMemLevel.Default );
        }

        ZLibStream( Stream stream, CompressionMode mode, int bufferSize, ZLibCompressionLevel compressionLevel, ZLibMemLevel memLevel ) {

            this.stream = stream;
            this.mode = mode;

            if( Native == null ) {
                fallback = new GZipStream( stream, mode );
                return;
            }
            buffer = new byte[bufferSize];

            this.zstream.zalloc = IntPtr.Zero;
            this.zstream.zfree = IntPtr.Zero;
            this.zstream.opaque = IntPtr.Zero;

            ZLibReturnCode ret;
            if( mode == CompressionMode.Decompress ) {
                ret = Native.InflateInit2( ref this.zstream, ZLibHeaderType.Both, ZLibVersion, Marshal.SizeOf( typeof( z_stream ) ) );
            } else {
                ret = Native.DeflateInit2( ref this.zstream, compressionLevel, ZLibHeaderType.GZip, memLevel,
                                           ZLibCompressionStrategy.DefaultStrategy, Marshal.SizeOf( typeof( z_stream ) ) );
            }

            if( ret != ZLibReturnCode.Ok ) {
                throw new ArgumentException( "Unable to init ZLib. Return code: " + ret.ToString() );
            }

            this.bufferHandle = GCHandle.Alloc( buffer, GCHandleType.Pinned );
        }

        #endregion


        #region Close / Flush / Destructor

        public override void Close() {
            if( fallback != null ) fallback.Close();
            if( mode == CompressionMode.Compress ) {
                zstream.avail_in = 0;
                WriteLoop( ZLibFlush.Finish );
            }

            this.stream.Close();
            base.Close();
        }

        public override void Flush() {
            if( fallback != null ) {
                fallback.Flush();
                return;
            }
            if( this.mode != CompressionMode.Compress ) {
                throw new NotSupportedException( "The method or operation is not implemented." );
            }
            zstream.avail_in = 0;
            WriteLoop( ZLibFlush.SyncFlush );
        }

        ~ZLibStream() {
            if( fallback != null ) return;
            bufferHandle.Free();
            if( mode == CompressionMode.Decompress ) {
                Native.InflateEnd( ref this.zstream );
            } else {
                Native.DeflateEnd( ref this.zstream );
            }
        }

        #endregion


        public override int Read( byte[] array, int offset, int count ) {
            if( this.mode != CompressionMode.Decompress )
                throw new NotSupportedException( "Can't read on a compress stream!" );

            if( fallback != null ) return fallback.Read( array, offset, count );

            bool exitLoop = false;

            byte[] tmpOutputBuffer = new byte[count];
            GCHandle tmpOutpuBufferHandle = GCHandle.Alloc( tmpOutputBuffer, GCHandleType.Pinned );

            this.zstream.next_out = tmpOutpuBufferHandle.AddrOfPinnedObject();
            this.zstream.avail_out = (uint)tmpOutputBuffer.Length;

            try {
                while( this.zstream.avail_out > 0 && exitLoop == false ) {
                    if( this.zstream.avail_in == 0 ) {
                        int readLength = this.stream.Read( buffer, 0, buffer.Length );
                        this.zstream.avail_in = (uint)readLength;
                        this.zstream.next_in = this.bufferHandle.AddrOfPinnedObject();
                    }
                    ZLibReturnCode result = Native.Inflate( ref zstream, ZLibFlush.NoFlush );
                    switch( result ) {
                        case ZLibReturnCode.StreamEnd:
                            exitLoop = true;
                            Array.Copy( tmpOutputBuffer, 0, array, offset, count - (int)this.zstream.avail_out );
                            break;
                        case ZLibReturnCode.Ok:
                            Array.Copy( tmpOutputBuffer, 0, array, offset, count - (int)this.zstream.avail_out );
                            break;
                        case ZLibReturnCode.MemoryError:
                            throw new OutOfMemoryException( "ZLib return code: " + result.ToString() );
                        default:
                            throw new Exception( "ZLib return code: " + result.ToString() );
                    }
                }

                return (count - (int)this.zstream.avail_out);
            } finally {
                tmpOutpuBufferHandle.Free();
            }
        }


        public unsafe override void Write( byte[] input, int offset, int count ) {
            if( this.mode != CompressionMode.Compress )
                throw new NotSupportedException( "Can't read on a compress stream!" );

            if( fallback != null ) {
                fallback.Write( input, offset, count );
                return;
            }

            fixed( byte* inputPtr = input ) {
                zstream.next_in = (IntPtr)(inputPtr + offset);
                zstream.avail_in = (uint)count;
                WriteLoop( ZLibFlush.NoFlush );
            }
        }


        void WriteLoop( ZLibFlush flushType ) {
            bool exitLoop = false;
            while( !exitLoop ) {
                ZLibReturnCode result;

                if( zstream.avail_in == 0 ) {
                    if( flushType != ZLibFlush.NoFlush ) {
                        zstream.next_out = bufferHandle.AddrOfPinnedObject();
                        zstream.avail_out = (uint)buffer.Length;
                        result = Native.Deflate( ref zstream, flushType );
                    } else {
                        return;
                    }
                } else {
                    zstream.next_out = bufferHandle.AddrOfPinnedObject();
                    zstream.avail_out = (uint)buffer.Length;
                    result = Native.Deflate( ref zstream, ZLibFlush.NoFlush );
                }

                switch( result ) {
                    case ZLibReturnCode.StreamEnd:
                        exitLoop = true;
                        stream.Write( buffer, 0, (int)(buffer.Length - zstream.avail_out) );
                        break;

                    case ZLibReturnCode.Ok:
                        stream.Write( buffer, 0, (int)(buffer.Length - zstream.avail_out) );
                        break;

                    case ZLibReturnCode.MemoryError:
                        throw new OutOfMemoryException( "ZLib return code: " + result.ToString() );

                    default:
                        throw new Exception( "ZLib return code: " + result.ToString() );
                }
            }
        }


        public override bool CanRead {
            get { return (this.mode == CompressionMode.Decompress); }
        }

        public override bool CanWrite {
            get { return (this.mode == CompressionMode.Compress); }
        }

        public override bool CanSeek {
            get { return (false); }
        }

        public Stream BaseStream {
            get { return (this.stream); }
        }


        #region Not yet supported
        public override long Seek( long offset, SeekOrigin origin ) {
            throw new NotSupportedException();
        }

        public override void SetLength( long value ) {
            throw new NotSupportedException();
        }

        public override long Length {
            get { throw new NotSupportedException(); }
        }

        public override long Position {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        #endregion


        #region P/Invoke

        interface ZLibPInvoke {
            ZLibReturnCode InflateInit2( ref z_stream strm, ZLibHeaderType windowBits, string version, int stream_size );
            ZLibReturnCode Inflate( ref z_stream strm, ZLibFlush flush );
            ZLibReturnCode InflateEnd( ref z_stream strm );
            ZLibReturnCode DeflateInit2( ref z_stream strm, ZLibCompressionLevel level, ZLibHeaderType windowBits,
                                                     ZLibMemLevel memLevel, ZLibCompressionStrategy strategy, int stream_size );
            ZLibReturnCode Deflate( ref z_stream strm, ZLibFlush flush );
            ZLibReturnCode DeflateEnd( ref z_stream strm );
            string Version();
        }


        class ZLibPInvokeWin32 : ZLibPInvoke {
            [DllImport( "zlib32.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflateInit2_( ref z_stream strm, ZLibHeaderType windowBits,
                                                                [MarshalAs( UnmanagedType.LPStr )] string version, int stream_size );
            public ZLibReturnCode InflateInit2( ref z_stream strm, ZLibHeaderType windowBits, string version, int stream_size ) {
                return inflateInit2_( ref strm, windowBits, version, stream_size );
            }

            [DllImport( "zlib32.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflate( ref z_stream strm, ZLibFlush flush );
            public ZLibReturnCode Inflate( ref z_stream strm, ZLibFlush flush ) {
                return inflate( ref strm, flush );
            }

            [DllImport( "zlib32.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflateEnd( ref z_stream strm );
            public ZLibReturnCode InflateEnd( ref z_stream strm ) {
                return inflateEnd( ref strm );
            }

            [DllImport( "zlib32.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflateInit2_( ref z_stream strm, ZLibCompressionLevel level, ZLibCompressionMethod method,
                                                               ZLibHeaderType windowBits, ZLibMemLevel memLevel, ZLibCompressionStrategy strategy,
                                                               IntPtr version, int stream_size );

            public ZLibReturnCode DeflateInit2( ref z_stream strm, ZLibCompressionLevel level, ZLibHeaderType windowBits,
                                                ZLibMemLevel memLevel, ZLibCompressionStrategy strategy, int stream_size ) {
                return deflateInit2_( ref strm, level, ZLibCompressionMethod.Deflated, windowBits, memLevel, strategy, ZLibStream.VersionPointer, stream_size );
            }

            [DllImport( "zlib32.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflate( ref z_stream strm, ZLibFlush flush );
            public ZLibReturnCode Deflate( ref z_stream strm, ZLibFlush flush ) {
                return deflate( ref strm, flush );
            }


            [DllImport( "zlib32.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflateEnd( ref z_stream strm );
            public ZLibReturnCode DeflateEnd( ref z_stream strm ) {
                return deflateEnd( ref strm );
            }


            [DllImport( "zlib32.dll", CharSet = CharSet.Ansi )]
            private static extern string zlibVersion();
            public string Version() {
                return zlibVersion();
            }
        }


        class ZLibPInvokeWin64 : ZLibPInvoke {
            [DllImport( "zlib64.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflateInit2_( ref z_stream strm, ZLibHeaderType windowBits,
                                                                [MarshalAs( UnmanagedType.LPStr )] string version, int stream_size );
            public ZLibReturnCode InflateInit2( ref z_stream strm, ZLibHeaderType windowBits, string version, int stream_size ) {
                return inflateInit2_( ref strm, windowBits, version, stream_size );
            }

            [DllImport( "zlib64.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflate( ref z_stream strm, ZLibFlush flush );
            public ZLibReturnCode Inflate( ref z_stream strm, ZLibFlush flush ) {
                return inflate( ref strm, flush );
            }

            [DllImport( "zlib64.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflateEnd( ref z_stream strm );
            public ZLibReturnCode InflateEnd( ref z_stream strm ) {
                return inflateEnd( ref strm );
            }

            [DllImport( "zlib64.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflateInit2_( ref z_stream strm, ZLibCompressionLevel level, ZLibCompressionMethod method,
                                                               ZLibHeaderType windowBits, ZLibMemLevel memLevel, ZLibCompressionStrategy strategy,
                                                               IntPtr version, int stream_size );

            public ZLibReturnCode DeflateInit2( ref z_stream strm, ZLibCompressionLevel level, ZLibHeaderType windowBits,
                                                ZLibMemLevel memLevel, ZLibCompressionStrategy strategy, int stream_size ) {
                return deflateInit2_( ref strm, level, ZLibCompressionMethod.Deflated, windowBits, memLevel, strategy, ZLibStream.VersionPointer, stream_size );
            }

            [DllImport( "zlib64.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflate( ref z_stream strm, ZLibFlush flush );
            public ZLibReturnCode Deflate( ref z_stream strm, ZLibFlush flush ) {
                return deflate( ref strm, flush );
            }


            [DllImport( "zlib64.dll", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflateEnd( ref z_stream strm );
            public ZLibReturnCode DeflateEnd( ref z_stream strm ) {
                return deflateEnd( ref strm );
            }


            [DllImport( "zlib64.dll", CharSet = CharSet.Ansi )]
            private static extern IntPtr zlibVersion();
            public string Version() {
                IntPtr ptr = zlibVersion();
                return Marshal.PtrToStringAnsi( ptr );
            }
        }


        class ZLibPInvokeUnix : ZLibPInvoke {
            [DllImport( "z", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflateInit2_( ref z_stream strm, ZLibHeaderType windowBits,
                                                                [MarshalAs( UnmanagedType.LPStr )]string version, int stream_size );
            public ZLibReturnCode InflateInit2( ref z_stream strm, ZLibHeaderType windowBits, string version, int stream_size ) {
                return inflateInit2_( ref strm, windowBits, version, stream_size );
            }

            [DllImport( "z", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflate( ref z_stream strm, ZLibFlush flush );
            public ZLibReturnCode Inflate( ref z_stream strm, ZLibFlush flush ) {
                return inflate( ref strm, flush );
            }

            [DllImport( "z", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode inflateEnd( ref z_stream strm );
            public ZLibReturnCode InflateEnd( ref z_stream strm ) {
                return inflateEnd( ref strm );
            }

            [DllImport( "z", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflateInit2_( ref z_stream strm, ZLibCompressionLevel level, ZLibCompressionMethod method,
                                                                ZLibHeaderType windowBits, ZLibMemLevel memLevel, ZLibCompressionStrategy strategy,
                                                                IntPtr version, int stream_size );

            public ZLibReturnCode DeflateInit2( ref z_stream strm, ZLibCompressionLevel level, ZLibHeaderType windowBits,
                                                ZLibMemLevel memLevel, ZLibCompressionStrategy strategy, int stream_size ) {
                return deflateInit2_( ref strm, level, ZLibCompressionMethod.Deflated, windowBits, memLevel, strategy, ZLibStream.VersionPointer, stream_size );
            }

            [DllImport( "z", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflate( ref z_stream strm, ZLibFlush flush );
            public ZLibReturnCode Deflate( ref z_stream strm, ZLibFlush flush ) {
                return deflate( ref strm, flush );
            }


            [DllImport( "z", CharSet = CharSet.Ansi )]
            private static extern ZLibReturnCode deflateEnd( ref z_stream strm );
            public ZLibReturnCode DeflateEnd( ref z_stream strm ) {
                return deflateEnd( ref strm );
            }


            [DllImport( "z", CharSet = CharSet.Ansi )]
            private static extern IntPtr zlibVersion();
            public string Version() {
                IntPtr ptr = zlibVersion();
                return Marshal.PtrToStringAnsi( ptr );
            }
        }

        #endregion

    }
}