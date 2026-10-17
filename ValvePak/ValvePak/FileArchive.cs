using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ValvePak
{
	/// <summary>
	/// Archive data in a file, read through its handle and memory mapped on first use.
	/// </summary>
	internal sealed class FileArchive : Archive
	{
		private readonly SafeFileHandle Handle;
		private readonly bool OwnsHandle;
		private MemoryMappedFile? Mapping;
		private MemoryMappedViewAccessor? View;
		private bool IsMapped;
		private object? MappingLock;

		public FileArchive(SafeFileHandle handle, bool ownsHandle, long origin, long dataOffset)
			: base(origin, dataOffset)
		{
			Handle = handle;
			OwnsHandle = ownsHandle;
		}

		public override int Read(long offset, Span<byte> output)
		{
			var totalRead = 0;

			while (totalRead < output.Length)
			{
				var bytesRead = RandomAccess.Read(Handle, output[totalRead..], Origin + offset + totalRead);

				if (bytesRead == 0)
				{
					break;
				}

				totalRead += bytesRead;
			}

			return totalRead;
		}

		public override Stream? OpenMappedStream(long offset, long length)
		{
			var view = GetView();
			var position = Origin + offset;

			if (view == null || position + length > view.Capacity)
			{
				return null;
			}

			return new UnmanagedMemoryStream(view.SafeMemoryMappedViewHandle, view.PointerOffset + position, length);
		}

		/// <summary>
		/// Gets a view of the whole file, mapping it on first use.
		/// </summary>
		/// <returns>The view, or null if the file is empty or the process is 32-bit, where whole archives may not fit in the address space.</returns>
		private MemoryMappedViewAccessor? GetView()
		{
			if (!Environment.Is64BitProcess)
			{
				return null;
			}

			return LazyInitializer.EnsureInitialized(ref View, ref IsMapped, ref MappingLock, CreateView);
		}

		private MemoryMappedViewAccessor? CreateView()
		{
			var length = RandomAccess.GetLength(Handle);

			if (length == 0)
			{
				return null;
			}

			var mapping = MemoryMappedFile.CreateFromFile(Handle, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);

			try
			{
				var view = mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
				Mapping = mapping;

				return view;
			}
			catch
			{
				mapping.Dispose();
				throw;
			}
		}

		/// <summary>
		/// Releases the view and the mapping, and closes the handle if it is owned.
		/// </summary>
		public override void Dispose()
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
