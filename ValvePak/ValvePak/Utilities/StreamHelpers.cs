using System.IO;
using System.Text;

namespace SteamDatabase.ValvePak
{
    internal static class StreamHelpers
    {
        /// <summary>
        /// Reads a null terminated string.
        /// </summary>
        /// <returns>String.</returns>
        /// <param name="stream">Stream.</param>
        /// <param name="encoding">Encoding.</param>
        public static string ReadNullTermString(this BinaryReader stream, Encoding encoding)
        {
            var characterSize = encoding.GetByteCount("e");

            using (var ms = new MemoryStream())
            {
                while (true)
                {
                    var data = new byte[characterSize];
                    stream.Read(data, 0, characterSize);

                    if (encoding.GetString(data, 0, characterSize) == "\0")
                    {
                        break;
                    }

                    ms.Write(data, 0, data.Length);
                }

                return encoding.GetString(ms.ToArray());
            }
        }
    }
}
