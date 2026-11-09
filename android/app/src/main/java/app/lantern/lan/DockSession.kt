package app.lantern.lan

import android.app.KeyguardManager
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.database.Cursor
import android.graphics.Bitmap
import android.net.Uri
import android.os.CancellationSignal
import android.os.Process
import android.os.StatFs
import android.os.SystemClock
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import android.util.Size
import kotlinx.coroutines.flow.MutableStateFlow
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FilterInputStream
import java.io.InputStream
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

internal data class DockUiState(val active:Boolean=false,val expiresAt:Long=0,val items:List<DockExport> = emptyList(),
    val preparing:Boolean=false,val message:String="Connect Phone Dock on your PC to begin.")
internal data class DockGesture(val id:String="",val revision:Long=0,val phase:String="cancel",val itemIds:List<String> = emptyList())

internal object DockSessions {
    val state=MutableStateFlow(DockUiState())
    private val owners=DockOwnerSlot<DockSession>()
    private val session get()=owners.current()
    fun current()=session
    fun active()=session?.valid()==true
    fun matches(token:String?)=token!=null && session?.matches(token)==true && active()
    @Synchronized fun open(context:Context,token:String):Boolean {
        require(DockPolicy.token(token))
        if(matches(token)) return true
        return try { owners.replace{DockSession(context.applicationContext,token)}; true }
        catch(_:Exception) { state.value=DockUiState(message="Could not start Phone Dock. Stop the old session and reconnect."); false }
    }
    @Synchronized fun stop(message:String="Phone Dock stopped.") { session?.let{owners.stop(it)}; state.value=DockUiState(message=message) }
    @Synchronized fun stopIfCurrent(expected:DockSession,message:String="Phone Dock stopped."):Boolean {
        if(!owners.stop(expected)) return false
        state.value=DockUiState(message=message);return true
    }
    fun import(context:Context,uris:List<Uri>,owner:String,lease:(()->Unit)?=null) {
        val current=session
        if(current==null || !current.valid()) { runCatching{lease?.invoke()}; state.value=state.value.copy(message="Connect Phone Dock on your PC first."); return }
        current.import(context,uris,owner,lease)
    }
    fun importFolder(context:Context,uri:Uri,owner:String) {
        val current=session
        if(current==null || !current.valid()) {notice("Connect Phone Dock on your PC first.");return}
        current.import(context,listOf(uri),owner,null,folder=true)
    }
    fun revokeOwner(owner:String) { session?.revokeOwner(owner) }
    fun begin(ids:List<String>) { session?.gesture(ids,"start") }
    fun end(cancel:Boolean=false) { session?.gesture(emptyList(),if(cancel) "cancel" else "end") }
    fun notice(message:String) { state.value=state.value.copy(message=message.take(300)) }
    fun edge(context:Context) { session?.let { context.startActivity(Intent(context,DockEdgeActivity::class.java).putExtra("dock_session",it.ownerToken())) } }
}

internal class DockSession(private val context:Context,private val token:String):AutoCloseable {
    private val resolver=context.contentResolver
    private val keyguard=context.getSystemService(KeyguardManager::class.java)
    @Volatile private var unlocked=!keyguard.isDeviceLocked
    private val born=SystemClock.elapsedRealtime()
    @Volatile private var lastUse=born
    val expiresAt=System.currentTimeMillis()+3_600_000
    @Volatile private var active=true
    private val gate=Any()
    private val entries=linkedMapOf<String,Pair<DockExport,String>>()
    private val leases=mutableMapOf<String,MutableList<()->Unit>>()
    private var revision=0L
    private var gesture=DockGesture()
    private val importing=AtomicBoolean(false)
    private val signals=ConcurrentHashMap<CancellationSignal,String>()
    private val streams=ConcurrentHashMap<InputStream,String>()
    private val cancelledOwners=ConcurrentHashMap.newKeySet<String>()
    private val directory=File(context.cacheDir,"dock-${hex(16)}").apply { check(mkdirs()) }
    private val imports=ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,ArrayBlockingQueue(1),{ r->Thread(r,"IPMsg-dock-import").apply{isDaemon=true} },ThreadPoolExecutor.AbortPolicy())
    private val thumbs=ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,ArrayBlockingQueue(30),{ r->Thread(r,"IPMsg-dock-thumbnails").apply{isDaemon=true} },ThreadPoolExecutor.AbortPolicy())
    private val deadlines=ScheduledThreadPoolExecutor(1) {r->Thread(r,"IPMsg-dock-expiry").apply{isDaemon=true}}.apply{removeOnCancelPolicy=true}
    private val wire=DockWire(token,::valid,{lastUse=SystemClock.elapsedRealtime()},::status,{id->synchronized(gate){entries[id]?.first}},
        {android.os.Handler(context.mainLooper).post{DockSessions.stopIfCurrent(this@DockSession)}},
        {id,version->acknowledgeGesture(id,version)})
    init {
        publish("Choose files, photos or a folder. Only your selections are shared.")
        imports.execute {
            // Process death cannot preserve a capability: remove only this
            // feature's old private snapshots, never arbitrary cache folders.
            context.cacheDir.listFiles()?.filter{it!=directory && it.isDirectory && it.name.matches(Regex("dock-[0-9a-f]{32}"))}?.forEach { old ->
                old.listFiles()?.filter{it.isFile && DockPolicy.id(it.name)}?.forEach{it.delete()};old.delete()
            }
        }
        deadlines.scheduleAtFixedRate({ unlocked=!keyguard.isDeviceLocked; if(!valid()) android.os.Handler(context.mainLooper).post {
            DockSessions.stopIfCurrent(this@DockSession,"Phone Dock expired or the phone was locked.")
        } },1,1,TimeUnit.SECONDS)
    }
    fun ownerToken()=token
    fun matches(value:String)=MessageDigest.isEqual(value.toByteArray(),token.toByteArray())
    fun valid():Boolean = active && unlocked && SystemClock.elapsedRealtime()-born<3_600_000 && SystemClock.elapsedRealtime()-lastUse<900_000
    private fun ensure(owner:String?=null) { check(valid() && (owner==null || owner !in cancelledOwners)) {"Phone Dock access ended"} }
    fun gesture(ids:List<String>,phase:String) = synchronized(gate) {
        if(!valid()) return@synchronized
        if(phase=="start") {
            val known=ids.distinct().filter { it in entries }.take(30)
            if(known.isEmpty()) return@synchronized
            gesture=DockGesture(hex(16),gesture.revision+1,"start",known)
        } else gesture=gesture.copy(revision=gesture.revision+1,phase=phase)
        revision++
    }
    private fun acknowledgeGesture(id:String?,version:Long?)=synchronized(gate) {
        // Old Windows versions send an unscoped acknowledgement. Do not let
        // such a delayed request cancel a newly started handoff.
        if(!valid() || !DockPolicy.acknowledgesGesture(gesture.id,gesture.revision,id,version)) return@synchronized
        gesture=gesture.copy(revision=gesture.revision+1,phase="end");revision++
    }
    private fun status():String = synchronized(gate) {
        val q=DockPolicy::quote
        "{\"version\":1,\"expiresAt\":$expiresAt,\"revision\":$revision,\"items\":["+
            entries.values.joinToString(","){(f,_)->"{\"id\":${q(f.id)},\"name\":${q(f.name)},\"mime\":${q(f.mime)},\"size\":${f.size},\"modified\":${f.modified},\"thumbnail\":${f.thumbnail!=null}}"}+
            "],\"gesture\":{\"id\":${q(gesture.id)},\"revision\":${gesture.revision},\"phase\":${q(gesture.phase)},\"itemIds\":[${gesture.itemIds.joinToString(","){q(it)}}]}}"
    }
    private fun publish(message:String?=null)=synchronized(gate) {
        if(active) DockSessions.state.value=DockUiState(true,expiresAt,entries.values.map{it.first},importing.get(),message ?: DockSessions.state.value.message)
    }
    fun import(activityContext:Context,uris:List<Uri>,owner:String,lease:(()->Unit)?,folder:Boolean=false) {
        val released=AtomicBoolean(false)
        val release={if(released.compareAndSet(false,true)) runCatching{lease?.invoke()};Unit}
        if(!importing.compareAndSet(false,true)) { release(); publish("Finish adding the current selection first."); return }
        try {
            require(uris.isNotEmpty() && uris.size<=30)
            synchronized(gate) {ensure(owner); require(entries.size+uris.distinct().size<=30) {"A dock session holds up to 30 selections. Clear the PC shelf and reconnect to start a new one."}; lease?.let{leases.getOrPut(owner){mutableListOf()}.add(release)} }
            val checked=uris.distinct().onEach { uri ->
                require(uri.scheme=="content" && !uri.authority.isNullOrBlank() && uri.authority?.substringAfterLast('@')!=context.packageName+".files") {"Choose a granted content file"}
                require(activityContext.checkUriPermission(uri,Process.myPid(),Process.myUid(),Intent.FLAG_GRANT_READ_URI_PERMISSION)==PackageManager.PERMISSION_GRANTED) {"The source did not grant file access. Use its Share button."}
                if(folder) require(DocumentsContract.isTreeUri(uri)) {"Choose a folder using the system folder picker."}
            }
            // Extend temporary picker/share grants to the non-exported foreground
            // service. No persistable grants or URIs are written to storage.
            val clip=ClipData.newRawUri("Selected files",checked.first()); checked.drop(1).forEach {clip.addItem(ClipData.Item(it))}
            activityContext.startService(Intent(context,DockBridgeService::class.java).setAction("GRANTS").apply {
                clipData=clip; addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or if(folder) Intent.FLAG_GRANT_PREFIX_URI_PERMISSION else 0)
            })
            publish("Adding your selection…")
            imports.execute {
                var added=0; var failed=0;var failure:String?=null
                try {
                    checked.forEach { uri ->
                        try {
                            ensure(owner)
                            val id=hex(16)
                            val metadata=if(folder) null else metadata(uri,owner)
                            val export=if(folder) folderSnapshot(uri,id,owner) else {
                                val meta=checkNotNull(metadata)
                                if(meta.size>=0 && !meta.virtual) direct(uri,meta,id,owner) else snapshot(uri,meta,id,owner)
                            }
                            try {synchronized(gate) { ensure(owner);entries[id]=export to owner; revision++ }}
                            catch(error:Exception) {File(directory,id).delete();throw error}
                            added++; publish("$added selection(s) ready. Hold a card to drag it to the PC shelf.")
                            if(metadata!=null && (metadata.mime.startsWith("image/") || metadata.mime.startsWith("video/"))) runCatching {
                                thumbs.execute { val thumb=thumbnail(uri); if(thumb!=null && valid()) synchronized(gate) {
                                    val old=entries[id]; if(old!=null) { entries[id]=old.first.copy(thumbnail=thumb) to old.second; revision++; publish() }
                                } }
                            }
                        } catch(error:Exception) { failed++;failure=error.message?.take(220) }
                    }
                } finally {
                    importing.set(false)
                    publish(if(failed>0) "$added selection(s) ready. ${failure ?: "Check the source's access or choose a local copy."}" else "$added selection(s) ready. Hold a card to drag it to the PC shelf.")
                }
            }
        } catch(e:Exception) { importing.set(false); release(); publish(e.message ?: "Cannot add this selection") }
    }
    private data class Metadata(val name:String,val mime:String,val size:Long,val modified:Long,val virtual:Boolean)
    private fun <T> bounded(owner:String?=null,action:(CancellationSignal)->T):T {
        ensure(owner); val signal=CancellationSignal(); signals[signal]=owner.orEmpty()
        val timeout=deadlines.schedule({signal.cancel()},15,TimeUnit.SECONDS)
        try { return action(signal) } finally { timeout.cancel(false); signals.remove(signal) }
    }
    private fun metadata(uri:Uri,owner:String?=null):Metadata = bounded(owner) { signal ->
        resolver.query(uri,null,null,null,null,signal)?.use { cursor ->
            check(cursor.moveToFirst())
            fun text(name:String)=cursor.getColumnIndex(name).takeIf{it>=0}?.let { if(cursor.isNull(it)) null else cursor.getString(it) }
            fun number(name:String)=text(name)?.toLongOrNull()
            val raw=text(OpenableColumns.DISPLAY_NAME).orEmpty().take(240)
            val name=raw.map { if(it.code<32 || it=='/' || it=='\\') '_' else it }.joinToString("").ifBlank{"Selected file"}
            val mime=DockPolicy.selectedFileMime(resolver.getType(uri))
            Metadata(name,mime,number(OpenableColumns.SIZE) ?: -1,number(DocumentsContract.Document.COLUMN_LAST_MODIFIED)?.coerceAtLeast(0) ?: 0,
                ((number(DocumentsContract.Document.COLUMN_FLAGS) ?: 0) and DocumentsContract.Document.FLAG_VIRTUAL_DOCUMENT.toLong())!=0L)
        } ?: error("Source metadata unavailable")
    }
    private fun open(uri:Uri,virtual:Boolean,mime:String,owner:String):InputStream {
        ensure(owner)
        val source=bounded(owner) { signal ->
            val descriptor=if(virtual) resolver.openTypedAssetFileDescriptor(uri,mime,null,signal) else resolver.openAssetFileDescriptor(uri,"r",signal)
            checkNotNull(descriptor)
            try {descriptor.createInputStream()} catch(error:Exception) {descriptor.close();throw error}
        }
        return track(source,owner)
    }
    private fun track(source:InputStream,owner:String):InputStream {
        val tracked=object:FilterInputStream(source) {
            override fun read(b:ByteArray,off:Int,len:Int):Int {
                ensure(owner); val guard=deadlines.schedule({runCatching{close()}},15,TimeUnit.SECONDS)
                try { return super.read(b,off,len) } finally {guard.cancel(false)}
            }
            override fun close() { streams.remove(this); super.close() }
        }
        streams[tracked]=owner; return tracked
    }
    private fun direct(uri:Uri,meta:Metadata,id:String,owner:String):DockExport {
        require(meta.size<=256L*1024*1024*1024)
        // Validate read access without reading/copying the body. Large local
        // videos become ready after metadata + opening one descriptor.
        open(uri,false,meta.mime,owner).close()
        return DockExport(id,meta.name,meta.mime,meta.size,meta.modified,null,
            {open(uri,false,meta.mime,owner)}, {runCatching{ensure(owner); val now=metadata(uri,owner); now.size==meta.size && (meta.modified==0L || now.modified==meta.modified)}.getOrDefault(false)})
    }
    private fun snapshot(uri:Uri,meta:Metadata,id:String,owner:String):DockExport {
        publish("Preparing ${meta.name}…")
        val streamMime=if(meta.virtual) resolver.getStreamTypes(uri,"*/*")?.firstOrNull() ?: error("No readable virtual file") else meta.mime
        require(streamMime.length in 1..120 && streamMime.none(Char::isISOControl)) {"The provider returned an invalid stream type."}
        val mime=DockPolicy.selectedFileMime(streamMime)
        val file=File(directory,id)
        val limit=minOf(32L*1024*1024*1024,StatFs(directory.path).availableBytes-32L*1024*1024).coerceAtLeast(0)
        val until=SystemClock.elapsedRealtime()+300_000
        var size=0L
        try {
            open(uri,meta.virtual,streamMime,owner).use { input -> file.outputStream().use { output ->
                val buffer=ByteArray(131072)
                while(true) {
                    ensure(owner); check(SystemClock.elapsedRealtime()<until) {"Provider preparation timed out"}; val n=input.read(buffer); if(n<0) break; check(n>0)
                    size+=n; require(size<=limit) {"Not enough space to prepare this provider's file"}; output.write(buffer,0,n); lastUse=SystemClock.elapsedRealtime()
                }; output.fd.sync()
            } }
            ensure(owner); check(file.length()==size)
            if(meta.size>=0) check(size==meta.size)
            return DockExport(id,meta.name,mime,size,meta.modified,null,{ensure(owner);track(file.inputStream(),owner)},{valid() && owner !in cancelledOwners && file.isFile && file.length()==size})
        } catch(e:Exception) {file.delete();throw e}
    }
    private fun folderSnapshot(tree:Uri,id:String,owner:String):DockExport {
        val rootId=DocumentsContract.getTreeDocumentId(tree)
        fun uri(key:String)=DocumentsContract.buildDocumentUriUsingTree(tree,key)
        fun read(cursor:Cursor):DockFolderArchive.Node {
            fun text(column:String)=cursor.getColumnIndex(column).takeIf{it>=0}?.let {if(cursor.isNull(it)) null else cursor.getString(it)}
            val key=text(DocumentsContract.Document.COLUMN_DOCUMENT_ID) ?: error("The folder provider did not identify a document.")
            val name=text(DocumentsContract.Document.COLUMN_DISPLAY_NAME) ?: error("The folder provider did not name a document.")
            val mime=text(DocumentsContract.Document.COLUMN_MIME_TYPE) ?: error("The folder provider did not describe a document.")
            require(key.length<=4096 && name.length<=240 && mime.length<=120) {"The folder contains unsupported document metadata."}
            val directory=mime==DocumentsContract.Document.MIME_TYPE_DIR
            return DockFolderArchive.Node(key,name,directory,
                if(directory) 0 else text(DocumentsContract.Document.COLUMN_SIZE)?.toLongOrNull() ?: -1,
                text(DocumentsContract.Document.COLUMN_LAST_MODIFIED)?.toLongOrNull()?.coerceAtLeast(0) ?: 0,mime,
                ((text(DocumentsContract.Document.COLUMN_FLAGS)?.toLongOrNull() ?: 0) and DocumentsContract.Document.FLAG_VIRTUAL_DOCUMENT.toLong())!=0L)
        }
        val projection=arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE,DocumentsContract.Document.COLUMN_SIZE,
            DocumentsContract.Document.COLUMN_LAST_MODIFIED,DocumentsContract.Document.COLUMN_FLAGS)
        val source=object:DockFolderArchive.Source {
            override fun metadata(key:String):DockFolderArchive.Node=bounded(owner) {signal->
                resolver.query(uri(key),projection,null,null,null,signal)?.use {cursor->
                    check(cursor.moveToFirst()) {"A selected document is unavailable. Select the folder again."}
                    read(cursor).also {check(it.key==key && !cursor.moveToNext()) {"The folder provider returned ambiguous document metadata."}}
                } ?: error("The selected folder is unavailable.")
            }
            override fun children(key:String):List<DockFolderArchive.Node> = bounded(owner) {signal->
                resolver.query(DocumentsContract.buildChildDocumentsUriUsingTree(tree,key),projection,null,null,null,signal)?.use {cursor->
                    buildList<DockFolderArchive.Node> {while(cursor.moveToNext()) {
                        ensure(owner);require(size<DockFolderArchive.MAX_ENTRIES) {"Choose a folder containing at most 10000 entries."};add(read(cursor))
                    }}
                } ?: error("Cannot read this folder. Select a local folder with access permission.")
            }
            override fun open(node:DockFolderArchive.Node)=open(uri(node.key),false,node.mime,owner)
        }
        val root=source.metadata(rootId)
        publish("Preparing ${root.name}… Keep this phone unlocked.")
        val file=File(directory,id)
        val result=DockFolderArchive.create(root,file,source,{
            ensure(owner);lastUse=SystemClock.elapsedRealtime()
        },{StatFs(directory.path).availableBytes},{SystemClock.elapsedRealtime()})
        return DockExport(id,result.name,DockFolderArchive.MIME,result.bytes,root.modified,null,
            {ensure(owner);track(file.inputStream(),owner)},
            {valid() && owner !in cancelledOwners && file.isFile && file.length()==result.bytes})
    }
    private fun thumbnail(uri:Uri):ByteArray?=runCatching { bounded {signal ->
        val bitmap=resolver.loadThumbnail(uri,Size(320,320),signal)
        try { ByteArrayOutputStream().use { output -> bitmap.compress(Bitmap.CompressFormat.JPEG,78,output); output.toByteArray().takeIf{it.size<=262144} } }
        finally {bitmap.recycle()}
    } }.getOrNull()
    fun revokeOwner(owner:String) {
        cancelledOwners.add(owner)
        signals.filterValues{it==owner}.keys.forEach{it.cancel()}
        streams.filterValues{it==owner}.keys.forEach{runCatching{it.close()}}
        synchronized(gate) {
            val ids=entries.filterValues{it.second==owner}.keys.toSet(); ids.forEach{entries.remove(it)}; revision++
            if(gesture.itemIds.any{it in ids}) gesture=gesture.copy(revision=gesture.revision+1,phase="cancel",itemIds=emptyList())
            leases.remove(owner)?.forEach {runCatching{it()}}
        }; publish()
    }
    override fun close() {
        if(!active) return; active=false; wire.close()
        signals.keys.forEach {it.cancel()}; streams.keys.forEach{runCatching{it.close()}}; streams.clear()
        imports.shutdownNow(); thumbs.shutdownNow(); deadlines.shutdownNow()
        synchronized(gate) {entries.clear();leases.values.flatten().forEach{runCatching{it()}};leases.clear()}
        directory.listFiles()?.forEach{it.delete()}; directory.delete()
    }
    companion object { private fun hex(bytes:Int)=ByteArray(bytes).also{SecureRandom().nextBytes(it)}.joinToString(""){"%02x".format(it.toInt() and 255)} }
}
