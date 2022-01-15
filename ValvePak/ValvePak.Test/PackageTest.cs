using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using SteamDatabase.ValvePak;

namespace Tests
{
    [TestFixture]
    public class PackageTest
    {
        [Test]
        public void ParseVPK()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "platform_misc_dir.vpk");

            using var package = new Package();
            package.Read(path);

            package.VerifyHashes();

            Assert.IsTrue(package.IsSignatureValid());
        }

        [Test]
        public void InvalidPackageThrows()
        {
            using var resource = new Package();
            using var ms = new MemoryStream(Enumerable.Repeat<byte>(1, 12).ToArray());

            // Should yell about not setting file name
            Assert.Throws<InvalidOperationException>(() => resource.Read(ms));

            resource.SetFileName("a.vpk");

            Assert.Throws<InvalidDataException>(() => resource.Read(ms));
        }

        [Test]
        public void CorrectHeaderWrongVersionThrows()
        {
            using var resource = new Package();
            resource.SetFileName("a.vpk");

            using var ms = new MemoryStream(new byte[] { 0x34, 0x12, 0xAA, 0x55, 0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x22 });
            Assert.Throws<InvalidDataException>(() => resource.Read(ms));
        }

        [Test]
        public void FindEntryDeep()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "platform_misc_dir.vpk");

            using var package = new Package();
            package.Read(path);

            Assert.AreEqual(0xA4115395, package.FindEntry("addons\\chess\\chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("addons\\chess\\", "chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("addons\\chess\\", "chess", "vdf")?.CRC32);

            Assert.AreEqual(0xA4115395, package.FindEntry("addons/chess\\chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("addons/chess\\", "chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("addons/chess\\", "chess", "vdf")?.CRC32);

            Assert.AreEqual(0xA4115395, package.FindEntry("addons/chess/chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("addons/chess/", "chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("addons/chess/", "chess", "vdf")?.CRC32);

            Assert.AreEqual(0xA4115395, package.FindEntry("\\addons/chess/chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("\\addons/chess/", "chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("\\addons/chess/", "chess", "vdf")?.CRC32);

            Assert.AreEqual(0xA4115395, package.FindEntry("/addons/chess/chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("/addons/chess/", "chess.vdf")?.CRC32);
            Assert.AreEqual(0xA4115395, package.FindEntry("/addons/chess/", "chess", "vdf")?.CRC32);

            Assert.IsNull(package.FindEntry("\\addons/chess/hello_github_reader.vdf"));
            Assert.IsNull(package.FindEntry("\\addons/chess/", "hello_github_reader.vdf"));
            Assert.IsNull(package.FindEntry("\\addons/chess/", "hello_github_reader", "vdf"));

            Assert.IsNull(package.FindEntry("\\addons/hello_github_reader/chess.vdf"));
            Assert.IsNull(package.FindEntry("\\addons/hello_github_reader/", "chess.vdf"));
            Assert.IsNull(package.FindEntry("\\addons/hello_github_reader/", "chess", "vdf"));

            Assert.IsNull(package.FindEntry("\\addons/", "chess/chess.vdf"));
            Assert.IsNull(package.FindEntry("\\addons/", "chess/chess", "vdf"));
            Assert.IsNull(package.FindEntry("\\addons/", "chess\\chess.vdf"));
            Assert.IsNull(package.FindEntry("\\addons/", "chess\\chess", "vdf"));
        }

        [Test]
        public void FindEntryRoot()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

            using var package = new Package();
            package.Read(path);

            Assert.AreEqual(0x9C800116, package.FindEntry("kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry(string.Empty, "kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry(string.Empty, "kitten", "jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry(" ", "kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry(" ", "kitten", "jpg")?.CRC32);

            Assert.AreEqual(0x9C800116, package.FindEntry("\\kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry("\\", "kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry("\\", "kitten", "jpg")?.CRC32);

            Assert.AreEqual(0x9C800116, package.FindEntry("/kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry("/", "kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry("/", "kitten", "jpg")?.CRC32);

            Assert.AreEqual(0x9C800116, package.FindEntry("\\/kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry("\\/\\", "kitten.jpg")?.CRC32);
            Assert.AreEqual(0x9C800116, package.FindEntry("\\\\/", "kitten", "jpg")?.CRC32);
        }

        [Test]
        public void ThrowsNullArgumentInSetFilename()
        {
            using var package = new Package();
            Assert.Throws<ArgumentNullException>(() => package.SetFileName(null));
        }

        [Test]
        public void ThrowsNullArgumentInReadStream()
        {
            using var package = new Package();
            Assert.Throws<ArgumentNullException>(() => package.Read((Stream)null));
        }

        [Test]
        public void ThrowsNullArgumentInnReadString()
        {
            using var package = new Package();
            Assert.Throws<ArgumentNullException>(() => package.Read((string)null));
        }

        [Test]
        public void ThrowsNullArgumentInFindEntry()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

            using var package = new Package();
            package.Read(path);

            Assert.Throws<ArgumentNullException>(() => package.FindEntry(null));
            Assert.Throws<ArgumentNullException>(() => package.FindEntry("", null));
            Assert.Throws<ArgumentNullException>(() => package.FindEntry(null, ""));
            Assert.Throws<ArgumentNullException>(() => package.FindEntry(null, "", ""));
            Assert.Throws<ArgumentNullException>(() => package.FindEntry("", null, ""));
            Assert.Throws<ArgumentNullException>(() => package.FindEntry("", "", null));
        }

        [Test]
        public void ThrowsNullArgumentInReadEntry()
        {
            using var package = new Package();
            Assert.Throws<ArgumentNullException>(() => package.ReadEntry(null, out var output));
        }

        [Test]
        public void FindEntrySpacesAndExtensionless()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

            using var package = new Package();
            package.Read(path);

            Assert.AreEqual(0x0BA144CC, package.FindEntry("test")?.CRC32);
            Assert.AreEqual(0x0BA144CC, package.FindEntry("\\/\\", "test")?.CRC32);
            Assert.AreEqual(0x0BA144CC, package.FindEntry("\\\\/", "test")?.CRC32);
            Assert.AreEqual(0x0BA144CC, package.FindEntry("\\\\/", "test", " ")?.CRC32);
            Assert.AreEqual(0x0BA144CC, package.FindEntry(" ", "test")?.CRC32);
            Assert.AreEqual(0x0BA144CC, package.FindEntry(" ", "test", " ")?.CRC32);

            Assert.AreEqual(0xBF108706, package.FindEntry("folder with space/test")?.CRC32);
            Assert.AreEqual(0xBF108706, package.FindEntry("folder with space", "test")?.CRC32);
            Assert.AreEqual(0xBF108706, package.FindEntry("\\folder with space", "test", " ")?.CRC32);
            Assert.AreEqual(0xBF108706, package.FindEntry("//folder with space//", "test", " ")?.CRC32);

            Assert.AreEqual(0x09321FC0, package.FindEntry("folder with space\\space_extension. txt")?.CRC32);
            Assert.AreEqual(0x09321FC0, package.FindEntry("/folder with space", "space_extension. txt")?.CRC32);
            Assert.AreEqual(0x09321FC0, package.FindEntry("folder with space/", "space_extension", " txt")?.CRC32);

            Assert.AreEqual(0x76D91432, package.FindEntry("folder with space/file name with space.txt")?.CRC32);

            Assert.AreEqual(0x15C1490F, package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.CRC32);
            Assert.AreEqual(0x32CFF012, package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.CRC32);

            Assert.IsNull(package.FindEntry("UpperCaseFolder/bad_file_forfun.txt"));
            Assert.IsNull(package.FindEntry("uppercasefolder/UpperCaseFile.txt"));
            Assert.IsNull(package.FindEntry("uppercasefolder/bad_file_forfun.TXT"));
            Assert.IsNull(package.FindEntry("uppercasefolder/bad_file_forfun.txt2"));
        }

        [Test]
        public void ThrowsOnInvalidCRC32()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

            using var package = new Package();
            package.Read(path);

            var file = package.FindEntry("UpperCaseFolder/UpperCaseFile.txt");
            Assert.AreEqual(0x32CFF012, file.CRC32);

            file.CRC32 = 0xDEADBEEF;

            Assert.Throws<InvalidDataException>(() => package.ReadEntry(file, out var entry));
            Assert.Throws<InvalidDataException>(() => package.ReadEntry(file, out var entry, true));
            Assert.DoesNotThrow(() => package.ReadEntry(file, out var entry, false));
        }

        [Test]
        public void TestGetFullPath()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

            using var package = new Package();
            package.Read(path);

            Assert.AreEqual("test", package.FindEntry("test")?.GetFullPath());
            Assert.AreEqual("folder with space/test", package.FindEntry("folder with space/test")?.GetFullPath());
            Assert.AreEqual("folder with space/space_extension. txt", package.FindEntry("folder with space\\space_extension. txt")?.GetFullPath());
            Assert.AreEqual("uppercasefolder/bad_file_forfun.txt", package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.GetFullPath());
            Assert.AreEqual("UpperCaseFolder/UpperCaseFile.txt", package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.GetFullPath());

            Assert.AreEqual("test", package.FindEntry("test")?.GetFileName());
            Assert.AreEqual("test", package.FindEntry("folder with space/test")?.GetFileName());
            Assert.AreEqual("space_extension. txt", package.FindEntry("folder with space\\space_extension. txt")?.GetFileName());
            Assert.AreEqual("bad_file_forfun.txt", package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.GetFileName());
        }

        [Test]
        public void TestPackageEntryToString()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

            using var package = new Package();
            package.Read(path);

            Assert.AreEqual("test crc=0xba144cc metadatasz=0 fnumber=0 ofs=0x00 sz=39", package.FindEntry("test")?.ToString());
            Assert.AreEqual("folder with space/test crc=0xbf108706 metadatasz=0 fnumber=0 ofs=0x52 sz=41", package.FindEntry("folder with space/test")?.ToString());
            Assert.AreEqual("folder with space/space_extension. txt crc=0x9321fc0 metadatasz=0 fnumber=0 ofs=0x7b sz=30", package.FindEntry("folder with space\\space_extension. txt")?.ToString());
            Assert.AreEqual("uppercasefolder/bad_file_forfun.txt crc=0x15c1490f metadatasz=0 fnumber=0 ofs=0xa2 sz=2", package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.ToString());
            Assert.AreEqual("UpperCaseFolder/UpperCaseFile.txt crc=0x32cff012 metadatasz=0 fnumber=0 ofs=0x27 sz=43", package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.ToString());
        }

        [Test]
        public void TestRespawnVPK()
        {
            using var resource = new Package();
            resource.SetFileName("apexlegends.vpk");

            using var ms = new MemoryStream(new byte[] { 0x34, 0x12, 0xAA, 0x55, 0x02, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 });
            Assert.Throws<NotSupportedException>(() => resource.Read(ms));
        }

        [Test]
        public void ExtractInlineVPK()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

            TestVPKExtraction(path);
        }

        [Test]
        public void ExtractDirVPK()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_dir.vpk");

            TestVPKExtraction(path);
        }

        private static void TestVPKExtraction(string path)
        {
            using var package = new Package();
            package.Read(path);

            Assert.AreEqual(2, package.Entries.Count);
            Assert.Contains("jpg", package.Entries.Keys);
            Assert.Contains("proto", package.Entries.Keys);

            var flatEntries = new Dictionary<string, PackageEntry>();

            using (var sha1 = SHA1.Create())
            {
                var data = new Dictionary<string, string>();

                foreach (var a in package.Entries)
                {
                    foreach (var b in a.Value)
                    {
                        Assert.AreEqual(a.Key, b.TypeName);

                        flatEntries.Add(b.FileName, b);

                        package.ReadEntry(b, out var entry);

                        data.Add(b.FileName + '.' + b.TypeName, BitConverter.ToString(sha1.ComputeHash(entry)).Replace("-", string.Empty));
                    }
                }

                Assert.AreEqual(3, data.Count);
                Assert.AreEqual("E0D865F19F0A4A7EA3753FBFCFC624EE8B46928A", data["kitten.jpg"]);
                Assert.AreEqual("2EFFCB09BE81E8BEE88CB7BA8C18E87D3E1168DB", data["steammessages_base.proto"]);
                Assert.AreEqual("22741F66442A4DC880725D2CC019E6C9202FD70C", data["steammessages_clientserver.proto"]);
            }

            Assert.AreEqual(flatEntries["kitten"].TotalLength, 16361);
            Assert.AreEqual(flatEntries["steammessages_base"].TotalLength, 2563);
            Assert.AreEqual(flatEntries["steammessages_clientserver"].TotalLength, 39177);
        }
    }
}
