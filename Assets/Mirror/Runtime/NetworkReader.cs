// Custom NetworkReader that doesn't use C#'s built in MemoryStream in order to
// avoid allocations.
//
// Benchmark: 100kb byte[] passed to NetworkReader constructor 1000x
//   before with MemoryStream
//     0.8% CPU time, 250KB memory, 3.82ms
//   now:
//     0.0% CPU time,  32KB memory, 0.02ms
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Mirror
{
    /// <summary>
    /// a class that holds readers for the different types
    /// Note that c# creates a different static variable for each
    /// type
    /// This will be populated by the weaver
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public static class Reader<T>
    {
        public static Func<NetworkReader, T> Read { internal get; set; }
    }

    // Note: This class is intended to be extremely pedantic, and
    // throw exceptions whenever stuff is going slightly wrong.
    // The exceptions will be handled in NetworkServer/NetworkClient.
    /// <summary>
    /// Binary stream Reader. Supports simple types, buffers, arrays, structs, and nested types
    /// <para>Use <see cref="NetworkReaderPool.GetReader">NetworkReaderPool.GetReader</see> to reduce memory allocation</para>
    /// </summary>
    public class NetworkReader
    {
        // internal buffer
        // byte[] pointer would work, but we use ArraySegment to also support
        // the ArraySegment constructor
        internal ArraySegment<byte> buffer;

        // 'int' is the best type for .Position. 'short' is too small if we send >32kb which would result in negative .Position
        // -> converting long to int is fine until 2GB of data (MAX_INT), so we don't have to worry about overflows here
        public int Position;
        public int Length => buffer.Count;

        public NetworkReader(byte[] bytes)
        {
            buffer = new ArraySegment<byte>(bytes);
        }

        public NetworkReader(ArraySegment<byte> segment)
        {
            buffer = segment;
        }

        // ReadBlittable<T> from DOTSNET
        // this is extremely fast, but only works for blittable types.
        //
        // Benchmark: see NetworkWriter.WriteBlittable!
        //
        // Note:
        //   ReadBlittable assumes same endianness for server & client.
        //   All Unity 2018+ platforms are little endian.
        public unsafe T ReadBlittable<T>()
            where T : unmanaged
        {
            // check if blittable for safety
#if UNITY_EDITOR
            if (!UnsafeUtility.IsBlittable(typeof(T)))
            {
                throw new ArgumentException(typeof(T) + " is not blittable!");
            }
#endif

            // calculate size
            //   sizeof(T) gets the managed size at compile time.
            //   Marshal.SizeOf<T> gets the unmanaged size at runtime (slow).
            // => our 1mio writes benchmark is 6x slower with Marshal.SizeOf<T>
            // => for blittable types, sizeof(T) is even recommended:
            // https://docs.microsoft.com/en-us/dotnet/standard/native-interop/best-practices
            int size = sizeof(T);

            // enough data to read?
            if (Position + size > buffer.Count)
            {
                throw new EndOfStreamException($"ReadBlittable<{typeof(T)}> out of range: {ToString()}");
            }

            // read blittable
            T value;
            fixed (byte* ptr = &buffer.Array[buffer.Offset + Position])
            {
                // cast buffer to a T* pointer and then read from it.
                value = *(T*)ptr;
            }
            Position += size;
            return value;
        }

        // read bytes into the passed buffer
        public byte[] ReadBytes(byte[] bytes, int count)
        {
            // check if passed byte array is big enough
            if (count > bytes.Length)
            {
                throw new EndOfStreamException("ReadBytes can't read " + count + " + bytes because the passed byte[] only has length " + bytes.Length);
            }

            ArraySegment<byte> data = ReadBytesSegment(count);
            Array.Copy(data.Array, data.Offset, bytes, 0, count);
            return bytes;
        }

        // useful to parse payloads etc. without allocating
        public ArraySegment<byte> ReadBytesSegment(int count)
        {
            // check if within buffer limits
            if (Position + count > buffer.Count)
            {
                throw new EndOfStreamException("ReadBytesSegment can't read " + count + " bytes because it would read past the end of the stream. " + ToString());
            }

            // return the segment
            var result = new ArraySegment<byte>(buffer.Array, buffer.Offset + Position, count);
            Position += count;
            return result;
        }

        public override string ToString()
        {
            return "NetworkReader pos=" + Position + " len=" + Length + " buffer=" + BitConverter.ToString(buffer.Array, buffer.Offset, buffer.Count);
        }

        /// <summary>
        /// Reads any data type that mirror supports
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T Read<T>()
        {
            if (Reader<T>.Read == null)
                Debug.AssertFormat(
                    Reader<T>.Read != null,
                    @"No reader found for {0}. See https://mirrorng.github.io/MirrorNG/Articles/General/Troubleshooting.html for details",
                    typeof(T));

            return Reader<T>.Read(this);
        }
    }

    // Mirror's Weaver automatically detects all NetworkReader function types,
    // but they do all need to be extensions.
    public static class NetworkReaderExtensions
    {
        static readonly ILogger logger = LogFactory.GetLogger(typeof(NetworkReaderExtensions));

        // cache encoding instead of creating it each time
        // 1000 readers before:  1MB GC, 30ms
        // 1000 readers after: 0.8MB GC, 18ms
        static readonly UTF8Encoding encoding = new UTF8Encoding(false, true);

        public static byte ReadByte(this NetworkReader reader) => reader.ReadBlittable<byte>();
        public static sbyte ReadSByte(this NetworkReader reader) => reader.ReadBlittable<sbyte>();
        public static char ReadChar(this NetworkReader reader) => (char)reader.ReadBlittable<short>(); // char isn't blittable
        public static bool ReadBoolean(this NetworkReader reader) => reader.ReadBlittable<byte>() != 0; // bool isn't blittable
        public static short ReadInt16(this NetworkReader reader) => reader.ReadBlittable<short>();
        public static ushort ReadUInt16(this NetworkReader reader) => reader.ReadBlittable<ushort>();
        public static int ReadInt32(this NetworkReader reader) =>  reader.ReadBlittable<int>();
        public static uint ReadUInt32(this NetworkReader reader) => reader.ReadBlittable<uint>();
        public static long ReadInt64(this NetworkReader reader) => reader.ReadBlittable<long>();
        public static ulong ReadUInt64(this NetworkReader reader) => reader.ReadBlittable<ulong>();
        public static float ReadSingle(this NetworkReader reader) => reader.ReadBlittable<float>();
        public static double ReadDouble(this NetworkReader reader) => reader.ReadBlittable<double>();
        public static decimal ReadDecimal(this NetworkReader reader) => reader.ReadBlittable<decimal>();

        // note: this will throw an ArgumentException if an invalid utf8 string is sent
        // null support, see NetworkWriter
        public static string ReadString(this NetworkReader reader)
        {
            // read number of bytes
            ushort size = reader.ReadUInt16();

            if (size == 0)
                return null;

            int realSize = size - 1;

            // make sure it's within limits to avoid allocation attacks etc.
            if (realSize >= NetworkWriter.MaxStringLength)
            {
                throw new EndOfStreamException("ReadString too long: " + realSize + ". Limit is: " + NetworkWriter.MaxStringLength);
            }

            ArraySegment<byte> data = reader.ReadBytesSegment(realSize);

            // convert directly from buffer to string via encoding
            return encoding.GetString(data.Array, data.Offset, data.Count);
        }

        // Use checked() to force it to throw OverflowException if data is invalid
        // null support, see NetworkWriter
        public static byte[] ReadBytesAndSize(this NetworkReader reader)
        {
            // count = 0 means the array was null
            // otherwise count -1 is the length of the array
            uint count = reader.ReadUInt32();
            return count == 0 ? null : reader.ReadBytes(checked((int)(count - 1u)));
        }

        public static ArraySegment<byte> ReadBytesAndSizeSegment(this NetworkReader reader)
        {
            // count = 0 means the array was null
            // otherwise count - 1 is the length of the array
            uint count = reader.ReadUInt32();
            return count == 0 ? default : reader.ReadBytesSegment(checked((int)(count - 1u)));
        }

        public static Vector2 ReadVector2(this NetworkReader reader) => reader.ReadBlittable<Vector2>();
        public static Vector3 ReadVector3(this NetworkReader reader) => reader.ReadBlittable<Vector3>();
        public static Vector4 ReadVector4(this NetworkReader reader) => reader.ReadBlittable<Vector4>();
        public static Vector2Int ReadVector2Int(this NetworkReader reader) => reader.ReadBlittable<Vector2Int>();
        public static Vector3Int ReadVector3Int(this NetworkReader reader) => reader.ReadBlittable<Vector3Int>();
        public static Color ReadColor(this NetworkReader reader) => reader.ReadBlittable<Color>();
        public static Color32 ReadColor32(this NetworkReader reader) => reader.ReadBlittable<Color32>();
        public static Quaternion ReadQuaternion(this NetworkReader reader) => reader.ReadBlittable<Quaternion>();
        public static Rect ReadRect(this NetworkReader reader) => reader.ReadBlittable<Rect>();
        public static Plane ReadPlane(this NetworkReader reader) => reader.ReadBlittable<Plane>();
        public static Ray ReadRay(this NetworkReader reader) => reader.ReadBlittable<Ray>();
        public static Matrix4x4 ReadMatrix4x4(this NetworkReader reader) => reader.ReadBlittable<Matrix4x4>();

        public static byte[] ReadBytes(this NetworkReader reader, int count)
        {
            byte[] bytes = new byte[count];
            reader.ReadBytes(bytes, count);
            return bytes;
        }

        public static Guid ReadGuid(this NetworkReader reader) => new Guid(reader.ReadBytes(16));

        public static NetworkIdentity ReadNetworkIdentity(this NetworkReader reader)
        {
            uint netId = reader.ReadUInt32();
            if (netId == 0)
                return null;

            if (NetworkClient.Current != null)
            {
                NetworkClient.Current.Spawned.TryGetValue(netId, out NetworkIdentity identity);
                return identity;
            }
            else if (NetworkServer.Current != null)
            {
                NetworkServer.Current.Spawned.TryGetValue(netId, out NetworkIdentity identity);
                return identity;
            }

            if (logger.WarnEnabled()) logger.LogFormat(LogType.Warning, "ReadNetworkIdentity netId:{0} not found in spawned", netId);
            return null;
        }

        public static List<T> ReadList<T>(this NetworkReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0)
                return null;
            var result = new List<T>(length);
            for (int i=0; i< length; i++)
            {
                result.Add(reader.Read<T>());
            }
            return result;
        }

        public static T[] ReadArray<T>(this NetworkReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0)
                return null;
            var result = new T[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = reader.Read<T>();
            }
            return result;
        }

        public static Uri ReadUri(this NetworkReader reader)
        {
            return new Uri(reader.ReadString());
        }
    }
}
