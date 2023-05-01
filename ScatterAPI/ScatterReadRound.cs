using System.Runtime.InteropServices;

namespace VmmFrost.ScatterAPI
{
    /// <summary>
    /// Defines a Scatter Read Round. Each round will execute a single scatter read. If you have reads that
    /// are dependent on previous reads (chained pointers for example), you may need multiple rounds.
    /// </summary>
    public class ScatterReadRound
    {
        private readonly bool _useCache;
        protected Dictionary<int, Dictionary<int, IScatterEntry>> Results { get; }
        protected List<IScatterEntry> Entries { get; } = new();
        public ScatterReadRound(Dictionary<int, Dictionary<int, IScatterEntry>> results, bool useCache)
        {
            Results = results;
            _useCache = useCache;
        }

        /// <summary>
        /// (Base)
        /// Adds a single Scatter Read 
        /// </summary>
        /// <param name="index">For loop index this is associated with.</param>
        /// <param name="id">Random ID number to identify the entry's purpose.</param>
        /// <param name="addr">Address to read from (you can pass a ScatterReadEntry from an earlier round, 
        /// and it will use the result).</param>
        /// <param name="size">Size of oject to read (ONLY for reference types, value types get size from
        /// Type). You canc pass a ScatterReadEntry from an earlier round and it will use the Result.</param>
        /// <param name="offset">Optional offset to add to address (usually in the event that you pass a
        /// ScatterReadEntry to the Addr field).</param>
        /// <returns></returns>
        public virtual ScatterReadEntry<T> AddEntry<T>(int index, int id, object addr, object size = null, uint offset = 0x0)
        {
            var entry = new ScatterReadEntry<T>()
            {
                Index = index,
                Id = id,
                Addr = addr,
                Size = size,
                Offset = offset
            };
            Results[index].Add(id, entry);
            Entries.Add(entry);
            return entry;
        }

        /// <summary>
        /// ** Internal API use only do not use **
        /// </summary>
        internal void Run(MemDMA mem)
        {
            mem.ReadScatter(CollectionsMarshal.AsSpan(Entries), _useCache);
        }
    }
}
