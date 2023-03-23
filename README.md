# Valve Pak (vpk) for .NET

[![Build Status (GitHub)](https://img.shields.io/github/actions/workflow/status/SteamDatabase/ValvePak/ci.yml?label=Build&style=flat-square&branch=master)](https://github.com/SteamDatabase/ValvePak/actions)
[![NuGet](https://img.shields.io/nuget/v/ValvePak.svg?label=NuGet&style=flat-square)](https://www.nuget.org/packages/ValvePak/)
[![Coverage Status](https://img.shields.io/codecov/c/github/SteamDatabase/ValvePak/master?label=Coverage&style=flat-square)](https://app.codecov.io/gh/SteamDatabase/ValvePak)

VPK (Valve Pak) files are uncompressed archives used to package game content.
This library allows you to read and extract files out of these paks.

Usage:

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
}
```

Do note that files such as `pak01_001.vpk` are just data files, you have to open `pak01_dir.vpk`.
