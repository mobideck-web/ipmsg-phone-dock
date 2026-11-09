package app.lantern.lan
import org.junit.Assert.*
import org.junit.Test
import java.net.URI
import java.security.KeyPairGenerator
import java.security.KeyStore
import javax.net.ssl.*
import java.util.concurrent.atomic.AtomicInteger
class LiveCameraServerTest {
    @Test fun rejectedClientsCannotAccumulateTimeoutJobs() {
        LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},tls=identity.context).use {server->
            val sockets=mutableListOf<java.net.Socket>()
            try {
                val address=URI(server.url)
                repeat(30){
                    val socket=java.net.Socket()
                    try {socket.connect(java.net.InetSocketAddress(address.host,address.port),1000);sockets.add(socket)}
                    catch(_:java.io.IOException){socket.close()} // Refusing overload is a valid defensive response.
                    Thread.sleep(10)
                }
                assertTrue("The fixture must exercise admitted connections",sockets.size>=7)
                val deadline=System.nanoTime()+1_000_000_000L
                val field=LiveCameraServer::class.java.getDeclaredField("deadlines").apply{isAccessible=true}
                val executor=field.get(server) as java.util.concurrent.ScheduledThreadPoolExecutor
                while(System.nanoTime()<deadline && executor.queue.size<7) Thread.sleep(10)
                assertTrue("Handshake timeout jobs must remain bounded even when the worker queue rejects clients",executor.queue.size<=7)
            } finally {sockets.forEach{runCatching{it.close()}}}
        }
    }
    private val identity by lazy { val pair=KeyPairGenerator.getInstance("RSA").apply{initialize(2048)}.generateKeyPair();CameraTls.identity(pair.private,CameraTls.certificate(pair,"127.0.0.1")) }
    private fun factory(cert: java.security.cert.X509Certificate=identity.certificate): SSLSocketFactory {
        val store=KeyStore.getInstance(KeyStore.getDefaultType()).apply{load(null);setCertificateEntry("camera",cert)}
        val tm=TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply{init(store)}
        return SSLContext.getInstance("TLS").apply{init(null,tm.trustManagers,null)}.socketFactory
    }
    private fun request(server: LiveCameraServer,path:String="/",method:String="GET",cookie:String="",token:String?=null,origin:String?=server.origin):String {
        val u=URI(server.url)
        return (factory().createSocket(u.host,u.port) as SSLSocket).use { socket ->
            socket.soTimeout=5000;socket.sslParameters=socket.sslParameters.apply{endpointIdentificationAlgorithm="HTTPS"};socket.startHandshake()
            socket.outputStream.write(("$method $path HTTP/1.1\r\nHost: ${u.host}:${u.port}\r\n"+(origin?.let{"Origin: $it\r\n"}?:"")+(token?.let{"X-IPMsg-Token: $it\r\n"}?:"")+(if(cookie.isNotBlank()) "Cookie: $cookie\r\n" else "")+"Content-Length: 0\r\n\r\n").toByteArray());socket.inputStream.readBytes().toString(Charsets.UTF_8)
        }
    }
    @Test fun tokenIsOneUseAndRecordingNeedsSecureCookieAndOrigin() {
        val requests=AtomicInteger()
        LiveCameraServer("127.0.0.1","127.0.0.1",{requests.incrementAndGet()},{},{"Live"},tls=identity.context).use{s ->
            assertTrue(request(s).contains("history.replaceState"))
            assertTrue(request(s,"/record","POST").startsWith("HTTP/1.1 403"))
            assertTrue(request(s,"/auth","POST",token="wrong").startsWith("HTTP/1.1 403"))
            val token=URI(s.url).fragment.substringAfter("key=")
            val auth=request(s,"/auth","POST",token=token)
            assertTrue(auth.contains("Secure; HttpOnly; SameSite=Strict"))
            val cookie=Regex("Set-Cookie: ([^;]+)").find(auth)!!.groupValues[1]
            assertTrue(request(s,"/auth","POST",token=token).startsWith("HTTP/1.1 403"))
            assertTrue(request(s,"/record","POST",cookie=cookie,origin="https://bad.invalid").startsWith("HTTP/1.1 403"));assertEquals(0,requests.get())
            assertTrue(request(s,"/record","POST",cookie=cookie).startsWith("HTTP/1.1 202"));assertEquals(1,requests.get())
            assertTrue(request(s,"/record","POST",cookie=cookie,origin=null).startsWith("HTTP/1.1 404"))
            s.frame=byteArrayOf(1,2,3);assertTrue(request(s,"/frame",cookie=cookie).contains("image/jpeg"))
        }
    }
    @Test fun authenticatedStreamUsesOneTlsConnectionAndEndsAtExpiry() {
        LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},3000,tls=identity.context).use {server->
            assertTrue(request(server,"/stream").startsWith("HTTP/1.1 403"))
            val auth=request(server,"/auth","POST",token=URI(server.url).fragment.substringAfter("key="))
            val cookie=Regex("Set-Cookie: ([^;]+)").find(auth)!!.groupValues[1]
            server.frame="first-frame".toByteArray()
            val address=URI(server.url)
            (factory().createSocket(address.host,address.port) as SSLSocket).use {socket->
                socket.soTimeout=5000;socket.startHandshake()
                socket.outputStream.write(("GET /stream HTTP/1.1\r\nHost: ${address.host}:${address.port}\r\nCookie: $cookie\r\n\r\n").toByteArray())
                val input=socket.inputStream.bufferedReader();var first=false;var latest=false;var multipart=false
                while(true){val line=input.readLine() ?: break
                    if(line.contains("multipart/x-mixed-replace"))multipart=true
                    if(line=="first-frame"){first=true;server.frame="latest-frame".toByteArray()}
                    if(line=="latest-frame")latest=true
                }
                assertTrue(multipart);assertTrue(first);assertTrue(latest)
            }
        }
    }
    @Test fun closeAndDeadlineRevokeAccess() {
        val s=LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},tls=identity.context);s.close();assertTrue(runCatching{request(s)}.isFailure)
        LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},1,tls=identity.context).use {Thread.sleep(10);assertTrue(runCatching{request(it)}.isFailure)}
    }
    @Test fun captureRequiresAuthenticationAndRecordingsStayReceiverOnly(){
        val photos=AtomicInteger();val video="recorded-video".toByteArray()
        LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},tls=identity.context,snapshotTaken={photos.incrementAndGet()},savedRecording={CameraRecording(video.size.toLong()){video.inputStream()}}).use{s->
            s.frame="photo-data".toByteArray()
            assertTrue(request(s,"/capture","POST").startsWith("HTTP/1.1 403"))
            assertTrue(request(s,"/recording").startsWith("HTTP/1.1 403"))
            val auth=request(s,"/auth","POST",token=URI(s.url).fragment.substringAfter("key="));val cookie=Regex("Set-Cookie: ([^;]+)").find(auth)!!.groupValues[1]
            assertTrue(request(s,"/capture","POST",cookie=cookie,origin="https://bad.invalid").startsWith("HTTP/1.1 403"));assertEquals(0,photos.get())
            val photo=request(s,"/capture","POST",cookie=cookie);assertTrue(photo.contains("image/jpeg") && photo.endsWith("photo-data"));assertEquals(1,photos.get())
            assertTrue(request(s,"/capture","POST",cookie=cookie).startsWith("HTTP/1.1 429"));assertEquals(1,photos.get())
            assertTrue(request(s,"/recording",cookie=cookie).startsWith("HTTP/1.1 404"))
        }
    }
    @Test fun audioStartsMutedAndRequiresTheExistingAuthenticatedSession() {
        LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},tls=identity.context).use { server ->
            assertFalse(server.audioEnabled)
            assertTrue(request(server,"/audio").startsWith("HTTP/1.1 403"))
            assertTrue(request(server,"/audio-state").startsWith("HTTP/1.1 403"))
            val auth=request(server,"/auth","POST",token=URI(server.url).fragment.substringAfter("key="))
            val cookie=Regex("Set-Cookie: ([^;]+)").find(auth)!!.groupValues[1]
            assertTrue(request(server,"/audio",cookie=cookie).startsWith("HTTP/1.1 409"))
            assertTrue(request(server,"/audio-state",cookie=cookie).contains("\"enabled\":false"))
            assertTrue(request(server,"/audio","POST",cookie=cookie).startsWith("HTTP/1.1 404"))
            assertFalse(server.audioEnabled)
            server.audioEnabled=true
            assertTrue(request(server,"/audio-state",cookie=cookie,origin="https://bad.invalid").startsWith("HTTP/1.1 403"))
        }
    }
    @Test fun ownerMuteClosesTheAudioStreamAndReenableHasANewEpoch() {
        LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},tls=identity.context).use { server ->
            val auth=request(server,"/auth","POST",token=URI(server.url).fragment.substringAfter("key="))
            val cookie=Regex("Set-Cookie: ([^;]+)").find(auth)!!.groupValues[1]
            server.audioEnabled=true
            server.publishAudio(byteArrayOf(1,2,3,4))
            val address=URI(server.url)
            var firstEpoch=0
            (factory().createSocket(address.host,address.port) as SSLSocket).use { socket ->
                socket.soTimeout=3000;socket.startHandshake()
                socket.outputStream.write(("GET /audio HTTP/1.1\r\nHost: ${address.host}:${address.port}\r\nCookie: $cookie\r\n\r\n").toByteArray())
                val input=java.io.DataInputStream(socket.inputStream)
                val header=StringBuilder()
                while(!header.endsWith("\r\n\r\n"))header.append(input.readUnsignedByte().toChar())
                assertTrue(header.startsWith("HTTP/1.1 200"))
                firstEpoch=input.readInt();assertTrue(input.readLong()>0)
                val size=input.readInt();assertEquals(4,size)
                val data=ByteArray(size);input.readFully(data);assertArrayEquals(byteArrayOf(1,2,3,4),data)
                assertTrue(request(server,"/audio",cookie=cookie).startsWith("HTTP/1.1 409"))
                server.stopAudio()
                val ended = try { input.read() }
                    catch (timeout:java.net.SocketTimeoutException) { throw AssertionError("Muted audio must close, not remain open until the read timeout", timeout) }
                    catch (_:java.io.IOException) { -1 }
                assertEquals(-1,ended)
            }
            assertFalse(server.audioEnabled)
            server.publishAudio(byteArrayOf(9,9))
            server.audioEnabled=true
            val state=request(server,"/audio-state",cookie=cookie)
            val newEpoch=Regex("\"epoch\":(\\d+)").find(state)!!.groupValues[1].toInt()
            assertTrue(newEpoch>firstEpoch)
        }
    }
    @Test fun certificateIsNotACaAndIsBoundToTheAddress() {assertEquals(-1,identity.certificate.basicConstraints);assertTrue(identity.certificate.subjectAlternativeNames.any{it[1]=="127.0.0.1"})}
    @Test fun untrustedCertificateFailsTls() {
        LiveCameraServer("127.0.0.1","127.0.0.1",{},{},{"Live"},tls=identity.context).use { s ->val u=URI(s.url);assertTrue(runCatching{(SSLContext.getDefault().socketFactory.createSocket(u.host,u.port) as SSLSocket).use{it.startHandshake()}}.isFailure)}
    }
}
