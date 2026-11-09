package app.lantern.lan

import java.io.File
import java.io.FileOutputStream
import java.io.FilterOutputStream
import java.io.InputStream
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/** A bounded snapshot of one explicitly chosen SAF tree, never a path browser. */
internal object DockFolderArchive {
    const val MIME="application/vnd.ipmsg.folder+zip"
    const val MAX_ENTRIES=10_000
    const val MAX_DEPTH=32
    const val MAX_BYTES=32L*1024*1024*1024
    const val RESERVE_BYTES=32L*1024*1024
    const val MAX_MILLIS=600_000L
    data class Node(val key:String,val name:String,val directory:Boolean,val size:Long=0,
        val modified:Long=0,val mime:String="",val virtual:Boolean=false)
    interface Source {
        fun metadata(key:String):Node
        fun children(key:String):List<Node>
        fun open(node:Node):InputStream
    }
    data class Result(val name:String,val bytes:Long,val files:Int)
    private data class Entry(val node:Node,val path:String,val children:List<Node>)

    fun component(name:String):Boolean {
        if(name.isBlank() || name.length>240 || name=="." || name==".." || name.last() in " .") return false
        if(name.any {it.isISOControl() || it in "/\\:*?\"<>|"}) return false
        var i=0
        while(i<name.length) {val c=name[i++];if(c.isHighSurrogate()) {if(i==name.length || !name[i++].isLowSurrogate()) return false} else if(c.isLowSurrogate()) return false}
        val stem=name.substringBefore('.').trimEnd(' ').uppercase(Locale.ROOT)
        return stem!="CONIN\$" && stem!="CONOUT\$" && !stem.matches(Regex("(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])"))
    }

    fun create(root:Node,file:File,source:Source,ensure:()->Unit,availableBytes:()->Long,
        now:()->Long={System.nanoTime()/1_000_000}):Result {
        require(root.directory && component(root.name)) {"This folder name cannot be represented on Windows. Rename it and select it again."}
        val until=now()+MAX_MILLIS
        fun checkActive() {ensure();check(now()<until) {"Folder preparation timed out. Select a smaller local folder."}}
        val seen=hashSetOf<String>()
        val plan=ArrayList<Entry>()
        var knownBytes=0L
        fun visit(node:Node,path:String,depth:Int) {
            checkActive()
            require(node.key.isNotEmpty() && node.key.length<=4096 && seen.add(node.key)) {"The folder contains a repeated document or cycle."}
            require(!node.virtual && node.size>=-1 && node.modified>=0) {"The folder contains a virtual or invalid document. Export it as a local file first."}
            require(depth<=MAX_DEPTH && path.length<=2048 && (path.isEmpty() || component(node.name))) {"The folder contains an unsupported name or is too deeply nested."}
            require(plan.size<MAX_ENTRIES+1) {"Choose a folder containing at most $MAX_ENTRIES entries."}
            val children=if(node.directory) source.children(node.key) else emptyList()
            require(children.size<=MAX_ENTRIES) {"Choose a folder containing at most $MAX_ENTRIES entries."}
            val names=hashSetOf<String>()
            children.forEach {require(component(it.name) && names.add(it.name.uppercase(Locale.ROOT))) {"A folder contains an invalid or case-conflicting Windows filename. Rename it first."}}
            plan.add(Entry(node,path,children))
            if(!node.directory && node.size>=0) {require(node.size<=MAX_BYTES-knownBytes) {"A folder snapshot is limited to 32 GB."};knownBytes+=node.size}
            children.forEach {visit(it,if(path.isEmpty()) it.name else "$path/${it.name}",depth+1)}
        }
        visit(root,"",0)
        require(plan.size-1<=MAX_ENTRIES) {"Choose a folder containing at most $MAX_ENTRIES entries."}
        require(source.metadata(root.key)==root) {"The selected folder changed. Select it again."}
        val capacity=minOf(MAX_BYTES,availableBytes()-RESERVE_BYTES)
        require(capacity>=22) {"Free phone storage, then select the folder again."}
        // Level-0 deflate preserves practically all source bytes. Reserve a
        // conservative bound for local/central headers, UTF-8 names, ZIP64 and
        // stored blocks before opening a body. Unknown-size providers remain
        // bounded while copying and cannot receive a complete preflight.
        val overhead=98L+plan.drop(1).sumOf {entry->
            256L+2L*(entry.path+if(entry.node.directory) "/" else "").toByteArray(Charsets.UTF_8).size+
                if(!entry.node.directory && entry.node.size>=0) (entry.node.size/16_383+1)*5 else 0L
        }
        require(knownBytes<=capacity && overhead<=capacity-knownBytes) {"Not enough phone storage to prepare this folder. Free space or choose a smaller folder."}
        var written=0L;var checkedAt=0L;var contentBytes=0L;var files=0
        check(file.createNewFile()) {"Could not create a private folder snapshot."}
        try {
            FileOutputStream(file).use {raw->
                val limited=object:FilterOutputStream(raw) {
                    override fun write(value:Int) {write(byteArrayOf(value.toByte()),0,1)}
                    override fun write(bytes:ByteArray,offset:Int,length:Int) {
                        checkActive();require(length.toLong()<=capacity-written) {"Not enough phone storage for this folder snapshot."}
                        if(written-checkedAt>=8L*1024*1024) {require(availableBytes()>RESERVE_BYTES) {"Free phone storage and select this folder again."};checkedAt=written}
                        out.write(bytes,offset,length);written+=length
                    }
                    override fun close() {flush()}
                }
                ZipOutputStream(limited).use {zip->
                    // Local pictures/videos are usually compressed already. Avoid
                    // another expensive deflate pass while retaining standard ZIP.
                    zip.setLevel(0)
                    val buffer=ByteArray(131072)
                    plan.drop(1).forEach {entry->
                        checkActive();val node=entry.node
                        require(source.metadata(node.key)==node) {"A selected file changed during preparation. Select the folder again."}
                        zip.putNextEntry(ZipEntry(entry.path+if(node.directory) "/" else "").apply {if(node.modified>0) time=node.modified})
                        if(!node.directory) {
                            var size=0L
                            source.open(node).use {input->while(true) {
                                checkActive();val count=input.read(buffer);if(count<0) break;check(count>0) {"The source stopped supplying file data."}
                                require(count.toLong()<=MAX_BYTES-contentBytes) {"A folder snapshot is limited to 32 GB."}
                                size+=count;contentBytes+=count
                                require(node.size<0 || size<=node.size) {"A selected file changed during preparation. Select the folder again."}
                                zip.write(buffer,0,count)
                            }}
                            require(node.size<0 || size==node.size) {"A selected file changed during preparation. Select the folder again."}
                            files++
                        }
                        zip.closeEntry()
                        require(source.metadata(node.key)==node) {"A selected file changed during preparation. Select the folder again."}
                    }
                    // Detect additions, removals and renames throughout the tree
                    // before publishing any completed capability to Windows.
                    plan.filter {it.node.directory}.forEach {entry->
                        checkActive();require(source.metadata(entry.node.key)==entry.node &&
                            source.children(entry.node.key).sortedBy{it.key}==entry.children.sortedBy{it.key}) {"The selected folder changed during preparation. Select it again."}
                    }
                }
                raw.fd.sync()
            }
            checkActive();check(file.length()==written)
            return Result(root.name+".zip",written,files)
        } catch(error:Throwable) {file.delete();throw error}
    }
}
