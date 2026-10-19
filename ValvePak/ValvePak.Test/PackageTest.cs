using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;

namespace ValvePak.Test
{
	internal sealed class PackageTest
	{
		[Test]
		public async Task ParseVPK()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.Read(path);

			package.VerifyHashes();

			await Assert.That(package.IsSignatureValid()).IsTrue();
		}

		[Test]
		public async Task TestOriginalFileNameNotEndingInVpk()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "vpk_file_not_ending_in_vpk.vpk.0123456789abc");

			using var package = new Package();
			package.Read(path);

			await Assert.That(package.FindEntry("kitten.jpg")?.CRC32).IsEqualTo(0x9C800116u);
		}

		[Test]
		public async Task ThrowsOnInvalidPackage()
		{
			using var resource = new Package();
			using var ms = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);

			// Should yell about not setting file name
			await Assert.That(() => resource.Read(ms)).ThrowsExactly<InvalidOperationException>();

			resource.SetFileName("a.vpk");

			await Assert.That(() => resource.Read(ms)).ThrowsExactly<InvalidDataException>();
		}

		[Test]
		public async Task ThrowsOnCorrectHeaderWrongVersion()
		{
			using var resource = new Package();
			resource.SetFileName("a.vpk");

			using var ms = new MemoryStream([0x34, 0x12, 0xAA, 0x55, 0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x22]);
			await Assert.That(() => resource.Read(ms)).ThrowsExactly<InvalidDataException>();
		}

		[Test]
		public async Task FindEntryDeep()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.FindEntry("addons\\chess\\chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("addons/chess\\chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("addons/chess/chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("\\addons/chess/chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("/addons/chess/chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("\\addons/chess/hello_github_reader.vdf")).IsNull();
				await Assert.That(package.FindEntry("\\addons/hello_github_reader/chess.vdf")).IsNull();
				await Assert.That(package.FindEntry(string.Empty)).IsNull();
				await Assert.That(package.FindEntry(" ")).IsNull();
			}
		}

		[Test]
		public async Task TestBinarySearch()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.OptimizeEntriesForBinarySearch();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.FindEntry("addons\\chess\\chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("addons/chess\\chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("addons/chess/chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("\\addons/chess/chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("/addons/chess/chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("\\addons/chess/hello_github_reader.vdf")).IsNull();
				await Assert.That(package.FindEntry("\\addons/hello_github_reader/chess.vdf")).IsNull();
				await Assert.That(package.FindEntry(string.Empty)).IsNull();
				await Assert.That(package.FindEntry(" ")).IsNull();
			}

			foreach (var extension in package.Entries!.Values)
			{
				foreach (var entry in extension)
				{
					await Assert.That(package.FindEntry(entry.GetFullPath())).IsEqualTo(entry);
				}
			}
		}

		[Test]
		public async Task TestBinarySearchCaseInsensitive()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.FindEntry("ADDONS\\chess\\chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("addons/CHESS\\chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("addons/chess/CHESS.vdf")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("\\addons/chess/chess.VDF")?.CRC32).IsEqualTo(0xA4115395u);
				await Assert.That(package.FindEntry("/addons/CHESS/chess.vdf")?.CRC32).IsEqualTo(0xA4115395u);

				await Assert.That(package.FindEntry("\\addons/CHESS/hello_github_reader.vdf")).IsNull();
				await Assert.That(package.FindEntry("\\addons/hello_github_reader/CHESS.vdf")).IsNull();
			}

			foreach (var extension in package.Entries!.Values)
			{
				foreach (var entry in extension)
				{
					await Assert.That(package.FindEntry(entry.GetFullPath())).IsEqualTo(entry);
				}
			}
		}

		[Test]
		public async Task FindEntryRoot()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.FindEntry("kitten.jpg")?.CRC32).IsEqualTo(0x9C800116u);
				await Assert.That(package.FindEntry("\\kitten.jpg")?.CRC32).IsEqualTo(0x9C800116u);
				await Assert.That(package.FindEntry("/kitten.jpg")?.CRC32).IsEqualTo(0x9C800116u);
				await Assert.That(package.FindEntry("\\/kitten.jpg")?.CRC32).IsEqualTo(0x9C800116u);
			}
		}

		[Test]
		public async Task ThrowsNullArgumentInSetFilename()
		{
			using var package = new Package();
			await Assert.That(() => package.SetFileName(null!)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task ThrowsNullArgumentInReadStream()
		{
			using var package = new Package();
			await Assert.That(() => package.Read((Stream)null!)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task ThrowsNullArgumentInnReadString()
		{
			using var package = new Package();
			await Assert.That(() => package.Read((string)null!)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task ThrowsNullArgumentInFindEntry()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(() => package.FindEntry(null!)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task ThrowsNullArgumentInReadEntry()
		{
			using var package = new Package();
			await Assert.That(() => package.ReadEntry(null!, out var output)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task FindEntrySpacesAndExtensionless()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.FindEntry("test")?.CRC32).IsEqualTo(0x0BA144CCu);
				await Assert.That(package.FindEntry("folder with space/test")?.CRC32).IsEqualTo(0xBF108706u);
				await Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.CRC32).IsEqualTo(0x09321FC0u);
				await Assert.That(package.FindEntry("folder with space/file name with space.txt")?.CRC32).IsEqualTo(0x76D91432u);
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.CRC32).IsEqualTo(0x15C1490Fu);
				await Assert.That(package.FindEntry("UpperCaseFolder/bad_file_forfun.txt")?.CRC32).IsEqualTo(0x15C1490Fu);
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.TXT")?.CRC32).IsEqualTo(0x15C1490Fu);
				await Assert.That(package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")).IsNull();
				await Assert.That(package.FindEntry("uppercasefolder/UpperCaseFile.txt")).IsNull();
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt2")).IsNull();
			}
		}

		/// <summary>
		/// Returns the entry of broken_dir.vpk whose stored names have upper case letters, which <see cref="Package.FindEntry(string)"/> does not find.
		/// </summary>
		private static PackageEntry? GetUpperCaseEntry(Package package)
		{
			return package.Entries!["txt"].Find(static entry => entry.FileName == "UpperCaseFile");
		}

		[Test]
		public async Task ThrowsOnInvalidCRC32()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			var file = GetUpperCaseEntry(package);
			await Assert.That(file).IsNotNull();
			await Assert.That(file.CRC32).IsEqualTo(0x32CFF012u);

			file.CRC32 = 0xDEADBEEF;

			await Assert.That(() => package.ReadEntry(file, out _)).ThrowsExactly<InvalidDataException>();
			await Assert.That(() => package.ReadEntry(file, out _, true)).ThrowsExactly<InvalidDataException>()
				.WithMessage("CRC32 mismatch for read data (expected DEADBEEF, got 32CFF012).", StringComparison.Ordinal);
			await Assert.That(() => package.ReadEntry(file, out _, false)).ThrowsNothing();
		}

		[Test]
		public async Task ThrowsOnInvalidEntryTerminator()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "invalid_terminator.vpk");

			using var package = new Package();
			await Assert.That(() => package.Read(path)).ThrowsExactly<FormatException>();
		}

		[Test]
		public async Task TestGetFullPath()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.FindEntry("test")?.GetFullPath()).IsEqualTo("test");
				await Assert.That(package.FindEntry("folder with space/test")?.GetFullPath()).IsEqualTo("folder with space/test");
				await Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.GetFullPath()).IsEqualTo("folder with space/space_extension. txt");
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.GetFullPath()).IsEqualTo("uppercasefolder/bad_file_forfun.txt");
				await Assert.That(GetUpperCaseEntry(package)?.GetFullPath()).IsEqualTo("UpperCaseFolder/UpperCaseFile.txt");

				await Assert.That(package.FindEntry("test")?.GetFileName()).IsEqualTo("test");
				await Assert.That(package.FindEntry("folder with space/test")?.GetFileName()).IsEqualTo("test");
				await Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.GetFileName()).IsEqualTo("space_extension. txt");
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.GetFileName()).IsEqualTo("bad_file_forfun.txt");
			}
		}

		[Test]
		public async Task TestPackageEntryToString()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.FindEntry("test")?.ToString()).IsEqualTo("test crc=0xba144cc metadatasz=0 fnumber=0 ofs=0x00 sz=39");
				await Assert.That(package.FindEntry("folder with space/test")?.ToString()).IsEqualTo("folder with space/test crc=0xbf108706 metadatasz=0 fnumber=0 ofs=0x52 sz=41");
				await Assert.That(package.FindEntry("folder with space\\space_extension. txt")?.ToString()).IsEqualTo("folder with space/space_extension. txt crc=0x9321fc0 metadatasz=0 fnumber=0 ofs=0x7b sz=30");
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt")?.ToString()).IsEqualTo("uppercasefolder/bad_file_forfun.txt crc=0x15c1490f metadatasz=0 fnumber=0 ofs=0xa2 sz=2");
				await Assert.That(GetUpperCaseEntry(package)?.ToString()).IsEqualTo("UpperCaseFolder/UpperCaseFile.txt crc=0x32cff012 metadatasz=0 fnumber=0 ofs=0x27 sz=43");
			}
		}

		[Test]
		public async Task TestRespawnVPK()
		{
			using var resource = new Package();
			resource.SetFileName("apexlegends.vpk");

			using var ms = new MemoryStream([0x34, 0x12, 0xAA, 0x55, 0x02, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00]);
			await Assert.That(() => resource.Read(ms)).ThrowsExactly<NotSupportedException>();
		}

		[Test]
		public async Task TestFileReadWithPreloadedBytes()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "preload.vpk");

			using var package = new Package();
			package.Read(path);

			var file = package.FindEntry("lorem.txt");

			await Assert.That(file).IsNotNull();

			package.ReadEntry(file, out var allBytes);

			using (Assert.Multiple())
			{
				await Assert.That(file.ToString()).IsEqualTo("lorem.txt crc=0xf2cafa54 metadatasz=56 fnumber=32767 ofs=0x00 sz=588");
				await Assert.That(file.CRC32).IsEqualTo(0xF2CAFA54u);
				await Assert.That(file.SmallData).Count().IsEqualTo(56);
				await Assert.That(file.Length).IsEqualTo(588u);
				await Assert.That(file.SmallData).IsEquivalentTo(Encoding.ASCII.GetBytes("Lorem ipsum dolor sit amet, consectetur adipiscing elit."), CollectionOrdering.Matching);
				await Assert.That(allBytes).IsEquivalentTo(Encoding.ASCII.GetBytes("Lorem ipsum dolor sit amet, consectetur adipiscing elit. Nam aliquam dapibus lorem, id suscipit urna pharetra non. " +
					"Vestibulum eu orci ut turpis rhoncus ullamcorper non id nisi. Class aptent taciti sociosqu ad litora torquent per " +
					"conubia nostra, per inceptos himenaeos. Ut rutrum pulvinar elit, in aliquet eros lobortis eget. Vestibulum ornare " +
					"faucibus erat, vel fringilla purus scelerisque tempor. Proin feugiat blandit sapien eget tempus. Praesent gravida in " +
					"risus a accumsan. Praesent egestas tincidunt dui nec laoreet. Sed ac lacus non tortor consectetur consectetur a ac " +
					"lacus. In rhoncus turpis a nisl volutpat, nec cursus urna tincidunt.\n"), CollectionOrdering.Matching);
			}
		}

		[Test]
		public async Task ExtractInlineVPK()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");

			await TestVPKExtraction(path);
		}

		[Test]
		public async Task ExtractDirVPK()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_dir.vpk");

			await TestVPKExtraction(path);
		}

		[Test]
		public async Task ExtractDirVPKWithoutSuffix()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_without_suffix.vpk");

			await TestVPKExtraction(path);
		}

		[Test]
		public async Task ExtractIntoUserProvidedByteArray()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");
			using var package = new Package();
			package.Read(path);

			var entry = package.FindEntry("kitten.jpg");
			await Assert.That(entry).IsNotNull();
			var biggerBuffer = new byte[entry.TotalLength + 256];
			package.ReadEntry(entry, biggerBuffer, validateCrc: true);

			var correctBuffer = new byte[entry.TotalLength];
			package.ReadEntry(entry, correctBuffer, validateCrc: true);

			var smallBuffer = new byte[entry.TotalLength - 1];
			await Assert.That(() => package.ReadEntry(entry, smallBuffer)).ThrowsExactly<ArgumentOutOfRangeException>();
		}

		[Test]
		[Arguments("test_dir.vpk", false)]
		[Arguments("test_single.vpk", false)]
		[Arguments("test_single.vpk", true)]
		public async Task ThrowsWhenEntryExtendsPastEndOfArchive(string fileName, bool fromMemory)
		{
			var directory = Directory.CreateTempSubdirectory("ValvePakTest_").FullName;

			try
			{
				// Cut off the last entry (steammessages_clientserver.proto, 39177 bytes) and the end of the one before it (steammessages_base.proto, 2563 bytes)
				const int Cut = 39177 + 100;

				File.Copy(Path.Combine(AppContext.BaseDirectory, "Files", "test_dir.vpk"), Path.Combine(directory, "test_dir.vpk"));
				var chunk = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Files", "test_000.vpk"));
				await File.WriteAllBytesAsync(Path.Combine(directory, "test_000.vpk"), chunk[..^Cut]);

				var single = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk"));
				await File.WriteAllBytesAsync(Path.Combine(directory, "test_single.vpk"), single[..^Cut]);

				var path = Path.Combine(directory, fileName);

				using var package = new Package();

				if (fromMemory)
				{
					package.SetFileName(path);
					package.Read(new MemoryStream(await File.ReadAllBytesAsync(path)));
				}
				else
				{
					package.Read(path);
				}

				var kitten = package.FindEntry("kitten.jpg")!;
				await Assert.That(() => package.ReadEntry(kitten, out _, validateCrc: true)).ThrowsNothing();

				await AssertEntryIsTruncated(package, "steammessages_base.proto");
				await AssertEntryIsTruncated(package, "steammessages_clientserver.proto");
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		private static async Task AssertEntryIsTruncated(Package package, string fileName)
		{
			var entry = package.FindEntry(fileName)!;

			using (Assert.Multiple())
			{
				await Assert.That(() => package.ReadEntry(entry, out _, validateCrc: false)).ThrowsExactly<EndOfStreamException>();
				await Assert.That(() => package.ReadEntry(entry, out _, validateCrc: true)).ThrowsExactly<EndOfStreamException>();
				await Assert.That(() => package.ReadEntry(entry, new byte[entry.TotalLength], validateCrc: false)).ThrowsExactly<EndOfStreamException>();
				await Assert.That(() => package.GetMemoryMappedStreamIfPossible(entry)).ThrowsExactly<EndOfStreamException>();
			}
		}

		[Test]
		public async Task TestFileChecksums()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);
			await Assert.That(() => package.VerifyFileChecksums()).ThrowsNothing();

			var file = GetUpperCaseEntry(package);
			await Assert.That(file).IsNotNull();
			await Assert.That(file.CRC32).IsEqualTo(0x32CFF012u);

			file.CRC32 = 0xDEADBEEF;

			await Assert.That(() => package.VerifyFileChecksums()).ThrowsExactly<InvalidDataException>();
		}

		[Test]
		public async Task ParsesCS2VPKWithRSA4096Signature()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "cs2_new_signature.vpk");

			using var package = new Package();
			package.Read(path);

			package.VerifyHashes();

			using (Assert.Multiple())
			{
				await Assert.That(package.Signature).IsNull();
				await Assert.That(package.PublicKey).IsNull();
				await Assert.That(package.SignatureType).IsEqualTo(ESignatureType.OnlyFileChecksum);
				await Assert.That(package.IsSignatureValid()).IsTrue();
			}
		}

		[Test]
		public async Task ReadCSGOPak01WithRSA4096Signature()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "cs2_new_signature_actually_signed.vpk");

			using var package = new Package();
			package.Read(path);

			package.VerifyHashes();

			using (Assert.Multiple())
			{
				await Assert.That(package.SignatureType).IsEqualTo(ESignatureType.OnlyFileChecksum);
				await Assert.That(package.PublicKey).IsNotNull();
				await Assert.That(package.Signature).IsNotNull();
				await Assert.That(package.PublicKey!).Count().IsEqualTo(550);
				await Assert.That(package.Signature!).Count().IsEqualTo(512);
				await Assert.That(package.IsSignatureValid()).IsTrue();
			}
		}

		[Test]
		public async Task InvalidTreeChecksumThrows()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "bad_hash_a.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(() => package.VerifyHashes()).ThrowsExactly<InvalidDataException>();
		}

		[Test]
		public async Task InvalidArchiveMD5EntriesChecksumThrows()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "bad_hash_b.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(() => package.VerifyHashes()).ThrowsExactly<InvalidDataException>();
		}

		[Test]
		public async Task InvalidWholeFileChecksumThrows()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "bad_hash_c.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(() => package.VerifyHashes()).ThrowsExactly<InvalidDataException>();
		}

		[Test]
		[Arguments(0, "File tree")]
		[Arguments(16, "Archive MD5 entries")]
		public async Task SectionChecksumMismatchIsReportedWhenWholeFileChecksumMatches(int checksumOffset, string sectionName)
		{
			var data = CreateSmallPackageBytes();
			var otherMD5SectionOffset = GetOtherMD5SectionOffset(data);

			data.AsSpan(otherMD5SectionOffset + checksumOffset, 16).Fill(0xAB);
			MD5.HashData(data.AsSpan(0, otherMD5SectionOffset + 32)).CopyTo(data, otherMD5SectionOffset + 32);

			using var package = new Package();
			package.SetFileName("a.vpk");
			package.Read(new MemoryStream(data));

			var progress = new SynchronousProgress();

			await Assert.That(() => package.VerifyHashes(progress)).ThrowsNothing();
			await Assert.That(progress.Reports).Count().IsEqualTo(1);
			await Assert.That(progress.Reports[0]).StartsWith($"{sectionName} checksum mismatch (", StringComparison.Ordinal);
		}

		[Test]
		public async Task SectionChecksumMismatchThrowsWhenWholeFileChecksumMismatches()
		{
			var data = CreateSmallPackageBytes();
			var otherMD5SectionOffset = GetOtherMD5SectionOffset(data);

			data.AsSpan(otherMD5SectionOffset, 16).Fill(0xAB);

			using var package = new Package();
			package.SetFileName("a.vpk");
			package.Read(new MemoryStream(data));

			await Assert.That(() => package.VerifyHashes()).ThrowsExactly<InvalidDataException>()
				.WithMessageContaining("File tree checksum mismatch", StringComparison.Ordinal);
		}

		[Test]
		public async Task InvalidSignatureFails()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "bad_signature.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(package.IsSignatureValid()).IsFalse();
		}

		[Test]
		public async Task OptimizingAfterReadThrows()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(() => package.OptimizeEntriesForBinarySearch()).ThrowsExactly<InvalidOperationException>();
		}

		[Test]
		public async Task DoesNotThrowWhenFindingInUnintializedPackage()
		{
			using var package = new Package();

			await Assert.That(package.FindEntry("test.txt")).IsNull();
		}

		[Test]
		public async Task ThrowsDueToMissingPakFile()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.Read(path);

			package.VerifyHashes();

			await Assert.That(() => package.VerifyChunkHashes()).ThrowsExactly<FileNotFoundException>();
		}

		[Test]
		public async Task TestVerifyChunkHashes()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "fall_2025_rewardfx.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(() => package.VerifyHashes()).ThrowsNothing();
			await Assert.That(() => package.VerifyChunkHashes(null)).ThrowsNothing();
		}

		[Test]
		public async Task TestVerifyChunkHashesBlake3()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "monster_hunter_dashboard_balek3_chunk_hash.vpk");

			using var package = new Package();
			package.Read(path);

			await Assert.That(() => package.VerifyHashes()).ThrowsNothing();
			await Assert.That(() => package.VerifyChunkHashes(null)).ThrowsNothing();
		}

		[Test]
		public async Task SetFileNameStripsVpkExtensionAndDirSuffix()
		{
			using var package1 = new Package();
			package1.SetFileName("foo_dir.vpk");

			using (Assert.Multiple())
			{
				await Assert.That(package1.FileName).IsEqualTo("foo");
				await Assert.That(package1.IsDirVPK).IsTrue();
			}

			using var package2 = new Package();
			package2.SetFileName("bar.vpk");

			using (Assert.Multiple())
			{
				await Assert.That(package2.FileName).IsEqualTo("bar");
				await Assert.That(package2.IsDirVPK).IsFalse();
			}
		}

		[Test]
		public async Task ThrowsWhenReadTwice()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");

			using var package = new Package();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(() => package.Read(path)).ThrowsExactly<InvalidOperationException>();
				await Assert.That(() => package.SetFileName(path)).ThrowsExactly<InvalidOperationException>();
			}
		}

		[Test]
		public async Task VerifyHashesSkipsVersion1()
		{
			using var package = new Package();
			package.SetFileName("a.vpk");

			// VPK v1 header: correct magic, version 1, tree size 1
			using var ms = new MemoryStream([0x34, 0x12, 0xAA, 0x55, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]);
			package.Read(ms);

			var progress = new SynchronousProgress();

			await Assert.That(() => package.VerifyHashes(progress)).ThrowsNothing();
			await Assert.That(progress.Reports).IsEquivalentTo(["Version 1 packages have no checksums to verify."]);
		}

		[Test]
		public async Task VerifyFileChecksumsDoesNothingOnNullEntries()
		{
			using var package = new Package();
			await Assert.That(() => package.VerifyFileChecksums()).ThrowsNothing();
		}

		[Test]
		public async Task VerifyFileChecksumsWithProgressReporter()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			var progress = new SynchronousProgress();
			package.VerifyFileChecksums(progress);

			await Assert.That(progress.Reports).IsNotEmpty();
		}

		private sealed class SynchronousProgress : IProgress<string>
		{
			public List<string> Reports { get; } = [];
			public void Report(string value) => Reports.Add(value);
		}

		[Test]
		public async Task DisposeCanBeCalledMultipleTimes()
		{
			var package = new Package();
			package.Dispose();
			await Assert.That(() => package.Dispose()).ThrowsNothing();
		}

		[Test]
		public async Task IsSignatureValidReturnsTrueWhenNoSignature()
		{
			using var package = new Package();
			await Assert.That(package.IsSignatureValid()).IsTrue();
		}

		[Test]
		public async Task IsSignatureValidReturnsFalseAfterDispose()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "platform_misc_dir.vpk");

			var package = new Package();
			package.Read(path);

			using (Assert.Multiple())
			{
				await Assert.That(package.Signature).IsNotNull();
				await Assert.That(package.PublicKey).IsNotNull();
			}

			package.Dispose();

			await Assert.That(package.IsSignatureValid()).IsFalse();
		}

		[Test]
		public async Task VerifyHashesThrowsWhenPackageWasNotRead()
		{
			using var package = new Package();

			await Assert.That(() => package.VerifyHashes()).ThrowsExactly<InvalidOperationException>()
				.WithMessage("The package has not been read.", StringComparison.Ordinal);

			package.SetFileName("a.vpk");

			await Assert.That(() => package.VerifyHashes()).ThrowsExactly<InvalidOperationException>();
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task ReadsPackageStartingAtNonZeroStreamOffset(bool fromFile)
		{
			var directory = Directory.CreateTempSubdirectory("ValvePakTest_").FullName;

			try
			{
				var expected = ReadAllEntryData(Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk"));

				using (var package = await ReadEmbeddedPackage("test_single.vpk", fromFile, directory))
				{
					await Assert.That(package.HeaderSize).IsEqualTo(28u);
					await Assert.That(package.Entries).IsNotNull();

					var entryCount = 0;

					foreach (var entries in package.Entries.Values)
					{
						foreach (var entry in entries)
						{
							var path = entry.GetFullPath();

							package.ReadEntry(entry, out var data, validateCrc: true);

							using var mapped = package.GetMemoryMappedStreamIfPossible(entry);
							using var mappedCopy = new MemoryStream();
							await mapped.CopyToAsync(mappedCopy);

							using (Assert.Multiple())
							{
								await Assert.That(data).IsEquivalentTo(expected[path], CollectionOrdering.Matching);
								await Assert.That(mappedCopy.ToArray()).IsEquivalentTo(expected[path], CollectionOrdering.Matching);
							}

							entryCount++;
						}
					}

					await Assert.That(entryCount).IsEqualTo(expected.Count);
					await Assert.That(() => package.VerifyHashes()).ThrowsNothing();
					await Assert.That(() => package.VerifyFileChecksums()).ThrowsNothing();
				}
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task VerifiesSignatureOfPackageStartingAtNonZeroStreamOffset(bool fromFile)
		{
			var directory = Directory.CreateTempSubdirectory("ValvePakTest_").FullName;

			try
			{
				using var package = await ReadEmbeddedPackage("platform_misc_dir.vpk", fromFile, directory);

				await Assert.That(package.Signature).IsNotNull();
				await Assert.That(() => package.VerifyHashes()).ThrowsNothing();
				await Assert.That(package.IsSignatureValid()).IsTrue();
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[Test]
		public async Task ThrowsOnArchiveMD5SectionSizeNotMultipleOfEntrySize()
		{
			var package = CreateSmallPackageBytes();

			foreach (var size in new uint[] { 1, 27, 29, 55 })
			{
				var patched = (byte[])package.Clone();
				BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(ArchiveMD5SectionSizeOffset), size);

				await AssertReadThrowsInvalidData(patched, $"Archive MD5 section size ({size}) is not a multiple of 28.");
			}
		}

		[Test]
		public async Task ThrowsOnTreeSizeLargerThanRestOfFile()
		{
			var package = CreateSmallPackageBytes();
			var restOfFile = package.Length - 28;

			foreach (var size in new uint[] { (uint)restOfFile + 1, (uint)package.Length, uint.MaxValue })
			{
				var patched = (byte[])package.Clone();
				BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(TreeSizeOffset), size);

				await AssertReadThrowsInvalidData(patched, $"Directory tree size ({size}) is larger than the rest of the file ({restOfFile}).");
			}
		}

		[Test]
		[Arguments(1000)]
		[Arguments(-1)]
		public async Task ThrowsOnInvalidPublicKeySize(int publicKeySize)
		{
			var package = CreateSignedPackageBytes(publicKeySize);

			await AssertReadThrowsInvalidData(package, $"Invalid public key size in the signature section ({publicKeySize}).");
		}

		[Test]
		[Arguments(1000)]
		[Arguments(-1)]
		public async Task ThrowsOnInvalidSignatureSize(int signatureSize)
		{
			var package = CreateSignedPackageBytes(0, signatureSize);

			await AssertReadThrowsInvalidData(package, $"Invalid signature size in the signature section ({signatureSize}).");
		}

		[Test]
		[Arguments(1000, 0, "public key")]
		[Arguments(-1, 0, "public key")]
		[Arguments(0, 1000, "signature")]
		[Arguments(0, -1, "signature")]
		public async Task ThrowsOnInvalidSizeInNewSignatureSection(int publicKeySize, int signatureSize, string name)
		{
			var size = publicKeySize != 0 ? publicKeySize : signatureSize;

			// Magic, signature type, public key size, signature size, reserved
			var package = CreateSignedPackageBytes(Package.MAGIC, 1, publicKeySize, signatureSize, 0);

			await AssertReadThrowsInvalidData(package, $"Invalid {name} size in the signature section ({size}).");
		}

		[Test]
		[Arguments("abc")]
		[Arguments("txt\0dir")]
		[Arguments("txt\0dir\0file")]
		public async Task ThrowsOnDirectoryTreeEndingInTheMiddleOfString(string tree)
		{
			var treeBytes = Encoding.ASCII.GetBytes(tree);
			var package = new byte[12 + treeBytes.Length];

			BinaryPrimitives.WriteUInt32LittleEndian(package, (uint)Package.MAGIC);
			BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(4), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(8), (uint)treeBytes.Length);
			treeBytes.CopyTo(package, 12);

			await AssertReadThrowsInvalidData(package, "Directory tree ends in the middle of a string.");
		}

		private const int TreeSizeOffset = 8;
		private const int FileDataSectionSizeOffset = 12;
		private const int ArchiveMD5SectionSizeOffset = 16;
		private const int SignatureSectionSizeOffset = 24;
		private const int HeaderSize = 28;
		private const int UnsignedSignatureSectionSize = 20;
		private const int EmbeddedPrefixLength = 1000;
		private const int EmbeddedSuffixLength = 50;

		private static byte[] CreateSmallPackageBytes()
		{
			using var package = new Package();
			package.AddFile("hello.txt", Encoding.UTF8.GetBytes("world"));

			using var output = new MemoryStream();
			package.Write(output);

			return output.ToArray();
		}

		private static int GetOtherMD5SectionOffset(byte[] package)
		{
			var span = package.AsSpan();
			var treeSize = BinaryPrimitives.ReadInt32LittleEndian(span[TreeSizeOffset..]);
			var fileDataSectionSize = BinaryPrimitives.ReadInt32LittleEndian(span[FileDataSectionSizeOffset..]);
			var archiveMD5SectionSize = BinaryPrimitives.ReadInt32LittleEndian(span[ArchiveMD5SectionSizeOffset..]);

			return HeaderSize + treeSize + fileDataSectionSize + archiveMD5SectionSize;
		}

		/// <summary>
		/// Creates a package whose signature section holds the given little-endian integers.
		/// </summary>
		private static byte[] CreateSignedPackageBytes(params int[] signatureSection)
		{
			var package = CreateSmallPackageBytes().AsSpan(..^UnsignedSignatureSectionSize);
			var signatureSize = signatureSection.Length * sizeof(int);
			var result = new byte[package.Length + signatureSize];

			package.CopyTo(result);
			BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(SignatureSectionSizeOffset), (uint)signatureSize);

			for (var i = 0; i < signatureSection.Length; i++)
			{
				BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(package.Length + (i * sizeof(int))), signatureSection[i]);
			}

			return result;
		}

		private static async Task AssertReadThrowsInvalidData(byte[] data, string message)
		{
			using var package = new Package();
			package.SetFileName("a.vpk");

			using var ms = new MemoryStream(data);
			await Assert.That(() => package.Read(ms)).ThrowsExactly<InvalidDataException>()
				.WithMessage(message, StringComparison.Ordinal);
		}

		private static Dictionary<string, byte[]> ReadAllEntryData(string path)
		{
			var result = new Dictionary<string, byte[]>();

			using var package = new Package();
			package.Read(path);

			foreach (var entries in package.Entries!.Values)
			{
				foreach (var entry in entries)
				{
					package.ReadEntry(entry, out var data);
					result[entry.GetFullPath()] = data;
				}
			}

			return result;
		}

		/// <summary>
		/// Reads a fixture that is surrounded by other bytes, from a stream that is positioned at the start of the package.
		/// </summary>
		private static async Task<Package> ReadEmbeddedPackage(string fixtureName, bool fromFile, string directory)
		{
			var fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Files", fixtureName));
			var embedded = new byte[EmbeddedPrefixLength + fixture.Length + EmbeddedSuffixLength];

			Array.Fill(embedded, (byte)0xCD);
			fixture.CopyTo(embedded, EmbeddedPrefixLength);

			var path = Path.Combine(directory, fixtureName);
			var package = new Package();
			package.SetFileName(path);

			if (fromFile)
			{
				await File.WriteAllBytesAsync(path, embedded);

#pragma warning disable CA2000 // The package disposes the stream
				var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
#pragma warning restore CA2000
				fileStream.Position = EmbeddedPrefixLength;
				package.Read(fileStream);
			}
			else
			{
				var memoryStream = new MemoryStream(embedded);
				memoryStream.Position = EmbeddedPrefixLength;
				package.Read(memoryStream);
			}

			return package;
		}

		private static async Task TestVPKExtraction(string path)
		{
			using var package = new Package();
			package.Read(path);

			await Assert.That(package.Entries).IsNotNull();
			await Assert.That(package.Entries).Count().IsEqualTo(2);
			await Assert.That(package.Entries.Keys).Contains("jpg");
			await Assert.That(package.Entries.Keys).Contains("proto");

			var flatEntries = new Dictionary<string, PackageEntry>();
			var data = new Dictionary<string, string>();

			foreach (var a in package.Entries)
			{
				foreach (var b in a.Value)
				{
					await Assert.That(b.TypeName).IsEqualTo(a.Key);

					flatEntries.Add(b.FileName, b);

					package.ReadEntry(b, out var entry);

					data.Add(b.FileName + '.' + b.TypeName, Convert.ToHexString(SHA256.HashData(entry)));
				}
			}

			using (Assert.Multiple())
			{
				await Assert.That(data).Count().IsEqualTo(3);
				await Assert.That(data["kitten.jpg"]).IsEqualTo("1C03B452FEE5274B0BC1FA1A866EE6C8FA0D43AA464C6BCFB3AB531F6E813081");
				await Assert.That(data["steammessages_base.proto"]).IsEqualTo("FCC96AE59EE6BB9EEC4E16A50C928EFD3FB16E1CCA49E38BD2FA8391AB7936BE");
				await Assert.That(data["steammessages_clientserver.proto"]).IsEqualTo("1F90C38527D0853B4713942668F2DC83F433DBE919C002825A4526138A200428");
			}

			using (Assert.Multiple())
			{
				await Assert.That(flatEntries["kitten"].TotalLength).IsEqualTo(16361u);
				await Assert.That(flatEntries["steammessages_base"].TotalLength).IsEqualTo(2563u);
				await Assert.That(flatEntries["steammessages_clientserver"].TotalLength).IsEqualTo(39177u);
			}
		}
	}
}
