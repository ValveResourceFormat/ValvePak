using System;
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
				await Assert.That(package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.CRC32).IsEqualTo(0x32CFF012u);
				await Assert.That(package.FindEntry("UpperCaseFolder/bad_file_forfun.txt")).IsNull();
				await Assert.That(package.FindEntry("uppercasefolder/UpperCaseFile.txt")).IsNull();
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.TXT")).IsNull();
				await Assert.That(package.FindEntry("uppercasefolder/bad_file_forfun.txt2")).IsNull();
			}
		}

		[Test]
		public async Task ThrowsOnInvalidCRC32()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);

			var file = package.FindEntry("UpperCaseFolder/UpperCaseFile.txt");
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
				await Assert.That(package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.GetFullPath()).IsEqualTo("UpperCaseFolder/UpperCaseFile.txt");

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
				await Assert.That(package.FindEntry("UpperCaseFolder/UpperCaseFile.txt")?.ToString()).IsEqualTo("UpperCaseFolder/UpperCaseFile.txt crc=0x32cff012 metadatasz=0 fnumber=0 ofs=0x27 sz=43");
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
		public async Task ThrowsWhenEntryExtendsPastEndOfArchive()
		{
			var directory = Path.Combine(Path.GetTempPath(), "ValvePakTest_" + Path.GetRandomFileName());
			Directory.CreateDirectory(directory);

			try
			{
				// Cut off the last entry (steammessages_clientserver.proto, 39177 bytes) and the end of the one before it (steammessages_base.proto, 2563 bytes)
				const int Cut = 39177 + 100;

				File.Copy(Path.Combine(AppContext.BaseDirectory, "Files", "test_dir.vpk"), Path.Combine(directory, "test_dir.vpk"));
				var chunk = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Files", "test_000.vpk"));
				await File.WriteAllBytesAsync(Path.Combine(directory, "test_000.vpk"), chunk[..^Cut]);

				var single = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk"));
				await File.WriteAllBytesAsync(Path.Combine(directory, "test_single.vpk"), single[..^Cut]);

				foreach (var (name, fromMemory) in new[] { ("test_dir.vpk", false), ("test_single.vpk", false), ("test_single.vpk", true) })
				{
					var path = Path.Combine(directory, name);

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

					foreach (var truncatedName in new[] { "steammessages_base.proto", "steammessages_clientserver.proto" })
					{
						var truncated = package.FindEntry(truncatedName)!;

						using (Assert.Multiple())
						{
							await Assert.That(() => package.ReadEntry(truncated, out _, validateCrc: false)).ThrowsExactly<EndOfStreamException>();
							await Assert.That(() => package.ReadEntry(truncated, out _, validateCrc: true)).ThrowsExactly<EndOfStreamException>();
							await Assert.That(() => package.ReadEntry(truncated, new byte[truncated.TotalLength], validateCrc: false)).ThrowsExactly<EndOfStreamException>();
							await Assert.That(() => package.GetMemoryMappedStreamIfPossible(truncated)).ThrowsExactly<EndOfStreamException>();
						}
					}
				}
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[Test]
		public async Task TestFileChecksums()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "broken_dir.vpk");

			using var package = new Package();
			package.Read(path);
			await Assert.That(() => package.VerifyFileChecksums()).ThrowsNothing();

			var file = package.FindEntry("UpperCaseFolder/UpperCaseFile.txt");
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
		public async Task VerifyHashesThrowsOnVersion1()
		{
			using var package = new Package();
			package.SetFileName("a.vpk");

			// VPK v1 header: correct magic, version 1, tree size 1
			using var ms = new MemoryStream([0x34, 0x12, 0xAA, 0x55, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]);
			package.Read(ms);

			await Assert.That(() => package.VerifyHashes()).ThrowsExactly<InvalidDataException>()
				.WithMessage("Only version 2 is supported.", StringComparison.Ordinal);
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
