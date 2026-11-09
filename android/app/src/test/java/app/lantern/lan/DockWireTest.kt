package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class DockWireTest {
    private val token="a".repeat(64)
    private val id="b".repeat(32)
    private fun request(server:DockWire,path:String="/v1/status",method:String="GET",auth:String?="Bearer $token",extra:String=""):ByteArray = Socket("127.0.0.1",server.port).use {socket->
        socket.soTimeout=4000
        socket.getOutputStream().write(("$method $path HTTP/1.1\r\nHost: 127.0.0.1:${server.port}\r\n"+
            (auth?.let{"Authorization: $it\r\n"} ?: "")+extra+"\r\n").toByteArray())
        socket.getInputStream().readBytes()
    }
    private fun text(bytes:ByteArray)=String(bytes,Charsets.ISO_8859_1)
    private fun server(valid:()->Boolean={true},lookup:(String)->DockExport?={null},stop:()->Unit={},end:(String?,Long?)->Unit={_,_->})=
        DockWire(token,valid,{}, {"{\"version\":1,\"items\":[]}"},lookup,stop,end,0)
    @Test fun tokenIsStrictAndJsonEscapingPreservesData() {
        assertTrue(DockPolicy.token(token));assertFalse(DockPolicy.token("A".repeat(64)));assertFalse(DockPolicy.token(token+"0"));assertFalse(DockPolicy.token(null))
        assertEquals("\"a\\\"\\n\\\\b\"",DockPolicy.quote("a\"\n\\b"))
        assertTrue(DockPolicy.id(id));assertFalse(DockPolicy.id("../$id"))
    }
    @Test fun authenticationIsRequiredAndNotEchoed()=server().use {s->
        assertTrue(text(request(s)).startsWith("HTTP/1.1 200"))
        assertTrue(text(request(s,auth=null)).startsWith("HTTP/1.1 401"))
        val denied=text(request(s,auth="Bearer "+"c".repeat(64)));assertTrue(denied.startsWith("HTTP/1.1 401"));assertFalse(denied.contains(token))
    }
    @Test fun queriesOriginsDuplicateHeadersAndRequestBodiesAreRejected()=server().use {s->
        assertTrue(text(request(s,"/v1/status?token=$token")).startsWith("HTTP/1.1 400"))
        assertTrue(text(request(s,extra="Origin: https://example.test\r\n")).startsWith("HTTP/1.1 403"))
        assertTrue(text(request(s,extra="Authorization: Bearer $token\r\n")).startsWith("HTTP/1.1 400"))
        assertTrue(text(request(s,extra="Transfer-Encoding: chunked\r\n")).startsWith("HTTP/1.1 400"))
        assertTrue(text(request(s,extra="Content-Length: 3\r\n")).startsWith("HTTP/1.1 400"))
    }
    @Test fun pathsAndMethodsCannotOpenArbitrarySources()=server().use {s->
        assertTrue(text(request(s,"/v1/file/../../secret")).startsWith("HTTP/1.1 404"))
        assertTrue(text(request(s,"/v1/file/$id")).startsWith("HTTP/1.1 404"))
        assertTrue(text(request(s,method="PUT")).startsWith("HTTP/1.1 405"))
        assertTrue(text(request(s,"/v1/stop",method="GET")).startsWith("HTTP/1.1 404"))
    }
    @Test fun boundedHeadersRejectOversizedInput()=server().use {s->
        assertTrue(text(request(s,extra="X-Long: "+"x".repeat(3000)+"\r\n")).startsWith("HTTP/1.1 400"))
    }
    @Test fun expiredSessionAndStopAreEnforced() {
        val valid=AtomicBoolean(true);val stopped=AtomicInteger();val ended=AtomicInteger()
        server({valid.get()},stop={stopped.incrementAndGet()},end={_,_->ended.incrementAndGet()}).use{s->
            assertTrue(text(request(s,"/v1/gesture/end","POST",extra="Content-Length: 0\r\n")).startsWith("HTTP/1.1 200"));assertEquals(1,ended.get())
            assertTrue(text(request(s,"/v1/stop","POST")).startsWith("HTTP/1.1 200"));assertEquals(1,stopped.get())
            valid.set(false);assertTrue(text(request(s)).startsWith("HTTP/1.1 410"))
        }
    }
    @Test fun gestureAcknowledgementIsScopedAndMalformedScopeIsRejected() {
        var receivedId:String?=null;var receivedVersion:Long?=null;var calls=0
        server(end={id,revision->receivedId=id;receivedVersion=revision;calls++}).use{s->
            assertTrue(text(request(s,"/v1/gesture/end","POST",extra="X-IPMsg-Gesture-Id: $id\r\nX-IPMsg-Gesture-Revision: 17\r\n")).startsWith("HTTP/1.1 200"))
            assertEquals(id,receivedId);assertEquals(17L,receivedVersion);assertEquals(1,calls)
            for(headers in listOf("X-IPMsg-Gesture-Id: $id\r\n","X-IPMsg-Gesture-Revision: 17\r\n",
                "X-IPMsg-Gesture-Id: ../evil\r\nX-IPMsg-Gesture-Revision: 17\r\n",
                "X-IPMsg-Gesture-Id: $id\r\nX-IPMsg-Gesture-Revision: -1\r\n",
                "X-IPMsg-Gesture-Id: $id\r\nX-IPMsg-Gesture-Revision: 9999999999999999999\r\n"))
                assertTrue(text(request(s,"/v1/gesture/end","POST",extra=headers)).startsWith("HTTP/1.1 400"))
            assertEquals(1,calls)
        }
    }
    @Test fun delayedAndLegacyAcknowledgementsCannotEndANewerGesture() {
        val newer="c".repeat(32)
        assertFalse(DockPolicy.acknowledgesGesture(newer,8,null,null))
        assertFalse(DockPolicy.acknowledgesGesture(newer,8,id,7))
        assertFalse(DockPolicy.acknowledgesGesture(newer,8,newer,7))
        assertTrue(DockPolicy.acknowledgesGesture(newer,8,newer,8))
    }
    @Test fun ordinaryAndVirtualFilesCannotClaimTheFolderCapability() {
        assertEquals("application/zip",DockPolicy.selectedFileMime(DockFolderArchive.MIME))
        assertEquals("application/zip",DockPolicy.selectedFileMime(" APPLICATION/VND.IPMSG.FOLDER+ZIP "))
        assertEquals("image/jpeg",DockPolicy.selectedFileMime("image/jpeg"))
        assertEquals("application/octet-stream",DockPolicy.selectedFileMime("image/jpeg\n".replace("\n","\u0001")))
        assertEquals("application/octet-stream",DockPolicy.selectedFileMime(null))
    }
    @Test fun filesStreamExactBytesIncludingEmptyAndThumbnail() {
        val data=ByteArray(400_123){(it%251).toByte()};val thumb=byteArrayOf(-1,-40,-1,-39)
        fun item(bytes:ByteArray)=DockExport(id,"file.bin","application/octet-stream",bytes.size.toLong(),0,thumb,{ByteArrayInputStream(bytes)},{true})
        var selected=item(data)
        server(lookup={if(it==id)selected else null}).use{s->
            val raw=request(s,"/v1/file/$id");val headerEnd=text(raw).indexOf("\r\n\r\n")+4
            assertTrue(text(raw.copyOfRange(0,headerEnd)).contains("Content-Length: ${data.size}\r\n"));assertArrayEquals(data,raw.copyOfRange(headerEnd,raw.size))
            selected=item(ByteArray(0));val empty=request(s,"/v1/file/$id");assertTrue(text(empty).endsWith("\r\n\r\n"));assertTrue(text(empty).contains("Content-Length: 0\r\n"))
            assertTrue(text(request(s,"/v1/thumbnail/$id")).contains("Content-Type: image/jpeg\r\n"))
        }
    }
    @Test fun changedProviderDoesNotPublishLastBytesAsSuccessfulFile() {
        val checks=AtomicInteger()
        val item=DockExport(id,"changing.bin","application/octet-stream",4,0,null,{ByteArrayInputStream(byteArrayOf(1,2,3,4))},{checks.incrementAndGet()==1})
        server(lookup={item}).use{s->
            val result=request(s,"/v1/file/$id");assertTrue(text(result).contains("Content-Length: 4\r\n"));assertTrue(text(result).endsWith("\r\n\r\n"))
        }
    }
}
