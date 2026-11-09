package app.lantern.lan

import java.io.InputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.SynchronousQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

internal object DockPolicy {
    fun token(value: String?) = value != null && value.matches(Regex("[0-9a-f]{64}"))
    fun id(value: String) = value.matches(Regex("[0-9a-f]{32}"))
    fun ownerRequest(shareTarget:Boolean,requested:String?) = if(shareTarget) null else requested
    fun acknowledgesGesture(currentId:String,currentRevision:Long,id:String?,revision:Long?) =
        id!=null && revision!=null && currentId==id && currentRevision==revision
    fun selectedFileMime(value:String?):String {
        val safe=value?.trim()?.takeIf{it.length in 1..120 && it.none(Char::isISOControl)} ?: "application/octet-stream"
        // Only a system-tree selection can mint the folder capability. A
        // provider describing an ordinary ZIP must remain an ordinary file.
        return if(safe.equals(DockFolderArchive.MIME,ignoreCase=true)) "application/zip" else safe
    }
    fun quote(value: String) = buildString {
        append('"'); value.forEach { c -> when(c) {
            '"' -> append("\\\""); '\\' -> append("\\\\"); '\n' -> append("\\n"); '\r' -> append("\\r"); '\t' -> append("\\t")
            else -> if(c.code < 32 || c in '\uD800'..'\uDFFF') append("\\u%04x".format(c.code)) else append(c)
        } }; append('"')
    }
}

internal data class DockExport(val id: String, val name: String, val mime: String, val size: Long,
    val modified: Long, val thumbnail: ByteArray?, val open: () -> InputStream,
    val verify: () -> Boolean)

/** ADB-forwarded loopback only. No CORS, path/URI API, cookies, or network file callbacks. */
internal class DockWire(
    secret: String, private val valid: () -> Boolean, private val touch: () -> Unit,
    private val status: () -> String, private val lookup: (String) -> DockExport?,
    private val stopped: () -> Unit, private val gestureEnd: (String?,Long?) -> Unit, port: Int = 2428
) : AutoCloseable {
    private val key = secret.toByteArray(StandardCharsets.US_ASCII).also { require(DockPolicy.token(secret)) }
    private val server = ServerSocket(port, 4, InetAddress.getByName("127.0.0.1"))
    val port: Int get() = server.localPort
    @Volatile private var active = true
    private val sockets = ConcurrentHashMap.newKeySet<Socket>()
    private val deadlines = ScheduledThreadPoolExecutor(1) { r -> Thread(r,"IPMsg-dock-deadline").apply { isDaemon=true } }.apply { removeOnCancelPolicy=true }
    private val workers = ThreadPoolExecutor(4,4,0,TimeUnit.SECONDS,SynchronousQueue(),
        { r -> Thread(r,"IPMsg-dock-client").apply { isDaemon=true } },ThreadPoolExecutor.AbortPolicy())
    private val accept = Thread({
        while(active) {
            val socket = try { server.accept() } catch(_:Exception) { break }
            if(!active || !socket.inetAddress.isLoopbackAddress) { socket.close(); continue }
            sockets.add(socket)
            try {
                workers.execute {
                    val guard = deadlines.schedule({ runCatching { socket.close() } },10,TimeUnit.SECONDS)
                    try { socket.use { runCatching { serve(it) { guard.cancel(false) } } } }
                    finally { sockets.remove(socket); guard.cancel(false) } }
            } catch(_:Exception) { sockets.remove(socket); runCatching { socket.close() } }
        }
    },"IPMsg-dock-loopback").apply { isDaemon=true; start() }

    private fun serve(socket: Socket, ready: () -> Unit) {
        socket.soTimeout=5_000
        val input=socket.getInputStream().buffered(8192)
        val raw=StringBuilder(); val end=System.nanoTime()+5_000_000_000L
        try {
            while(!raw.endsWith("\r\n\r\n")) {
                require(raw.length<8192 && System.nanoTime()<end)
                val b=input.read(); require(b==9 || b==10 || b==13 || b in 32..126); raw.append(b.toChar())
            }
            val lines=raw.toString().removeSuffix("\r\n\r\n").split("\r\n")
            require(lines.size in 1..40 && lines.first().length<=1024 && lines.all { it.length<=2048 })
            val request=lines.first().split(' '); require(request.size==3 && request[2]=="HTTP/1.1")
            val method=request[0]; val path=request[1]
            require(path.startsWith('/') && path.none { it=='?' || it=='#' || it=='%' } && !path.startsWith("//"))
            val headers=mutableMapOf<String,String>()
            lines.drop(1).forEach { line ->
                require(':' in line && !line.startsWith(' ') && !line.startsWith('\t'))
                val name=line.substringBefore(':').lowercase(); require(name.matches(Regex("[a-z0-9-]+")) && name !in headers)
                headers[name]=line.substringAfter(':').trim()
            }
            require(headers["host"]?.matches(Regex("(?:127\\.0\\.0\\.1|localhost)(?::[0-9]{1,5})?"))==true)
            require("transfer-encoding" !in headers && (headers["content-length"]==null || headers["content-length"]=="0"))
            if("origin" in headers || "referer" in headers) { reply(socket,403); return }
            val auth=headers["authorization"].orEmpty()
            if(!auth.startsWith("Bearer ") || !DockPolicy.token(auth.removePrefix("Bearer ")) ||
                !MessageDigest.isEqual(auth.removePrefix("Bearer ").toByteArray(StandardCharsets.US_ASCII),key)) { reply(socket,401); return }
            if(!active || !valid()) { reply(socket,410); return }
            if(method!="GET" && method!="POST") { reply(socket,405); return }
            touch()
            when {
                method=="GET" && path=="/v1/status" -> { ready(); reply(socket,200,"application/json",status().toByteArray()) }
                method=="POST" && path=="/v1/stop" -> { reply(socket,200,"application/json","{}".toByteArray()); stopped() }
                method=="POST" && path=="/v1/gesture/end" -> {
                    val id=headers["x-ipmsg-gesture-id"];val version=headers["x-ipmsg-gesture-revision"]
                    require((id==null)==(version==null))
                    require(id==null || DockPolicy.id(id))
                    require(version==null || (version.matches(Regex("[0-9]{1,19}")) && version.toLongOrNull()?.let{it>=0}==true))
                    gestureEnd(id,version?.toLong());reply(socket,200,"application/json","{}".toByteArray())
                }
                method=="GET" && (path.startsWith("/v1/file/") || path.startsWith("/v1/thumbnail/")) -> {
                    val id=path.substringAfterLast('/'); if(!DockPolicy.id(id)) { reply(socket,404); return }
                    val item=lookup(id) ?: run { reply(socket,404); return }
                    if(path=="/v1/thumbnail/$id") {
                        val thumb=item.thumbnail
                        if(thumb==null || thumb.size>262144) reply(socket,404) else reply(socket,200,"image/jpeg",thumb)
                    } else if(path=="/v1/file/$id") sendFile(socket,item,ready) else reply(socket,404)
                }
                else -> reply(socket,404)
            }
        } catch(_:IllegalArgumentException) { runCatching { reply(socket,400) } }
    }
    private fun header(code:Int,mime:String,size:Long) = ("HTTP/1.1 $code Response\r\nContent-Type: $mime\r\nContent-Length: $size\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n").toByteArray(StandardCharsets.US_ASCII)
    private fun reply(socket:Socket,code:Int,mime:String="application/json",body:ByteArray=ByteArray(0)) {
        val guard=deadlines.schedule({runCatching{socket.close()}},5,TimeUnit.SECONDS)
        try { socket.getOutputStream().apply { write(header(code,mime,body.size.toLong())); write(body); flush() } }
        finally { guard.cancel(false) }
    }
    private fun sendFile(socket:Socket,item:DockExport,ready:()->Unit) {
        if(!item.verify()) { reply(socket,410); return }
        item.open().use { input ->
            ready()
            val output=socket.getOutputStream().buffered(131072)
            fun write(bytes:ByteArray,count:Int) {
                val guard=deadlines.schedule({runCatching{socket.close()}},10,TimeUnit.SECONDS)
                try { output.write(bytes,0,count); output.flush() } finally { guard.cancel(false) }
            }
            val h=header(200,"application/octet-stream",item.size); write(h,h.size)
            val buffer=ByteArray(131072); var sent=0L
            while(sent<item.size && active && valid()) {
                val count=input.read(buffer,0,minOf(buffer.size.toLong(),item.size-sent).toInt())
                check(count>0)
                // Keep the last chunk until the provider metadata is checked.
                // A changed source must not look like a completed sized body.
                if(sent+count==item.size) check(item.verify())
                write(buffer,count); sent+=count; touch()
            }
            check(sent==item.size)
        }
    }
    override fun close() {
        active=false; runCatching { server.close() }; sockets.forEach { runCatching { it.close() } }; sockets.clear()
        workers.shutdownNow(); deadlines.shutdownNow(); key.fill(0)
    }
}
