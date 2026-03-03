<h1 align="center"><img src="./Misc/logo.png" width="64" height="64" align="center"> Valve Pak for .NET</h1>

<p align="center">
    <a href="https://github.com/ValveResourceFormat/ValvePak/actions" title="Build Status"><img alt="Build Status" src="https://img.shields.io/github/actions/workflow/status/ValveResourceFormat/ValvePak/ci.yml?logo=github&label=Build&logoColor=ffffff&style=for-the-badge&branch=master"></a>
    <a href="https://www.nuget.org/packages/ValvePak/" title="NuGet"><img alt="NuGet" src="https://img.shields.io/nuget/v/ValvePak.svg?logo=nuget&label=NuGet&logoColor=ffffff&color=004880&style=for-the-badge"></a>
    <a href="https://app.codecov.io/gh/ValveResourceFormat/ValvePak" title="Code Coverage"><img alt="Code Coverage" src="https://img.shields.io/codecov/c/github/ValveResourceFormat/ValvePak/master?logo=codecov&label=Coverage&logoColor=ffffff&color=F01F7A&style=for-the-badge"></a>
</p>

A .NET library for reading and extracting VPK (Valve Pak) files, the uncompressed archive format used to package game content in Source and Source 2 engine games.

## Usage

```csharp
using var package = new Package();

// Open a vpk file
package.Read("pak01_dir.vpk");

// Can also pass in a stream
package.Read(File.OpenRead("pak01_dir.vpk"));

// Optionally verify hashes and signatures of the file if there are any
package.VerifyHashes();

// Find a file, this returns a PackageEntry
var file = package.FindEntry("path/to/file.txt");

if (file != null) {
	// Read a file to a byte array
	package.ReadEntry(file, out byte[] fileContents);

	// Inspect entry metadata
	Console.WriteLine(file.GetFullPath());  // "path/to/file.txt"
	Console.WriteLine(file.GetFileName());  // "file.txt"
	Console.WriteLine(file.TotalLength);    // file size in bytes
	Console.WriteLine(file.CRC32);          // CRC32 checksum
}
```

Do note that files such as `pak01_001.vpk` are just data files, you have to open `pak01_dir.vpk`.

## Extract all files

```csharp
using var package = new Package();
package.Read("pak01_dir.vpk");

foreach (var group in package.Entries)
{
	foreach (var entry in group.Value)
	{
		var filePath = entry.GetFullPath();

		package.ReadEntry(entry, out byte[] data);

		// Create the directory if needed, then write the file
		Directory.CreateDirectory(Path.GetDirectoryName(filePath));
		File.WriteAllBytes(filePath, data);
	}
}
```

## Create a VPK

```csharp
using var package = new Package();

// Add files to the package
package.AddFile("path/to/file.txt", File.ReadAllBytes("file.txt"));
package.AddFile("models/example.vmdl", File.ReadAllBytes("example.vmdl"));

// Remove a file from the package
package.RemoveFile(package.FindEntry("path/to/file.txt"));

// Write the package to disk
package.Write("pak01_dir.vpk");
```

## Optimize for many lookups

By default, `FindEntry` performs a linear scan. If you need to look up many files, call `OptimizeEntriesForBinarySearch()` before `Read()` to sort entries and use binary search instead. You can also pass `StringComparison.OrdinalIgnoreCase` for case-insensitive lookups.

```csharp
using var package = new Package();

// Call before Read() to enable binary search for FindEntry
package.OptimizeEntriesForBinarySearch();
package.Read("pak01_dir.vpk");

// FindEntry calls are now significantly faster
var file = package.FindEntry("path/to/file.txt");
```

## Read into a user-provided buffer

```csharp
var entry = package.FindEntry("path/to/file.txt");

// Allocate your own buffer (must be at least entry.TotalLength bytes)
var buffer = new byte[entry.TotalLength];
package.ReadEntry(entry, buffer, validateCrc: true);
```

Using `ArrayPool` to avoid allocations when reading many files:

```csharp
var entry = package.FindEntry("path/to/file.txt");

var buffer = ArrayPool<byte>.Shared.Rent((int)entry.TotalLength);

try
{
	package.ReadEntry(entry, buffer, validateCrc: true);

	// Use buffer[..entry.TotalLength] here
}
finally
{
	ArrayPool<byte>.Shared.Return(buffer);
}
```

## Stream-based access

`GetMemoryMappedStreamIfPossible` returns a memory-mapped stream for large files (over 4 KiB) and a `MemoryStream` for smaller ones. This avoids reading the entire file into a byte array.

```csharp
var entry = package.FindEntry("path/to/file.txt");

using var stream = package.GetMemoryMappedStreamIfPossible(entry);
```

## Verification

```csharp
using var package = new Package();
package.Read("pak01_dir.vpk");

// Verify MD5 hashes of the directory tree and whole file
package.VerifyHashes();

// Verify MD5/Blake3 hashes of individual chunk files (pak01_000.vpk, pak01_001.vpk, ...)
package.VerifyChunkHashes();

// Verify CRC32 checksums of every file in the package
package.VerifyFileChecksums();

// Verify the RSA signature if the package is signed
bool valid = package.IsSignatureValid();
```
