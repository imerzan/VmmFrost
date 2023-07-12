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
        /// (Base)
        /// Currently Set Process ID (PID).
        /// </summary>
        protected uint PID;
        /// <summary>
        /// (Base)
        /// Currently Set Module Base Virtual Address.
        /// </summary>
        protected ulong ModuleBase;

        /// <summary>
        /// (Base)
        /// MemProcFS Vmm Instance
        /// </summary>
        protected Vmm HVmm { get; }

        /// <summary>
        /// (Base)
        /// Constructor.
        /// </summary>
        /// <param name="args">(Optional) Custom Startup Args. If NULL default FPGA parameters will be used.</param>
        /// <param name="autoMemMap">Automatic Memory Map Generation/Initialization. (Default: True)</param>
        public MemDMA(string[] args = null, bool autoMemMap = true)
        {
            try
            {
                Debug.WriteLine("Loading memory module...");
                args ??= new string[] { "-printf", "-v", "-device", "fpga", "-waitinitialize" }; // Default args
                if (autoMemMap)
                {
                    Debug.WriteLine("[DMA] Auto Mem Map");
                    /// Check for Existing MemMap
                    if (!File.Exists(MemoryMapFile))
                    {
                        try
                        {
                            Debug.WriteLine("[DMA] Generating Mem Map...");
                            try // Init for Memory Map Generation
                            {
                                HVmm = new Vmm(args);
                            }
                            catch (Exception ex)
                            {
                                throw new DMAException("Vmm Init [FAIL]", ex);
                            }
                            GetMemMap();
                        }
                        finally
                        {
                            HVmm?.Dispose(); // Close FPGA Connection after getting map.
                            HVmm = null; // Null Vmm Handle
                        }
                    }
                    /// Append Memory Map Args
                    var mapArgs = new string[] { "-memmap", MemoryMapFile };
                    args = args.Concat(mapArgs).ToArray();
                }
                try // Final Init
                {
                    HVmm = new Vmm(args);
                }
                catch (Exception ex)
                {
                    throw new DMAException("Vmm Init [FAIL]", ex);
                }
            }
            catch (Exception ex)
            {
                throw new DMAException("[DMA] INIT ERROR", ex);
            }
        }
        #endregion

        #region Mem Startup
        /// <summary>
        /// Generates a Physical Memory Map (mmap.txt) to enhance performance/safety.
        /// https://github.com/ufrisk/LeechCore/wiki/Device_FPGA_AMD_Thunderbolt
        /// </summary>
        private void GetMemMap()
        {
            try
            {
                var map = HVmm.Map_GetPhysMem();
                if (map.Length == 0) 
                    throw new Exception("Map_GetPhysMem() returned no entries!");
                var sb = new StringBuilder();
                sb.AppendFormat("{0,4}", "#")
                    .Append(' ') // Spacer [1]
                    .AppendFormat("{0,16}", "Base")
                    .Append("   ") // Spacer [3]
                    .AppendFormat("{0,16}", "Top")
                    .AppendLine();
                sb.AppendLine("-----------------------------------------");
                for (int i = 0; i < map.Length; i++)
                {
                    sb.AppendFormat("{0,4}", $"{i.ToString("D4")}")
                        .Append(' ') // Spacer [1]
                        .AppendFormat("{0,16}", $"{map[i].pa.ToString("x")}")
                        .Append(" - ") // Spacer [3]
                        .AppendFormat("{0,16}", $"{(map[i].pa + map[i].cb - 1).ToString("x")}")
                        .AppendLine();
                }
                File.WriteAllText(MemoryMapFile, sb.ToString());
            }
            catch (Exception ex)
            {
                throw new DMAException("Failed to generate Mem Map!", ex);
            }
        }

        /// <summary>
        /// (Base)
        /// Obtain the PID for a process.
        /// </summary>
        /// <param name="process">Process Name (including file extension, ex: .exe)</param>
        /// <returns>True if successful, otherwise False.</returns>
        public virtual bool GetPid(string process)
        {
            try
            {
                if (!HVmm.PidGetFromName(process, out PID))
                    throw new DMAException("PID Lookup Failed");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DMA] Unable to get PID for {process}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// (Base)
        /// Obtain the Base Address of a Process Module.
        /// </summary>
        /// <param name="module">Module Name (including file extension, ex: .dll)</param>
        /// <returns>True if successful, otherwise False.</returns>
        public virtual bool GetModuleBase(string module)
        {
            try
            {
                ModuleBase = HVmm.ProcessGetModuleBase(PID, module);
                if (ModuleBase == 0x0)
                    throw new DMAException("Module Lookup Failed");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DMA] Unable to get Module Base for {module}: {ex}");
                return false;
            }
        }
        #endregion

        #region ScatterRead
        /// <summary>
        /// (Base)
        /// Performs multiple reads in one sequence, significantly faster than single reads.
        /// Designed to run without throwing unhandled exceptions, which will ensure the maximum amount of
        /// reads are completed OK even if a couple fail.
        /// </summary>
        public virtual void ReadScatter(ReadOnlySpan<IScatterEntry> entries, bool useCache = true)
        {
            var pagesToRead = new HashSet<ulong>(); // Will contain each unique page only once to prevent reading the same page multiple times
            foreach (var entry in entries) // First loop through all entries - GET INFO
            {
                // Parse Address and Size properties
                ulong addr = entry.ParseAddr();
                uint size = (uint)entry.ParseSize();

                // INTEGRITY CHECK - Make sure the read is valid and within range
                if (addr == 0x0 || size == 0 || size > (PAGE_SIZE * 10))
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
            var scatters = HVmm.MemReadScatter(PID, flags, pagesToRead.ToArray()); // execute scatter read

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
        public virtual byte[] ReadBuffer(ulong addr, int size, bool useCache = true)
        {
            try
            {
                if ((uint)size > PAGE_SIZE * 1500) 
                    throw new DMAException("Buffer length outside expected bounds!");
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                var buf = HVmm.MemRead(PID, addr, (uint)size, flags);
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
        public virtual ulong ReadPtrChain(ulong addr, uint[] offsets, bool useCache = true)
        {
            ulong ptr = addr; // push ptr to first address value
            for (int i = 0; i < offsets.Length; i++)
            {
                try
                {
                    ptr = ReadPtr(ptr + offsets[i], useCache);
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
        public virtual ulong ReadPtr(ulong addr, bool useCache = true)
        {
            try
            {
                var ptr = ReadValue<MemPointer>(addr, useCache);
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
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to read from.</param>
        public virtual T ReadValue<T>(ulong addr, bool useCache = true)
            where T : struct
        {
            try
            {
                int size = Marshal.SizeOf<T>();
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                var buf = HVmm.MemRead(PID, addr, (uint)size, flags);
                return MemoryMarshal.Read<T>(buf);
            }
            catch (Exception ex)
            {
                throw new DMAException($"[DMA] ERROR reading {typeof(T)} value at 0x{addr.ToString("X")}", ex);
            }
        }

        /// <summary>
        /// (Base)
        /// Read null terminated string (utf-8/default).
        /// </summary>
        /// <param name="length">Number of bytes to read.</param>
        public virtual string ReadString(ulong addr, uint length, bool useCache = true) // read n bytes (string)
        {
            try
            {
                if (length > PAGE_SIZE) 
                    throw new DMAException("String length outside expected bounds!");
                uint flags = useCache ? 0 : Vmm.FLAG_NOCACHE;
                var buf = HVmm.MemRead(PID, addr, length, flags);
                return Encoding.Default.GetString(buf).Split('\0')[0];
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
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to write to.</param>
        /// <param name="value">Value to write.</param>
        public virtual void WriteValue<T>(ulong addr, T value)
            where T : struct
        {
            try
            {
                var data = new byte[Marshal.SizeOf<T>()];
                MemoryMarshal.Write(data, ref value);
                if (!HVmm.MemWrite(PID, addr, data))
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
        /// <param name="entries">Write entries.</param>
        public virtual void WriteScatter(params ScatterWriteEntry[] entries)
        {
            try
            {
                using var hScatter = HVmm.Scatter_Initialize(PID, Vmm.FLAG_NOCACHE);
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
        protected const int PAGE_SHIFT = 12;

        /// <summary>
        /// The PAGE_ALIGN macro takes a virtual address and returns a page-aligned
        /// virtual address for that page.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static ulong PAGE_ALIGN(ulong va)
        {
            return (va & ~(PAGE_SIZE - 1));
        }
        /// <summary>
        /// The ADDRESS_AND_SIZE_TO_SPAN_PAGES macro takes a virtual address and size and returns the number of pages spanned by the size.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static uint ADDRESS_AND_SIZE_TO_SPAN_PAGES(ulong va, uint size)
        {
            return (uint)((BYTE_OFFSET(va) + (size) + (PAGE_SIZE - 1)) >> PAGE_SHIFT);
        }

        /// <summary>
        /// The BYTE_OFFSET macro takes a virtual address and returns the byte offset
        /// of that address within the page.
        /// </summary>
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
