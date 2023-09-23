using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using VmmFrost.ScatterAPI;
using vmmsharp;

namespace VmmFrost
{
    /// <summary>
    /// Base Memory Module.
    /// Can be inherited if you want to make your own implementation.
    /// </summary>
    public class MemDMA : IDisposable
    {
        #region Fields/Properties/Constructor

        private const string MemoryMapFile = "mmap.txt";

        /// <summary>
        /// MemProcFS Vmm Instance
        /// </summary>
        public Vmm HVmm { get; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="args">(Optional) Custom Startup Args. If NULL default FPGA parameters will be used.</param>
        /// <param name="autoMemMap">Automatic Memory Map Generation/Initialization. (Default: True)</param>
        public MemDMA(string[] args = null, bool autoMemMap = true)
        {
            try
            {
                Debug.WriteLine("[DMA] Loading...");
                args ??= new string[] { "-printf", "-v", "-device", "fpga", "-waitinitialize" }; // Default args
                if (autoMemMap && !File.Exists(MemoryMapFile))
                {
                    try
                    {
                        Debug.WriteLine("[DMA] Auto Mem Map");
                        HVmm = new Vmm(args);
                        var map = GetMemMap();
                        File.WriteAllBytes(MemoryMapFile, map);
                    }
                    finally
                    {
                        HVmm?.Dispose(); // Close FPGA Connection after getting map.
                        HVmm = null; // Null Vmm Handle
                    }
                }
                if (autoMemMap) // Append Memory Map Args
                {
                    var mapArgs = new string[] { "-memmap", MemoryMapFile };
                    args = args.Concat(mapArgs).ToArray();
                }
                HVmm = new Vmm(args);
            }
            catch (Exception ex)
            {
                throw new DMAException("[DMA] INIT ERROR", ex);
            }
        }
        #endregion

        #region Mem Startup
        /// <summary>
        /// Generates a Physical Memory Map in ASCII Binary Format.
        /// https://github.com/ufrisk/LeechCore/wiki/Device_FPGA_AMD_Thunderbolt
        /// </summary>
        private byte[] GetMemMap()
        {
            try
            {
                var map = HVmm.Map_GetPhysMem();
                if (map.Length == 0)
                    throw new Exception("VMMDLL_Map_GetPhysMem FAIL!");
                var sb = new StringBuilder();
                int leftLength = map.Max(x => x.pa).ToString("x").Length;
                for (int i = 0; i < map.Length; i++)
                {
                    sb.AppendFormat($"{{0,{-leftLength}}}", map[i].pa.ToString("x"))
                        .Append($" - {(map[i].pa + map[i].cb - 1).ToString("x")}")
                        .AppendLine();
                }
                return Encoding.ASCII.GetBytes(sb.ToString());
            }
            catch (Exception ex)
            {
                throw new DMAException("[DMA] Unable to get Mem Map!", ex);
            }
        }

        /// <summary>
        /// Obtain the PID for a process.
        /// </summary>
        /// <param name="process">Process Name (including file extension, ex: .exe)</param>
        /// <returns>Process ID (PID)</returns>
        public uint GetPid(string process)
        {
            if (!HVmm.PidGetFromName(process, out var pid))
                throw new DMAException("PID Lookup Failed");
            return pid;
        }

        /// <summary>
        /// Obtain the Base Address of a Process Module.
        /// </summary>
        /// <param name="pid">Process ID the Module is contained in.</param>
        /// <param name="module">Module Name (including file extension, ex: .dll)</param>
        /// <returns>Module Base virtual address.</returns>
        public ulong GetModuleBase(uint pid, string module)
        {
            var moduleBase = HVmm.ProcessGetModuleBase(pid, module);
            if (moduleBase == 0x0)
                throw new DMAException($"Unable to get Module Base for '{module}'");
            return moduleBase;
        }
        #endregion

        #region ScatterRead
        /// <summary>
        /// (Base)
        /// Performs multiple reads in one sequence, significantly faster than single reads.
        /// Designed to run without throwing unhandled exceptions, which will ensure the maximum amount of
        /// reads are completed OK even if a couple fail.
        /// </summary>
        /// <param name="pid">Process ID to read from.</param>
        /// <param name="entries">Scatter Read Entries to read from for this round.</param>
        /// <param name="useCache">Use caching for this read (recommended).</param>
        internal virtual void ReadScatter(uint pid, ReadOnlySpan<IScatterEntry> entries, bool useCache = true)
        {
            var pagesToRead = new HashSet<ulong>(); // Will contain each unique page only once to prevent reading the same page multiple times
            foreach (var entry in entries) // First loop through all entries - GET INFO
            {
                // Parse Address and Size properties
                ulong addr = entry.ParseAddr();
                uint size = (uint)entry.ParseSize();

                // INTEGRITY CHECK - Make sure the read is valid
                if (addr == 0x0 || size == 0)
                {
                    entry.IsFailed = true;
                    continue;
                }
                // location of object
                ulong readAddress = addr + entry.Offset;
                // get the number of pages
                uint numPages = ADDRESS_AND_SIZE_TO_SPAN_PAGES(readAddress, size);
                ulong basePage = PAGE_ALIGN(readAddress);

                //loop all the pages we would need
                for (int p = 0; p < numPages; p++)
                {
                    ulong page = basePage + PAGE_SIZE * (uint)p;
                    pagesToRead.Add(page);
                }
            }
            uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
            var scatters = HVmm.MemReadScatter(pid, flags, pagesToRead.ToArray()); // execute scatter read

            foreach (var entry in entries) // Second loop through all entries - PARSE RESULTS
            {
                if (entry.IsFailed) // Skip this entry, leaves result as null
                    continue;

                ulong readAddress = (ulong)entry.Addr + entry.Offset; // location of object
                uint pageOffset = BYTE_OFFSET(readAddress); // Get object offset from the page start address

                uint size = (uint)(int)entry.Size;
                var buffer = new byte[size]; // Alloc result buffer on heap
                int bytesCopied = 0; // track number of bytes copied to ensure nothing is missed
                uint cb = Math.Min(size, (uint)PAGE_SIZE - pageOffset); // bytes to read this page

                uint numPages = ADDRESS_AND_SIZE_TO_SPAN_PAGES(readAddress, size); // number of pages to read from (in case result spans multiple pages)
                ulong basePage = PAGE_ALIGN(readAddress);

                for (int p = 0; p < numPages; p++)
                {
                    ulong page = basePage + PAGE_SIZE * (uint)p; // get current page addr
                    var scatter = scatters.FirstOrDefault(x => x.qwA == page); // retrieve page of mem needed
                    if (scatter.f) // read succeeded -> copy to buffer
                    {
                        scatter.pb
                            .AsSpan((int)pageOffset, (int)cb)
                            .CopyTo(buffer.AsSpan(bytesCopied, (int)cb)); // Copy bytes to buffer
                        bytesCopied += (int)cb;
                    }
                    else // read failed -> set failed flag
                    {
                        entry.IsFailed = true;
                        break;
                    }

                    cb = (uint)PAGE_SIZE; // set bytes to read next page
                    if (bytesCopied + cb > size) // partial chunk last page
                        cb = size - (uint)bytesCopied;

                    pageOffset = 0x0; // Next page (if any) should start at 0x0
                }
                if (bytesCopied != size)
                    entry.IsFailed = true;
                entry.SetResult(buffer);
            }
        }
        #endregion

        #region ReadMethods

        /// <summary>
        /// (Base)
        /// Read memory into a buffer.
        /// </summary>
        /// <param name="pid">Process ID to read from.</param>
        /// <param name="addr">Virtual Address to read from.</param>
        /// <param name="size">Size (bytes) of this read.</param>
        /// <param name="useCache">Use caching for this read (recommended).</param>
        /// <returns>Byte Array containing memory read.</returns>
        /// <exception cref="DMAException"></exception>
        public virtual byte[] ReadBuffer(uint pid, ulong addr, int size, bool useCache = true)
        {
            try
            {
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                var buf = HVmm.MemRead(pid, addr, (uint)size, flags);
                if (buf.Length != size) 
                    throw new DMAException("Incomplete memory read!");
                return buf;
            }
            catch (Exception ex)
            {
                throw new DMAException($"[DMA] ERROR reading buffer at 0x{addr.ToString("X")}", ex);
            }
        }

        /// <summary>
        /// (Base)
        /// Read a chain of pointers and get the final result.
        /// </summary>
        /// <param name="pid">Process ID to read from.</param>
        /// <param name="addr">Virtual Address to read from.</param>
        /// <param name="offsets">Offsets to read in a chain.</param>
        /// <param name="useCache">Use caching for this read (recommended).</param>
        /// <returns>Virtual address of the Pointer Result.</returns>
        /// <exception cref="DMAException"></exception>
        public virtual ulong ReadPtrChain(uint pid, ulong addr, uint[] offsets, bool useCache = true)
        {
            ulong ptr = addr; // push ptr to first address value
            for (int i = 0; i < offsets.Length; i++)
            {
                try
                {
                    ptr = ReadPtr(pid, ptr + offsets[i], useCache);
                }
                catch (Exception ex)
                {
                    throw new DMAException($"[DMA] ERROR reading pointer chain at index {i}, addr 0x{ptr.ToString("X")} + 0x{offsets[i].ToString("X")}", ex);
                }
            }
            return ptr;
        }

        /// <summary>
        /// (Base)
        /// Resolves a pointer and returns the memory address it points to.
        /// </summary>
        /// <param name="pid">Process ID to read from.</param>
        /// <param name="addr">Virtual Address to read from.</param>
        /// <param name="useCache">Use caching for this read (recommended).</param>
        /// <returns>Virtual address of the Pointer Result.</returns>
        /// <exception cref="DMAException"></exception>
        public virtual ulong ReadPtr(uint pid, ulong addr, bool useCache = true)
        {
            try
            {
                var ptr = ReadValue<MemPointer>(pid, addr, useCache);
                ptr.Validate();
                return ptr;
            }
            catch (Exception ex)
            {
                throw new DMAException($"[DMA] ERROR reading pointer at 0x{addr.ToString("X")}", ex);
            }
        }

        /// <summary>
        /// (Base)
        /// Read value type/struct from specified address.
        /// </summary>
        /// <typeparam name="T">Value Type to read.</typeparam>
        /// <param name="pid">Process ID to read from.</param>
        /// <param name="addr">Virtual Address to read from.</param>
        /// <param name="useCache">Use caching for this read (recommended).</param>
        /// <returns>Value Type of <typeparamref name="T"/></returns>
        /// <exception cref="DMAException"></exception>
        public virtual T ReadValue<T>(uint pid, ulong addr, bool useCache = true)
            where T : unmanaged
        {
            try
            {
                int size = Unsafe.SizeOf<T>();
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                var buf = HVmm.MemRead(pid, addr, (uint)size, flags);
                if (buf.Length != size)
                    throw new Exception("Incomplete Memory Read!");
                return Unsafe.As<byte, T>(ref buf[0]);
            }
            catch (Exception ex)
            {
                throw new DMAException($"[DMA] ERROR reading {typeof(T)} value at 0x{addr.ToString("X")}", ex);
            }
        }

        /// <summary>
        /// (Base)
        /// Read null terminated string (UTF-8).
        /// </summary>
        /// <param name="pid">Process ID to read from.</param>
        /// <param name="addr">Virtual Address to read from.</param>
        /// <param name="size">Size (bytes) of this read.</param>
        /// <param name="useCache">Use caching for this read (recommended).</param>
        /// <returns>UTF-8 Encoded String</returns>
        /// <exception cref="DMAException"></exception>
        public virtual string ReadString(uint pid, ulong addr, uint size, bool useCache = true) // read n bytes (string)
        {
            try
            {
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                var buf = HVmm.MemRead(pid, addr, size, flags);
                return Encoding.UTF8.GetString(buf).Split('\0')[0];
            }
            catch (Exception ex)
            {
                throw new DMAException($"[DMA] ERROR reading string at 0x{addr.ToString("X")}", ex);
            }
        }
        #endregion

        #region WriteMethods

        /// <summary>
        /// (Base)
        /// Write value type/struct to specified address.
        /// </summary>
        /// <typeparam name="T">Value Type to write.</typeparam>
        /// <param name="pid">Process ID to write to.</param>
        /// <param name="addr">Virtual Address to write to.</param>
        /// <param name="value"></param>
        /// <exception cref="DMAException"></exception>
        public virtual void WriteValue<T>(uint pid, ulong addr, T value)
            where T : unmanaged
        {
            try
            {
                var data = new byte[Unsafe.SizeOf<T>()];
                MemoryMarshal.Write(data, ref value);
                if (!HVmm.MemWrite(pid, addr, data))
                    throw new Exception("Memory Write Failed!");
            }
            catch (Exception ex)
            {
                throw new DMAException($"[DMA] ERROR writing {typeof(T)} value at 0x{addr.ToString("X")}", ex);
            }
        }

        /// <summary>
        /// (Base)
        /// Perform a Scatter Write Operation.
        /// </summary>
        /// <param name="pid">Process ID to write to.</param>
        /// <param name="entries">Scatter Write Entries to write.</param>
        /// <exception cref="DMAException"></exception>
        public virtual void WriteScatter(uint pid, params ScatterWriteEntry[] entries)
        {
            try
            {
                using var hScatter = HVmm.Scatter_Initialize(pid, Vmm.FLAG_NOCACHE);
                foreach (var entry in entries)
                {
                    if (!hScatter.PrepareWrite(entry.Va, entry.Value))
                        throw new DMAException($"ERROR preparing Scatter Write for entry 0x{entry.Va.ToString("X")}");
                }
                if (!hScatter.Execute())
                    throw new DMAException("Scatter Write Failed!");
            }
            catch (Exception ex)
            {
                throw new DMAException($"[DMA] ERROR executing Scatter Write!", ex);
            }
        }
        #endregion

        #region IDisposable
        private readonly object _disposeSync = new();
        private bool _disposed = false;
        /// <summary>
        /// Closes the FPGA Connection and cleans up native resources.
        /// </summary>
        public void Dispose() => Dispose(true); // Public Dispose Pattern

        protected virtual void Dispose(bool disposing)
        {
            lock (_disposeSync)
            {
                if (!_disposed)
                {
                    if (disposing)
                    {
                        HVmm.Dispose();
                    }
                    _disposed = true;
                }
            }
        }
        #endregion

        #region Memory Macros
        /// Mem Align Functions Ported from Win32 (C Macros)
        protected const ulong PAGE_SIZE = 0x1000;
        private const int PAGE_SHIFT = 12;

        /// <summary>
        /// The PAGE_ALIGN macro takes a virtual address and returns a page-aligned
        /// virtual address for that page.
        /// </summary>
        /// <param name="va">Virtual address to check.</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static ulong PAGE_ALIGN(ulong va)
        {
            return (va & ~(PAGE_SIZE - 1));
        }

        /// <summary>
        /// The ADDRESS_AND_SIZE_TO_SPAN_PAGES macro takes a virtual address and size and returns the number of pages spanned by the size.
        /// </summary>
        /// <param name="va">Virtual Address to check.</param>
        /// <param name="size">Size of Memory Chunk spanned by this virtual address.</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static uint ADDRESS_AND_SIZE_TO_SPAN_PAGES(ulong va, uint size)
        {
            return (uint)((BYTE_OFFSET(va) + (size) + (PAGE_SIZE - 1)) >> PAGE_SHIFT);
        }

        /// <summary>
        /// The BYTE_OFFSET macro takes a virtual address and returns the byte offset
        /// of that address within the page.
        /// </summary>
        /// <param name="va">Virtual Address to get the byte offset of.</param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static uint BYTE_OFFSET(ulong va)
        {
            return (uint)(va & (PAGE_SIZE - 1));
        }
        #endregion
    }
}

    #region Exceptions
    public sealed class DMAException : Exception
    {
        public DMAException()
        {
        }

        public DMAException(string message)
            : base(message)
        {
        }

        public DMAException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public sealed class NullPtrException : Exception
    {
        public NullPtrException()
        {
        }

        public NullPtrException(string message)
            : base(message)
        {
        }

        public NullPtrException(string message, Exception inner)
            : base(message, inner)
        {
        }
        #endregion
}
