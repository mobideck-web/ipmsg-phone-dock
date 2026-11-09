package app.lantern.lan

import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URI
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

data class CameraRecording(val size:Long,val open:()->java.io.InputStream)

/** A short lived, foreground-only LAN viewer. No remote camera activation or file browsing. */
class LiveCameraServer(
    localIp: String,
    private val peerIp: String,
    private val requestRecording: () -> Unit,
    private val stopRecording: () -> Unit,
    private val recordingStatus: () -> String,
    private val lifetimeMs: Long = 600_000,
    private val tls: javax.net.ssl.SSLContext,
    private val snapshotTaken:()->Unit = {},
    private val savedRecording:()->CameraRecording? = {null}
) : AutoCloseable {
    private val server = (tls.serverSocketFactory.createServerSocket(0,4,InetAddress.getByName(localIp)) as javax.net.ssl.SSLServerSocket).apply {
        enabledProtocols = supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
    }
    private val secret = ByteArray(24).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it.toInt() and 255) }
    val origin = "https://$localIp:${server.localPort}"
    val url = "$origin/#key=$secret"
    private val cookie = ByteArray(24).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it.toInt() and 255) }
    @Volatile private var claimed = false
    @Volatile var frame: ByteArray? = null
    @Volatile private var active = true
    private val audio = AudioBuffer()
    private val audioSockets = ConcurrentHashMap.newKeySet<Socket>()
    @Volatile var audioEnabled: Boolean = false
        @Synchronized set(value) {
            val enabled = value && valid()
            if (field == enabled) return
            field = enabled
            audio.clear()
            audioSockets.forEach { runCatching { it.close() } }
            audioSockets.clear()
        }
    private val started = System.nanoTime()
    private val sockets = ConcurrentHashMap.newKeySet<Socket>()
    private val lastCapture=java.util.concurrent.atomic.AtomicLong(0)
    private val workers = ThreadPoolExecutor(3, 3, 0, TimeUnit.SECONDS, ArrayBlockingQueue(4), { task -> Thread(task, "IPMsg-camera-client").apply { isDaemon = true } }, ThreadPoolExecutor.AbortPolicy())
    private val admission=java.util.concurrent.Semaphore(7)
    private val deadlines=java.util.concurrent.ScheduledThreadPoolExecutor(1) { task->Thread(task,"IPMsg-camera-deadline").apply{isDaemon=true} }.apply{removeOnCancelPolicy=true}
    private val acceptor = Thread({
        while (active) {
            val socket = try { server.accept() } catch (_: Exception) { break }
            if (!valid() || socket.inetAddress.hostAddress != peerIp || !admission.tryAcquire()) { socket.close(); continue }
            sockets.add(socket)
            val timeout=try { deadlines.schedule({runCatching{socket.close()}},10,TimeUnit.SECONDS) }
                catch(_:Exception) {sockets.remove(socket);socket.close();admission.release();continue}
            try {
                workers.execute { try {socket.use { runCatching { serve(it) {timeout.cancel(false)} } }} finally {sockets.remove(socket);timeout.cancel(false);admission.release()} } }
            catch (_: Exception) { timeout.cancel(false);sockets.remove(socket);socket.close();admission.release() }
        }
    }, "IPMsg-camera").apply { isDaemon = true; start() }

    private fun valid() = active && (System.nanoTime() - started) / 1_000_000 < lifetimeMs
    @Synchronized fun publishAudio(pcm: ByteArray) {
        if (audioEnabled && valid() && pcm.isNotEmpty() && pcm.size <= AudioBuffer.MAX_CHUNK_BYTES && pcm.size % 2 == 0) audio.publish(pcm)
    }
    fun stopAudio() { audioEnabled = false }
    private fun serve(socket: Socket, releaseHandshakeDeadline:()->Unit) {
        socket.soTimeout = 3000
        (socket as javax.net.ssl.SSLSocket).startHandshake()
        val input = socket.getInputStream().buffered(8192)
        val header = StringBuilder()
        val deadline = System.nanoTime() + 5_000_000_000L
        while (!header.endsWith("\r\n\r\n")) {
            require(header.length < 8192 && System.nanoTime() < deadline)
            val b = input.read(); require(b >= 0); header.append(b.toChar())
        }
        val lines = header.toString().split("\r\n")
        val request = lines.first().split(' ')
        require(request.size == 3)
        val uri = URI(request[1])
        val headers = lines.drop(1).filter { ':' in it }.associate { it.substringBefore(':').lowercase() to it.substringAfter(':').trim() }
        if (!valid() || headers["host"] != origin.removePrefix("https://") || headers["origin"]?.let { it != origin } == true) { respond(socket,403,"text/plain",ByteArray(0)); return }
        if (request[0] == "GET" && uri.path == "/") { respond(socket,200,"text/html; charset=utf-8",page().toByteArray()); return }
        if (request[0] == "POST" && uri.path == "/auth" && headers["origin"] == origin) {
            val token = headers["x-ipmsg-token"].orEmpty()
            val accepted = synchronized(this) { if (!claimed && MessageDigest.isEqual(token.toByteArray(),secret.toByteArray())) { claimed = true; true } else false }
            if (accepted) respond(socket,200,"text/plain","Connected".toByteArray(),"Set-Cookie: ipmsg=$cookie; Secure; HttpOnly; SameSite=Strict; Path=/; Max-Age=600\r\n")
            else respond(socket,403,"text/plain","Link expired or already used".toByteArray())
            return
        }
        val supplied = headers["cookie"].orEmpty().split(';').map { it.trim() }.firstOrNull { it.startsWith("ipmsg=") }?.substringAfter('=').orEmpty()
        if (!claimed || !MessageDigest.isEqual(supplied.toByteArray(),cookie.toByteArray())) { respond(socket,403,"text/plain","Session unavailable".toByteArray()); return }
        when {
            request[0] == "GET" && uri.path == "/" -> respond(socket, 200, "text/html; charset=utf-8", page().toByteArray())
            request[0] == "GET" && uri.path == "/stream" -> { releaseHandshakeDeadline();stream(socket) }
            request[0] == "GET" && uri.path == "/audio" -> { releaseHandshakeDeadline(); streamAudio(socket) }
            request[0] == "GET" && uri.path == "/audio-state" -> respond(socket,200,"application/json",("{\"enabled\":" + audioEnabled + ",\"epoch\":" + audio.currentEpoch() + ",\"sampleRate\":16000}").toByteArray())
            request[0] == "POST" && uri.path == "/capture" && headers["origin"]==origin -> {
                val now=System.nanoTime();val previous=lastCapture.get()
                if(previous!=0L && now-previous<2_000_000_000L || !lastCapture.compareAndSet(previous,now)){respond(socket,429,"text/plain","Wait before taking another photo".toByteArray());return}
                val image=frame ?: return respond(socket,503,"text/plain","Camera not ready".toByteArray())
                respond(socket,200,"image/jpeg",image,"Content-Disposition: attachment; filename=IPMsg-photo.jpg\r\n");snapshotTaken()
            }
            request[0] == "GET" && uri.path == "/frame" -> frame?.let { respond(socket, 200, "image/jpeg", it) } ?: respond(socket, 503, "text/plain", ByteArray(0))
            request[0] == "GET" && uri.path == "/status" -> respond(socket, 200, "text/plain", recordingStatus().toByteArray())
            request[0] == "POST" && uri.path == "/record" && headers["origin"] == origin -> { requestRecording(); respond(socket, 202, "text/plain", "Starting recording. The sender is notified.".toByteArray()) }
            request[0] == "POST" && uri.path == "/stop" && headers["origin"] == origin -> { stopRecording(); respond(socket, 200, "text/plain", "Stopped".toByteArray()) }
            else -> respond(socket, 404, "text/plain", ByteArray(0))
        }
    }
    private fun streamAudio(socket: Socket) {
        val epoch = synchronized(this) {
            if (!audioEnabled || !valid()) return respond(socket,409,"text/plain","Sound is off on the phone".toByteArray())
            if (audioSockets.isNotEmpty()) return respond(socket,409,"text/plain","Sound is already open in another viewer".toByteArray())
            audioSockets.add(socket)
            audio.currentEpoch()
        }
        socket.sendBufferSize = 4096
        val output = java.io.DataOutputStream(socket.getOutputStream().buffered(4096))
        fun write(action: () -> Unit) {
            val guard = deadlines.schedule({ runCatching { socket.close() } },3,TimeUnit.SECONDS)
            try { action(); output.flush() } finally { guard.cancel(false) }
        }
        try {
            write { output.write(("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-IPMsg-Audio-Epoch: $epoch\r\nConnection: close\r\n\r\n").toByteArray()) }
            var previous = -1L
            while (valid() && audioEnabled && audio.currentEpoch() == epoch && !socket.isClosed && !Thread.currentThread().isInterrupted) {
                val packet = audio.latestAfter(previous,epoch)
                if (packet == null) { Thread.sleep(10); continue }
                write {
                    // Network-order framing; the PCM payload is signed 16-bit little endian.
                    output.writeInt(epoch.toInt()); output.writeLong(packet.sequence); output.writeInt(packet.bytes.size); output.write(packet.bytes)
                }
                previous = packet.sequence
            }
        } finally { audioSockets.remove(socket) }
    }
    private fun stream(socket:Socket) {
        socket.sendBufferSize=32*1024
        val output=socket.getOutputStream().buffered(32*1024)
        var previous:ByteArray?=null
        fun write(action:()->Unit){
            val guard=deadlines.schedule({runCatching{socket.close()}},3,TimeUnit.SECONDS)
            try{action();output.flush()}finally{guard.cancel(false)}
        }
        write{output.write("HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=ipmsgframe\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n".toByteArray())}
        while(valid() && !socket.isClosed && !Thread.currentThread().isInterrupted){
            val latest=frame
            if(latest!=null && latest!==previous){
                write{output.write(("--ipmsgframe\r\nContent-Type: image/jpeg\r\nContent-Length: ${latest.size}\r\n\r\n").toByteArray());output.write(latest);output.write("\r\n".toByteArray())}
                previous=latest
            }
            Thread.sleep(25)
        }
    }
    private fun respond(socket: Socket, code: Int, type: String, body: ByteArray, extra: String = "") {
        val header = "HTTP/1.1 $code Result\r\n${extra}Content-Type: $type\r\nContent-Length: ${body.size}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'self'; img-src 'self' blob:; media-src 'self' blob:; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-ancestors 'none'\r\nConnection: close\r\n\r\n"
        socket.getOutputStream().apply { write(header.toByteArray()); write(body); flush() }
    }
    private fun page() = """<!doctype html><html><meta name="viewport" content="width=device-width"><title>IPMsg camera</title>
<style>*{box-sizing:border-box}html{background:#0b1110}body{font:15px system-ui;color:#eff9f5;max-width:1200px;margin:auto;padding:20px;display:flex;flex-direction:column;gap:14px;height:100dvh}h2,p{margin:0}h2{font-size:18px;font-weight:600;letter-spacing:-.02em}img{width:100%;min-height:0;flex:1;object-fit:contain;border-radius:24px;background:#050908}button{padding:12px 18px;border-radius:14px;border:1px solid #ffffff20;background:#172421;color:#f3faf7;font:600 14px system-ui;cursor:pointer;transition:background .15s}button:hover{background:#293d35}button:focus-visible{outline:3px solid #adddcd;outline-offset:3px}button:disabled{opacity:.4;cursor:default}button[aria-pressed=true]{background:#c5ebdd;color:#10291e}#record{background:#c5ebdd;color:#10291e}#stop{background:#522b2d;color:#ffe7e7}p{color:#bacfc5;line-height:1.5}.controls{display:flex;gap:8px;flex-wrap:wrap;align-items:center}.note{font-size:12px}body.wide{max-width:none;padding:0}body.wide #view{position:fixed;inset:0;width:100vw;height:100dvh;border-radius:0;object-fit:cover;object-position:center}body.wide h2,body.wide .note{display:none}body.wide .controls{position:fixed;bottom:20px;left:12px;right:12px;margin:auto;justify-content:center;z-index:2;width:fit-content;max-width:calc(100vw - 24px);padding:8px;border:1px solid #ffffff20;border-radius:22px;background:#0b1512db;backdrop-filter:blur(18px);box-shadow:0 8px 32px #0005}body.wide #status{position:fixed;top:16px;left:16px;right:16px;z-index:2;width:fit-content;max-width:calc(100vw - 32px);padding:9px 14px;border:1px solid #ffffff18;border-radius:100px;background:#101d19d9;backdrop-filter:blur(12px);font-size:13px}@media(max-width:600px){body.wide .controls{bottom:12px;gap:5px;padding:6px;border-radius:18px}body.wide button{padding:10px 12px;font-size:12px;border-radius:12px}}</style><body class="wide">
<h2>IPMsg · Live camera</h2><img id="view" alt="Connecting"><p id="status">Verify the certificate fingerprint against the phone.</p>
<div class="controls"><button id="record" disabled onclick="recordVideo()">Record</button><button id="capture" disabled onclick="photo()">Photo</button><button id="sound" disabled onclick="toggleSound()" aria-pressed="false">Sound off</button><button id="stop" disabled onclick="stopVideo()">Stop recording</button><button id="full" onclick="wideview()" aria-pressed="true">Fit view</button></div>
<p class="note">Photos and WebM videos download only here. Sound requires the phone owner to enable the microphone and you to turn Sound on. The sender receives capture and recording alerts.</p>
<script>
let active=true,recorder=null,chunks=[],tracks=null,drawId=null,clipTimer=null,pollTimer=null,starting=false,recordingBytes=0;
let soundWanted=false,ownerSound=false,ownerEpoch=-1,audioCtx=null,audioGain=null,audioDestination=null,audioAbort=null,audioRun=0,nextAudioAt=0;
const audioSources=new Set(),MAX_RECORDING_BYTES=96*1024*1024;
const img=document.getElementById('view'),status=document.getElementById('status'),record=document.getElementById('record'),stop=document.getElementById('stop');
const sound=document.getElementById('sound');
function download(blob,name){const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=name;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),60000)}
function stopVideo(notify=true){clearTimeout(clipTimer);cancelAnimationFrame(drawId);if(recorder&&recorder.state!=='inactive')recorder.stop();if(notify&&active)fetch('/stop',{method:'POST'}).catch(end);stop.disabled=true;record.disabled=true}
function clearAudioNodes(){for(const source of audioSources){try{source.stop();source.disconnect()}catch(e){}}audioSources.clear();nextAudioAt=0;if(audioGain)audioGain.gain.value=0}
function stopAudioPlayback(){audioRun++;audioAbort?.abort();audioAbort=null;clearAudioNodes()}
function renderSound(){sound.textContent=!soundWanted?'Sound off':ownerSound?'Sound on':'Waiting for phone';sound.setAttribute('aria-pressed',String(soundWanted));sound.title=ownerSound?'Listen to the phone microphone':'The phone owner controls microphone sharing'}
function ensureAudio(){if(audioCtx)return;const Audio=window.AudioContext||window.webkitAudioContext;if(!Audio)throw Error('Sound is unavailable in this browser');audioCtx=new Audio({latencyHint:'interactive'});audioGain=audioCtx.createGain();audioGain.gain.value=0;audioDestination=audioCtx.createMediaStreamDestination();audioGain.connect(audioCtx.destination);audioGain.connect(audioDestination)}
function playPcm(bytes){if(!active||!soundWanted||!ownerSound||!audioCtx||audioCtx.state!=='running')return;const now=audioCtx.currentTime;if(nextAudioAt>now+.25)clearAudioNodes();const buffer=audioCtx.createBuffer(1,bytes.byteLength/2,16000),samples=buffer.getChannelData(0),view=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);for(let i=0;i<samples.length;i++)samples[i]=view.getInt16(i*2,true)/32768;const source=audioCtx.createBufferSource();source.buffer=buffer;source.connect(audioGain);audioGain.gain.value=1;audioSources.add(source);source.onended=()=>{audioSources.delete(source);source.disconnect()};const at=Math.max(now+.035,nextAudioAt);source.start(at);nextAudioAt=at+buffer.duration}
async function streamSound(){if(audioAbort||!active||!ownerSound||!soundWanted)return;const run=++audioRun,controller=new AbortController();audioAbort=controller;try{const r=await fetch('/audio',{signal:controller.signal});if(r.status===403)return end();if(!r.ok)return;const epoch=Number(r.headers.get('X-IPMsg-Audio-Epoch'));if(epoch!==ownerEpoch||run!==audioRun)return;const reader=r.body.getReader();let pending=new Uint8Array(0);while(active&&run===audioRun&&ownerSound&&soundWanted){const result=await reader.read();if(result.done)break;if(result.value.byteLength+pending.byteLength>65536)throw Error('Sound connection stalled');const data=new Uint8Array(pending.length+result.value.length);data.set(pending);data.set(result.value,pending.length);let offset=0;while(data.length-offset>=16){const head=new DataView(data.buffer,data.byteOffset+offset,16),packetEpoch=head.getUint32(0),length=head.getUint32(12);if(length<2||length>3200||length%2||packetEpoch!==ownerEpoch)throw Error('Sound session changed');if(data.length-offset<16+length)break;if(run!==audioRun||!ownerSound||!soundWanted)break;playPcm(data.subarray(offset+16,offset+16+length));offset+=16+length}pending=data.slice(offset)}}catch(e){if(e.name!=='AbortError'&&active)status.textContent='Sound interrupted · camera stays live'}finally{if(run===audioRun){stopAudioPlayback();renderSound()}}}
async function refreshSound(){const r=await fetch('/audio-state');if(!r.ok)return end();const state=await r.json();if(state.epoch!==ownerEpoch||!state.enabled){stopAudioPlayback();ownerEpoch=state.epoch}ownerSound=state.enabled;renderSound();if(ownerSound&&soundWanted)streamSound()}
async function toggleSound(){if(soundWanted){soundWanted=false;stopAudioPlayback();renderSound();return}try{ensureAudio();await audioCtx.resume();soundWanted=true;renderSound();await refreshSound()}catch(e){soundWanted=false;stopAudioPlayback();renderSound();status.textContent=e.message}}
function end(){if(!active)return;active=false;clearTimeout(pollTimer);soundWanted=false;ownerSound=false;stopAudioPlayback();stopVideo(false);audioDestination?.stream.getTracks().forEach(t=>t.stop());audioCtx?.close().catch(()=>{});img.removeAttribute('src');status.textContent='Session ended';document.querySelectorAll('button:not(#full)').forEach(b=>b.disabled=true)}
img.onerror=end;
function wideview(){const wide=document.body.classList.toggle('wide'),button=document.getElementById('full');button.textContent=wide?'Fit view':'Wide view';button.setAttribute('aria-pressed',String(wide))}
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&document.body.classList.contains('wide'))wideview()});
async function recordVideo(){if(starting||recorder)return;starting=true;record.disabled=true;try{
 if(!window.MediaRecorder||!HTMLCanvasElement.prototype.captureStream)throw Error('Recording is unavailable in this browser');
 if(!img.naturalWidth)throw Error('Wait for the camera preview');
 const canvas=document.createElement('canvas');canvas.width=img.naturalWidth;canvas.height=img.naturalHeight;const ctx=canvas.getContext('2d',{alpha:false});
 let lastDraw=0;const draw=(now=0)=>{if(now-lastDraw>=66||!lastDraw){ctx.drawImage(img,0,0,canvas.width,canvas.height);lastDraw=now}drawId=requestAnimationFrame(draw)};draw();tracks=canvas.captureStream(15);
 // A stable, silent-until-enabled track lets owner/receiver mute changes apply during a clip.
 if(window.AudioContext||window.webkitAudioContext){ensureAudio();await audioCtx.resume();tracks.addTrack(audioDestination.stream.getAudioTracks()[0].clone())}
 const mime=['video/webm;codecs=vp8,opus','video/webm;codecs=vp8','video/webm'].find(t=>MediaRecorder.isTypeSupported(t));if(!mime)throw Error('WebM recording is unavailable in this browser');
 const current=new MediaRecorder(tracks,{mimeType:mime,videoBitsPerSecond:4000000});recorder=current;chunks=[];recordingBytes=0;
 current.ondataavailable=e=>{if(e.data.size){chunks.push(e.data);recordingBytes+=e.data.size;if(recordingBytes>=MAX_RECORDING_BYTES&&current.state!=='inactive')stopVideo()}};
 current.onstop=()=>{cancelAnimationFrame(drawId);tracks?.getTracks().forEach(t=>t.stop());const blob=new Blob(chunks,{type:current.mimeType});chunks=[];recorder=null;if(blob.size)download(blob,'IPMsg-video-'+Date.now()+'.webm');if(active){record.disabled=false;status.textContent='Recording downloaded to this browser'}};
 current.onerror=()=>{status.textContent='Recording interrupted';stopVideo()};
 const response=await fetch('/record',{method:'POST'});if(!response.ok){end();throw Error('Session unavailable')}if(!active||recorder!==current)throw Error('Session ended');
 current.start(1000);stop.disabled=false;status.textContent='Recording · saved only here · sound follows Sound button';clipTimer=setTimeout(()=>stopVideo(),300000);
 }catch(e){cancelAnimationFrame(drawId);tracks?.getTracks().forEach(t=>t.stop());recorder=null;if(active){status.textContent=e.message;record.disabled=false;fetch('/stop',{method:'POST'}).catch(end)}}finally{starting=false}}
async function photo(){const button=document.getElementById('capture');button.disabled=true;try{const r=await fetch('/capture',{method:'POST'});if(!r.ok){status.textContent=await r.text();return}download(await r.blob(),'IPMsg-photo-'+Date.now()+'.jpg')}catch(e){end()}finally{if(active)button.disabled=false}}
async function poll(){if(!active)return;try{const r=await fetch('/status');if(!r.ok)return end();const text=await r.text();if(!recorder&&!starting)status.textContent=text;await refreshSound();if(active)pollTimer=setTimeout(poll,1000)}catch(e){end()}}
async function boot(){let key=new URLSearchParams(location.hash.slice(1)).get('key');history.replaceState(null,'',location.pathname);if(key){const r=await fetch('/auth',{method:'POST',headers:{'X-IPMsg-Token':key}});key=null;if(!r.ok)return end()}const r=await fetch('/status');if(!r.ok)return end();await refreshSound();if(!active)return;record.disabled=false;document.getElementById('capture').disabled=false;sound.disabled=false;img.src='/stream';pollTimer=setTimeout(poll,1000)}
document.addEventListener('visibilitychange',()=>{if(document.hidden){soundWanted=false;stopAudioPlayback();renderSound()}});
window.addEventListener('pagehide',end);
window.addEventListener('beforeunload',e=>{if(recorder){e.preventDefault();e.returnValue='Stop recording and wait for its download before leaving.'}});
boot().catch(end);
</script></body></html>"""

    override fun close() {
        active = false; stopAudio(); frame = null
        runCatching { server.close() }
        sockets.forEach { runCatching { it.close() } }; sockets.clear()
        workers.shutdownNow(); deadlines.shutdownNow()
    }
}
