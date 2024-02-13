using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using NUnit.Framework;
using SteamDatabase.ValvePak;

namespace Tests
{
	[TestFixture]
	public class MemoryMappedTest
	{
		[Test]
		public void ReturnsMemoryMappedViewStream()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg"));
			Assert.That(stream, Is.InstanceOf<MemoryMappedViewStream>());

			using var stream2 = package.GetMemoryMappedStreamIfPossible(package.FindEntry("steammessages_base.proto"));
			Assert.That(stream2, Is.InstanceOf<MemoryStream>()); // This file is less than 4kb
		}

		[Test]
		public void ReturnsMemoryStream()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

			using var package = new Package();
			package.Read(path);

			using var stream = package.GetMemoryMappedStreamIfPossible(package.FindEntry("kitten.jpg"));

			Assert.That(stream, Is.InstanceOf<MemoryStream>());
		}
	}
}
