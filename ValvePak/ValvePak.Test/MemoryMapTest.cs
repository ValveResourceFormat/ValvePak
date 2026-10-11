using System;
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
			await Assert.That(stream).IsNotAssignableTo<MemoryStream>();
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
			await Assert.That(stream).IsNotAssignableTo<MemoryStream>();
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
			await Assert.That(stream).IsNotAssignableTo<MemoryStream>();
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
