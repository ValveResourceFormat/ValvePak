using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ValvePak
{
	/// <summary>
	/// A file containing archive data of a package, kept open and memory mapped on first use.
	/// </summary>
	internal sealed class ArchiveFile : IDisposable
	{
		private readonly bool OwnsHandle;
		private readonly Lock MappingLock = new();
		private bool IsMapped;
		private MemoryMappedFile? Mapping;

		/// <summary>
		/// Initializes a new instance of the <see cref="ArchiveFile"/> class.
		/// </summary>
		/// <param name="handle">Handle of the file, opened for reading.</param>
		/// <param name="ownsHandle">Whether the handle is closed when this archive is disposed.</param>
		public ArchiveFile(SafeFileHandle handle, bool ownsHandle)
		{
			Handle = handle;
			OwnsHandle = ownsHandle;
		}

		/// <summary>
		/// Gets the file handle.
		/// </summary>
		public SafeFileHandle Handle { get; }

		/// <summary>
		/// Gets the length in bytes of the file at the time it was mapped.
		/// </summary>
		public long MappedLength { get; private set; }

		/// <summary>
		/// Gets the view of the whole mapped file, only created in 64-bit processes.
		/// </summary>
		public MemoryMappedViewAccessor? View { get; private set; }

		/// <summary>
		/// Gets the memory mapping of the file, creating it on first use.
		/// </summary>
		/// <returns>The mapping, or null if the file is empty.</returns>
		public MemoryMappedFile? GetMapping()
		{
			if (!Volatile.Read(ref IsMapped))
			{
				lock (MappingLock)
				{
					if (!IsMapped)
					{
						CreateMapping();
						Volatile.Write(ref IsMapped, true);
					}
				}
			}

			return Mapping;
		}

		private void CreateMapping()
		{
			var length = RandomAccess.GetLength(Handle);

			if (length == 0)
			{
				return;
			}

			var mapping = MemoryMappedFile.CreateFromFile(Handle, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);

			if (Environment.Is64BitProcess)
			{
				try
				{
					View = mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
				}
				catch
				{
					mapping.Dispose();
					throw;
				}
			}

			Mapping = mapping;
			MappedLength = length;
		}

		/// <summary>
		/// Releases the view and the mapping, and closes the handle if it is owned.
		/// Streams created over the view keep it mapped until they are disposed.
		/// </summary>
		public void Dispose()
		{
			View?.Dispose();
			Mapping?.Dispose();

			if (OwnsHandle)
			{
				Handle.Dispose();
			}
		}
	}
}
