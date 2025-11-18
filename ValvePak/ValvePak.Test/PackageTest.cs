using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

			Assert.That(package.IsSignatureValid(), Is.True);
		}

		[Test]
		public void TestOriginalFileNameNotEndingInVpk()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "vpk_file_not_ending_in_vpk.vpk.0123456789abc");

			using var package = new Package();
			package.Read(path);

			Assert.That(package.FindEntry("kitten.jpg")?.CRC32, Is.EqualTo(0x9C800116));
		}

		[Test]
		public void ThrowsOnInvalidPackage()
		{
			using var resource = new Package();
			using var ms = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);

			// Should yell about not setting file name
			Assert.Throws<InvalidOperationException>(() => resource.Read(ms));

			resource.SetFileName("a.vpk");

			Assert.Throws<InvalidDataException>(() => resource.Read(ms));
		}

		[Test]
		public void ThrowsOnCorrectHeaderWrongVersion()
		{
			using var resource = new Package();
			resource.SetFileName("a.vpk");

			using var ms = new MemoryStream([0x34, 0x12, 0xAA, 0x55, 0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x22]);
			Assert.Throws<InvalidDataException>(() => resource.Read(ms));
		}

		[Test]
		public void FindEntryDeep()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.FindEntry("addons\\chess\\chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("addons/chess\\chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("addons/chess/chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("\\addons/chess/chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("/addons/chess/chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("\\addons/chess/hello_github_reader.vdf"), Is.Null);
				Assert.That(package.FindEntry("\\addons/hello_github_reader/chess.vdf"), Is.Null);
				Assert.That(package.FindEntry(string.Empty), Is.Null);
				Assert.That(package.FindEntry(" "), Is.Null);
			}
		}

		[Test]
		public void TestBinarySearch()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.OptimizeEntriesForBinarySearch();
			package.Read(path);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.FindEntry("addons\\chess\\chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("addons/chess\\chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("addons/chess/chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("\\addons/chess/chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("/addons/chess/chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("\\addons/chess/hello_github_reader.vdf"), Is.Null);
				Assert.That(package.FindEntry("\\addons/hello_github_reader/chess.vdf"), Is.Null);
				Assert.That(package.FindEntry(string.Empty), Is.Null);
				Assert.That(package.FindEntry(" "), Is.Null);
			}

			foreach (var extension in package.Entries!.Values)
			{
				foreach (var entry in extension)
				{
					Assert.That(package.FindEntry(entry.GetFullPath()), Is.EqualTo(entry));
				}
			}
		}

		[Test]
		public void TestBinarySearchCaseInsensitive()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
			package.Read(path);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.FindEntry("ADDONS\\chess\\chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("addons/CHESS\\chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("addons/chess/CHESS.vdf")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("\\addons/chess/chess.VDF")?.CRC32, Is.EqualTo(0xA4115395));
				Assert.That(package.FindEntry("/addons/CHESS/chess.vdf")?.CRC32, Is.EqualTo(0xA4115395));

				Assert.That(package.FindEntry("\\addons/CHESS/hello_github_reader.vdf"), Is.Null);
				Assert.That(package.FindEntry("\\addons/hello_github_reader/CHESS.vdf"), Is.Null);
			}

			foreach (var extension in package.Entries!.Values)
			{
				foreach (var entry in extension)
				{
					Assert.That(package.FindEntry(entry.GetFullPath()), Is.EqualTo(entry));
				}
			}
		}

		[Test]
		public void FindEntryRoot()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.FindEntry("kitten.jpg")?.CRC32, Is.EqualTo(0x9C800116));
				Assert.That(package.FindEntry("\\kitten.jpg")?.CRC32, Is.EqualTo(0x9C800116));
				Assert.That(package.FindEntry("/kitten.jpg")?.CRC32, Is.EqualTo(0x9C800116));
				Assert.That(package.FindEntry("\\/kitten.jpg")?.CRC32, Is.EqualTo(0x9C800116));
			}
		}

		[Test]
		public void ThrowsNullArgumentInSetFilename()
		{
			using var package = new Package();
			Assert.Throws<ArgumentNullException>(() => package.SetFileName(null!));
		}

		[Test]
		public void ThrowsNullArgumentInReadStream()
		{
			using var package = new Package();
			Assert.Throws<ArgumentNullException>(() => package.Read((Stream)null!));
		}

		[Test]
		public void ThrowsNullArgumentInnReadString()
		{
			using var package = new Package();
			Assert.Throws<ArgumentNullException>(() => package.Read((string)null!));
		}

		[Test]
		public void ThrowsNullArgumentInFindEntry()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.Throws<ArgumentNullException>(() => package.FindEntry(null!));
		}

		[Test]
		public void ThrowsNullArgumentInReadEntry()
		{
			using var package = new Package();
			Assert.Throws<ArgumentNullException>(() => package.ReadEntry(null!, out var output));
		}

		[Test]
		public void FindEntrySpacesAndExtensionless()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.FindEntry("test")?.CRC32, Is.EqualTo(0x0BA144CC));
				Assert.That(package.FindEntry("folder with space/test")?.CRC32, Is.EqualTo(0xBF108706));
				Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.CRC32, Is.EqualTo(0x09321FC0));
				Assert.That(package.FindEntry("folder with space/file name with space.txt")?.CRC32, Is.EqualTo(0x76D91432));
				Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.CRC32, Is.EqualTo(0x15C1490F));
				Assert.That(package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.CRC32, Is.EqualTo(0x32CFF012));
				Assert.That(package.FindEntry("UpperCaseFolder/bad_file_forfun.txt"), Is.Null);
				Assert.That(package.FindEntry("uppercasefolder/UpperCaseFile.txt"), Is.Null);
				Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.TXT"), Is.Null);
				Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt2"), Is.Null);
			}
		}

		[Test]
		public void ThrowsOnInvalidCRC32()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			var file = package.FindEntry("UpperCaseFolder/UpperCaseFile.txt");
			Assert.That(file, Is.Not.Null);
			Assert.That(file.CRC32, Is.EqualTo(0x32CFF012));

			file.CRC32 = 0xDEADBEEF;

			Assert.Throws<InvalidDataException>(() => package.ReadEntry(file, out _));
			var ex = Assert.Throws<InvalidDataException>(() => package.ReadEntry(file, out _, true));
			Assert.That(ex.Message, Is.EqualTo("CRC32 mismatch for read data (expected DEADBEEF, got 32CFF012)."));
			Assert.DoesNotThrow(() => package.ReadEntry(file, out _, false));
		}

		[Test]
		public void ThrowsOnInvalidEntryTerminator()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "invalid_terminator.vpk");

			using var package = new Package();
			Assert.Throws<FormatException>(() => package.Read(path));
		}

		[Test]
		public void TestGetFullPath()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.FindEntry("test")?.GetFullPath(), Is.EqualTo("test"));
				Assert.That(package.FindEntry("folder with space/test")?.GetFullPath(), Is.EqualTo("folder with space/test"));
				Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.GetFullPath(), Is.EqualTo("folder with space/space_extension. txt"));
				Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.GetFullPath(), Is.EqualTo("uppercasefolder/bad_file_forfun.txt"));
				Assert.That(package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.GetFullPath(), Is.EqualTo("UpperCaseFolder/UpperCaseFile.txt"));

				Assert.That(package.FindEntry("test")?.GetFileName(), Is.EqualTo("test"));
				Assert.That(package.FindEntry("folder with space/test")?.GetFileName(), Is.EqualTo("test"));
				Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.GetFileName(), Is.EqualTo("space_extension. txt"));
				Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.GetFileName(), Is.EqualTo("bad_file_forfun.txt"));
			}
		}

		[Test]
		public void TestPackageEntryToString()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.FindEntry("test")?.ToString(), Is.EqualTo("test crc=0xba144cc metadatasz=0 fnumber=0 ofs=0x00 sz=39"));
				Assert.That(package.FindEntry("folder with space/test")?.ToString(), Is.EqualTo("folder with space/test crc=0xbf108706 metadatasz=0 fnumber=0 ofs=0x52 sz=41"));
				Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.ToString(), Is.EqualTo("folder with space/space_extension. txt crc=0x9321fc0 metadatasz=0 fnumber=0 ofs=0x7b sz=30"));
				Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.ToString(), Is.EqualTo("uppercasefolder/bad_file_forfun.txt crc=0x15c1490f metadatasz=0 fnumber=0 ofs=0xa2 sz=2"));
				Assert.That(package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.ToString(), Is.EqualTo("UpperCaseFolder/UpperCaseFile.txt crc=0x32cff012 metadatasz=0 fnumber=0 ofs=0x27 sz=43"));
			}
		}

		[Test]
		public void TestRespawnVPK()
		{
			using var resource = new Package();
			resource.SetFileName("apexlegends.vpk");

			using var ms = new MemoryStream([0x34, 0x12, 0xAA, 0x55, 0x02, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00]);
			Assert.Throws<NotSupportedException>(() => resource.Read(ms));
		}

		[Test]
		public void TestFileReadWithPreloadedBytes()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "preload.vpk");

			using var package = new Package();
			package.Read(path);

			var file = package.FindEntry("lorem.txt");

			Assert.That(file, Is.Not.Null);

			package.ReadEntry(file, out var allBytes);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(file.ToString(), Is.EqualTo("lorem.txt crc=0xf2cafa54 metadatasz=56 fnumber=32767 ofs=0x00 sz=588"));
				Assert.That(file.CRC32, Is.EqualTo(0xF2CAFA54));
				Assert.That(file.SmallData, Has.Length.EqualTo(56));
				Assert.That(file.Length, Is.EqualTo(588));
				Assert.That(file.SmallData, Is.EqualTo(Encoding.ASCII.GetBytes("Lorem ipsum dolor sit amet, consectetur adipiscing elit.")));
				Assert.That(
					allBytes,
					Is.EqualTo(Encoding.ASCII.GetBytes("Lorem ipsum dolor sit amet, consectetur adipiscing elit. Nam aliquam dapibus lorem, id suscipit urna pharetra non. " +
					"Vestibulum eu orci ut turpis rhoncus ullamcorper non id nisi. Class aptent taciti sociosqu ad litora torquent per " +
					"conubia nostra, per inceptos himenaeos. Ut rutrum pulvinar elit, in aliquet eros lobortis eget. Vestibulum ornare " +
					"faucibus erat, vel fringilla purus scelerisque tempor. Proin feugiat blandit sapien eget tempus. Praesent gravida in " +
					"risus a accumsan. Praesent egestas tincidunt dui nec laoreet. Sed ac lacus non tortor consectetur consectetur a ac " +
					"lacus. In rhoncus turpis a nisl volutpat, nec cursus urna tincidunt.\n")));
			}
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

		[Test]
		public void ExtractDirVPKWithoutSuffix()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_without_suffix.vpk");

			TestVPKExtraction(path);
		}

		[Test]
		public void ExtractIntoUserProvidedByteArray()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");
			using var package = new Package();
			package.Read(path);

			var entry = package.FindEntry("kitten.jpg");
			Assert.That(entry, Is.Not.Null);
			var biggerBuffer = new byte[entry.TotalLength + 256];
			package.ReadEntry(entry, biggerBuffer, validateCrc: true);

			var correctBuffer = new byte[entry.TotalLength];
			package.ReadEntry(entry, correctBuffer, validateCrc: true);

			var smallBuffer = new byte[entry.TotalLength - 1];
			Assert.Throws<ArgumentOutOfRangeException>(() => package.ReadEntry(entry, smallBuffer));
		}

		[Test]
		public void TestFileChecksums()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);
			Assert.DoesNotThrow(() => package.VerifyFileChecksums());

			var file = package.FindEntry("UpperCaseFolder/UpperCaseFile.txt");
			Assert.That(file, Is.Not.Null);
			Assert.That(file.CRC32, Is.EqualTo(0x32CFF012));

			file.CRC32 = 0xDEADBEEF;

			Assert.Throws<InvalidDataException>(() => package.VerifyFileChecksums());
		}

		[Test]
		public void ParsesCS2VPKWithRSA4096Signature()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "cs2_new_signature.vpk");

			using var package = new Package();
			package.Read(path);

			package.VerifyHashes();

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.Signature, Is.Null);
				Assert.That(package.PublicKey, Is.Null);
				Assert.That(package.SignatureType, Is.EqualTo(ESignatureType.OnlyFileChecksum));
				Assert.That(package.IsSignatureValid(), Is.True);
			}
		}

		[Test]
		public void ReadCSGOPak01WithRSA4096Signature()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "cs2_new_signature_actually_signed.vpk");

			using var package = new Package();
			package.Read(path);

			package.VerifyHashes();

			using (Assert.EnterMultipleScope())
			{
				Assert.That(package.SignatureType, Is.EqualTo(ESignatureType.OnlyFileChecksum));
				Assert.That(package.PublicKey, Is.Not.Null);
				Assert.That(package.Signature, Is.Not.Null);
				Assert.That(package.PublicKey!, Has.Length.EqualTo(550));
				Assert.That(package.Signature!, Has.Length.EqualTo(512));
				Assert.That(package.IsSignatureValid(), Is.True);
			}
		}

		[Test]
		public void InvalidTreeChecksumThrows()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "bad_hash_a.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.Throws<InvalidDataException>(() => package.VerifyHashes());
		}

		[Test]
		public void InvalidArchiveMD5EntriesChecksumThrows()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "bad_hash_b.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.Throws<InvalidDataException>(() => package.VerifyHashes());
		}

		[Test]
		public void InvalidWholeFileChecksumThrows()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "bad_hash_c.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.Throws<InvalidDataException>(() => package.VerifyHashes());
		}

		[Test]
		public void InvalidSignatureFails()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "bad_signature.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.That(package.IsSignatureValid(), Is.False);
		}

		[Test]
		public void OptimizingAfterReadThrows()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.Throws<InvalidOperationException>(() => package.OptimizeEntriesForBinarySearch());
		}

		[Test]
		public void DoesNotThrowWhenFindingInUnintializedPackage()
		{
			using var package = new Package();

			Assert.That(package.FindEntry("test.txt"), Is.Null);
		}

		[Test]
		public void ThrowsDueToMissingPakFile()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.Read(path);

			package.VerifyHashes();

			Assert.Throws<FileNotFoundException>(() => package.VerifyChunkHashes());
		}

		[Test]
		public void TestVerifyChunkHashes()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "fall_2025_rewardfx.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.DoesNotThrow(package.VerifyHashes);
			Assert.DoesNotThrow(() => package.VerifyChunkHashes(null));
		}

		[Test]
		public void TestVerifyChunkHashesBlake3()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "monster_hunter_dashboard_balek3_chunk_hash.vpk");

			using var package = new Package();
			package.Read(path);

			Assert.DoesNotThrow(package.VerifyHashes);
			Assert.DoesNotThrow(() => package.VerifyChunkHashes(null));
		}

		private static void TestVPKExtraction(string path)
		{
			using var package = new Package();
			package.Read(path);

			Assert.That(package.Entries, Has.Count.EqualTo(2));
			Assert.That(package.Entries.Keys, Does.Contain("jpg"));
			Assert.That(package.Entries.Keys, Does.Contain("proto"));

			var flatEntries = new Dictionary<string, PackageEntry>();
			var data = new Dictionary<string, string>();

			foreach (var a in package.Entries)
			{
				foreach (var b in a.Value)
				{
					Assert.That(b.TypeName, Is.EqualTo(a.Key));

					flatEntries.Add(b.FileName, b);

					package.ReadEntry(b, out var entry);

					data.Add(b.FileName + '.' + b.TypeName, Convert.ToHexString(SHA256.HashData(entry)));
				}
			}

			using (Assert.EnterMultipleScope())
			{
				Assert.That(data, Has.Count.EqualTo(3));
				Assert.That(data["kitten.jpg"], Is.EqualTo("1C03B452FEE5274B0BC1FA1A866EE6C8FA0D43AA464C6BCFB3AB531F6E813081"));
				Assert.That(data["steammessages_base.proto"], Is.EqualTo("FCC96AE59EE6BB9EEC4E16A50C928EFD3FB16E1CCA49E38BD2FA8391AB7936BE"));
				Assert.That(data["steammessages_clientserver.proto"], Is.EqualTo("1F90C38527D0853B4713942668F2DC83F433DBE919C002825A4526138A200428"));
			}

			using (Assert.EnterMultipleScope())
			{
				Assert.That(flatEntries["kitten"].TotalLength, Is.EqualTo(16361));
				Assert.That(flatEntries["steammessages_base"].TotalLength, Is.EqualTo(2563));
				Assert.That(flatEntries["steammessages_clientserver"].TotalLength, Is.EqualTo(39177));
			}
		}
	}
}
