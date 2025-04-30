using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using NUnit.Framework;
using SteamDatabase.ValvePak;

namespace Tests
{
	[TestFixture]
	public class MemoryMappedTest
	{
		private static void VerifyKitten(Stream stream) => Assert.That(Convert.ToHexString(SHA256.HashData(stream)), Is.EqualTo("1C03B452FEE5274B0BC1FA1A866EE6C8FA0D43AA464C6BCFB3AB531F6E813081"));
		private static void VerifyProto(Stream stream) => Assert.That(Convert.ToHexString(SHA256.HashData(stream)), Is.EqualTo("FCC96AE59EE6BB9EEC4E16A50C928EFD3FB16E1CCA49E38BD2FA8391AB7936BE"));

		[Test]
		public void ReturnsCorrectStreamsForSplitPackages()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			Assert.That(stream, Is.InstanceOf<MemoryMappedViewStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			Assert.That(stream2, Is.InstanceOf<MemoryStream>()); // This file is less than 4kb
			VerifyProto(stream2);
		}

		[Test]
		public void ReturnsCorrectStreams()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			Assert.That(stream, Is.InstanceOf<MemoryMappedViewStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			Assert.That(stream2, Is.InstanceOf<MemoryStream>());
			VerifyProto(stream2);
		}

		[Test]
		public void ReturnsCorrectStreamsWhenUsingFileStream()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");
			using var fileStream = File.OpenRead(path);

			using var package = new Package();
			package.SetFileName("surely non existing file");
			package.Read(fileStream);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			Assert.That(stream, Is.InstanceOf<MemoryMappedViewStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			Assert.That(stream2, Is.InstanceOf<MemoryStream>());
			VerifyProto(stream2);
		}

		[Test]
		public void ReturnsCorrectStreamsWhenUsingMemoryStream()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");
			using var memoryStream = new MemoryStream(File.ReadAllBytes(path));

			using var package = new Package();
			package.SetFileName("surely non existing file");
			package.Read(memoryStream);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg")!);
			Assert.That(stream, Is.InstanceOf<MemoryStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto")!);
			Assert.That(stream2, Is.InstanceOf<MemoryStream>());
			VerifyProto(stream2);
		}
	}
}
