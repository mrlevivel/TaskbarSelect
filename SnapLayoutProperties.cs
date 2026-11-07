using System;
namespace TaskbarSelect
{
    // GetProp documents its return value, not a LastError discriminator:
    // NULL is absent. Write authority remains SetProp success plus fresh,
    // positive exact readback; stale native errors never create authority.
    internal sealed class SnapLayoutProperties
    {
        readonly Func<IntPtr, string, IntPtr> get;
        readonly Func<IntPtr, string, IntPtr, bool> set;
        readonly Func<IntPtr, string, IntPtr> remove;
        internal SnapLayoutProperties(Func<IntPtr, string, IntPtr> get,
            Func<IntPtr, string, IntPtr, bool> set, Func<IntPtr, string, IntPtr> remove)
        { if (get == null || set == null || remove == null) throw new ArgumentNullException("property backend"); this.get = get; this.set = set; this.remove = remove; }
        internal long Read(IntPtr window, string name) { return get(window, name).ToInt64(); }
        internal bool Mark(IntPtr window, string name, long nonce)
        {
            if (nonce <= 0 || Read(window, name) != 0) return false;
            return set(window, name, new IntPtr(nonce)) && Read(window, name) == nonce;
        }
        internal bool Release(IntPtr window, string name, long nonce)
        {
            if (nonce <= 0 || Read(window, name) != nonce) return false;
            return remove(window, name) == new IntPtr(nonce);
        }
    }
}
