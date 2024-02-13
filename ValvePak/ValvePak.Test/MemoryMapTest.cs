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
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms, does not matter here
		private static void VerifyKitten(Stream stream) => Assert.That(Convert.ToHexString(SHA1.HashData(stream)), Is.EqualTo("E0D865F19F0A4A7EA3753FBFCFC624EE8B46928A"));
		private static void VerifyProto(Stream stream) => Assert.That(Convert.ToHexString(SHA1.HashData(stream)), Is.EqualTo("2EFFCB09BE81E8BEE88CB7BA8C18E87D3E1168DB"));
#pragma warning restore CA5350

		[Test]
		public void ReturnsCorrectStreamsForSplitPackages()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg"));
			Assert.That(stream, Is.InstanceOf<MemoryMappedViewStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto"));
			Assert.That(stream2, Is.InstanceOf<MemoryStream>()); // This file is less than 4kb
			VerifyProto(stream2);
		}

		[Test]
		public void ReturnsCorrectStreams()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg"));
			Assert.That(stream, Is.InstanceOf<MemoryMappedViewStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto"));
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

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg"));
			Assert.That(stream, Is.InstanceOf<MemoryMappedViewStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto"));
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

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg"));
			Assert.That(stream, Is.InstanceOf<MemoryStream>());
			VerifyKitten(stream);

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto"));
			Assert.That(stream2, Is.InstanceOf<MemoryStream>());
			VerifyProto(stream2);
		}
	}
}
