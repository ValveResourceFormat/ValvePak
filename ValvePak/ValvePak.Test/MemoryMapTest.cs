using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace ValvePak.Test
{
	internal sealed class MemoryMappedTest
	{
		private static async Task VerifyKitten(Stream stream) => await Assert.That(Convert.ToHexString(await SHA256.HashDataAsync(stream))).IsEqualTo("1C03B452FEE5274B0BC1FA1A866EE6C8FA0D43AA464C6BCFB3AB531F6E813081");
		private static async Task VerifyProto(Stream stream) => await Assert.That(Convert.ToHexString(await SHA256.HashDataAsync(stream))).IsEqualTo("FCC96AE59EE6BB9EEC4E16A50C928EFD3FB16E1CCA49E38BD2FA8391AB7936BE");

		[Test]
		public async Task ReturnsCorrectStreamsForSplitPackages()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			await Assert.That(stream).IsAssignableTo<UnmanagedMemoryStream>();
			await VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			await Assert.That(stream2).IsAssignableTo<MemoryStream>(); // This file is less than 4kb
			await VerifyProto(stream2);
		}

		[Test]
		public async Task ReturnsCorrectStreams()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			await Assert.That(stream).IsAssignableTo<UnmanagedMemoryStream>();
			await VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			await Assert.That(stream2).IsAssignableTo<MemoryStream>();
			await VerifyProto(stream2);
		}

		[Test]
		public async Task ReturnsCorrectStreamsWhenUsingFileStream()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");
			using var fileStream = File.OpenRead(path);

			using var package = new Package();
			package.SetFileName("surely non existing file");
			package.Read(fileStream);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			await Assert.That(stream).IsAssignableTo<UnmanagedMemoryStream>();
			await VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			await Assert.That(stream2).IsAssignableTo<MemoryStream>();
			await VerifyProto(stream2);
		}

		[Test]
		public async Task ReturnsCorrectStreamsWhenUsingMemoryStream()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");
			using var memoryStream = new MemoryStream(await File.ReadAllBytesAsync(path));

			using var package = new Package();
			package.SetFileName("surely non existing file");
			package.Read(memoryStream);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			await Assert.That(stream).IsAssignableTo<MemoryStream>();
			await VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			await Assert.That(stream2).IsAssignableTo<MemoryStream>();
			await VerifyProto(stream2);
		}

		[Test]
		[Arguments("test_single.vpk", false)]
		[Arguments("test_single.vpk", true)]
		[Arguments("test_dir.vpk", false)]
		public async Task ReadsEntriesConcurrently(string fileName, bool fromMemory)
		{
			const int Iterations = 5;
			const int ReadsPerEntry = 8;

			var path = Path.Combine(AppContext.BaseDirectory, "Files", fileName);
			var expected = new Dictionary<string, byte[]>();

			using (var reference = new Package())
			{
				reference.Read(path);

				foreach (var entries in reference.Entries!.Values)
				{
					foreach (var entry in entries)
					{
						reference.ReadEntry(entry, out var data);
						expected[entry.GetFullPath()] = data;
					}
				}
			}

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

			var allEntries = new List<PackageEntry>();

			foreach (var entries in package.Entries!.Values)
			{
				allEntries.AddRange(entries);
			}

			var mismatches = new ConcurrentBag<string>();
			var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };

			for (var iteration = 0; iteration < Iterations; iteration++)
			{
				Parallel.For(0, allEntries.Count * ReadsPerEntry, options, i =>
				{
					var entry = allEntries[i % allEntries.Count];
					var fullPath = entry.GetFullPath();

					package.ReadEntry(entry, out var data);

					if (!data.AsSpan().SequenceEqual(expected[fullPath]))
					{
						mismatches.Add($"ReadEntry {fullPath}");
					}

					using var stream = package.GetMemoryMappedStreamIfPossible(entry);
					using var copy = new MemoryStream();
					stream.CopyTo(copy);

					if (!copy.ToArray().AsSpan().SequenceEqual(expected[fullPath]))
					{
						mismatches.Add($"GetMemoryMappedStreamIfPossible {fullPath}");
					}
				});
			}

			await Assert.That(allEntries).Count().IsEqualTo(expected.Count);
			await Assert.That(mismatches).IsEmpty();
		}

		[Test]
		public async Task GetMemoryMappedStreamIfPossibleThrowsOnNullEntry()
		{
			using var package = new Package();
			await Assert.That(() => package.GetMemoryMappedStreamIfPossible(null!)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task GetMemoryMappedStreamIfPossibleWithPreloadedBytesReturnsMemoryStream()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "preload.vpk");

			using var package = new Package();
			package.Read(path);

			var entry = package.FindEntry("lorem.txt");
			await Assert.That(entry).IsNotNull();
			await Assert.That(entry.SmallData).IsNotEmpty();

			using var stream = package.GetMemoryMappedStreamIfPossible(entry);
			await Assert.That(stream).IsAssignableTo<MemoryStream>();
		}
	}
}
