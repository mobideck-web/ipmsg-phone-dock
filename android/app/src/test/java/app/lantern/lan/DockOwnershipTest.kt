package app.lantern.lan

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import javax.xml.parsers.DocumentBuilderFactory
import org.w3c.dom.Element

class DockOwnershipTest {
    private class Lease:AutoCloseable {
        var closed=0
        override fun close(){closed++}
        override fun equals(other:Any?)=other is Lease
        override fun hashCode()=0
    }
    @Test fun queuedOldExpiryOrServiceDestroyCannotStopReplacement() {
        val slot=DockOwnerSlot<Lease>();val old=slot.replace{Lease()}
        val queuedExpiry={slot.stop(old)}
        val fresh=slot.replace{Lease()}
        assertEquals(1,old.closed);assertFalse(queuedExpiry());assertSame(fresh,slot.current());assertEquals(0,fresh.closed)
        assertTrue(slot.stop(fresh));assertNull(slot.current());assertEquals(1,fresh.closed)
    }
    @Test fun failedReplacementDoesNotRestoreOldCapability() {
        val slot=DockOwnerSlot<Lease>();val old=slot.replace{Lease()}
        runCatching{slot.replace{error("cannot bind")}}
        assertNull(slot.current());assertEquals(1,old.closed);assertFalse(slot.stop(old))
    }
    @Test fun publicShareTargetAlwaysDiscardsSessionCreationToken() {
        val token="a".repeat(64)
        assertNull(DockPolicy.ownerRequest(true,token));assertNull(DockPolicy.ownerRequest(true,"malformed"))
        assertEquals(token,DockPolicy.ownerRequest(false,token))
    }
    @Test fun manifestGatesOnlyAdbEntryPointsAndKeepsServicePrivate() {
        val candidates=generateSequence(File(System.getProperty("user.dir"))){it.parentFile}.take(8)
            .flatMap{sequenceOf(File(it,"app/src/main/AndroidManifest.xml"),File(it,"src/main/AndroidManifest.xml"))}
        val manifest=candidates.firstOrNull{it.isFile} ?: error("Manifest fixture not found")
        val factory=DocumentBuilderFactory.newInstance().apply{isNamespaceAware=true;setFeature("http://apache.org/xml/features/disallow-doctype-decl",true)}
        val doc=factory.newDocumentBuilder().parse(manifest)
        val android="http://schemas.android.com/apk/res/android"
        fun component(kind:String,name:String):Element {
            val nodes=doc.getElementsByTagName(kind)
            return (0 until nodes.length).map{nodes.item(it) as Element}.first{it.getAttributeNS(android,"name")==name}
        }
        listOf(".DockFilesActivity",".DockEdgeActivity").forEach{name->
            val activity=component("activity",name)
            assertEquals("true",activity.getAttributeNS(android,"exported"))
            assertEquals("android.permission.DUMP",activity.getAttributeNS(android,"permission"))
        }
        val share=component("activity",".DockShareActivity")
        assertEquals("true",share.getAttributeNS(android,"exported"));assertEquals("",share.getAttributeNS(android,"permission"))
        assertEquals("false",component("service",".DockBridgeService").getAttributeNS(android,"exported"))
    }
}
