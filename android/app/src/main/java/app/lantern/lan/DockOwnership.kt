package app.lantern.lan

/** Identity-scoped lifecycle: queued work from an old session cannot revoke a replacement. */
internal class DockOwnerSlot<T:AutoCloseable> {
    @Volatile private var value:T?=null
    fun current()=value
    @Synchronized fun replace(create:()->T):T {
        val previous=value;value=null;runCatching{previous?.close()}
        return create().also{value=it}
    }
    @Synchronized fun stop(expected:T):Boolean {
        if(value!==expected) return false
        value=null;runCatching{expected.close()};return true
    }
}
