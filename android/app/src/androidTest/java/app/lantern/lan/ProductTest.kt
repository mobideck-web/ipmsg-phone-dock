package app.lantern.lan

import android.content.Intent
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProductTest {
    private val context get()=InstrumentationRegistry.getInstrumentation().targetContext
    @Test fun approvalFollowsVerifiedKeyButNeverFollowsPlaintextOrAnotherKey() {
        val model=Model.get(context);val engine=LanEngine(model,{_,_->});model.engine=engine
        @Suppress("UNCHECKED_CAST") val keys=LanEngine::class.java.getDeclaredField("keys").apply{isAccessible=true}.get(engine) as MutableMap<String,IpCrypto.Companion.PeerKey>
        val ip="192.0.2.106";val next="192.0.2.107";val user="policy-${System.nanoTime()}"
        val key=IpCrypto.generate();val peer=Peer(ip,"Policy test",user,"pc",Wire.CAPS)
        model.restore(ip);model.restore(next)
        try {
            keys[ip]=IpCrypto.Companion.PeerKey(IpCrypto.CAPS,key.pair.public)
            assertFalse(model.devices.approved(ip,user));assertTrue(runCatching{model.devices.authorize(ip,user,true,false)}.isFailure)
            model.devices.approve(peer);assertTrue(model.devices.approved(ip,user))
            keys[next]=keys[ip]!!;model.devices.observed(peer.copy(ip=next));assertTrue(model.devices.approved(next,user))
            keys.remove(next);assertFalse(model.devices.approved(next,user));assertTrue(runCatching{model.devices.authorize(next,user,false,true)}.isFailure)
            keys[next]=IpCrypto.Companion.PeerKey(IpCrypto.CAPS,IpCrypto.generate().pair.public);assertFalse(model.devices.approved(next,user))
            keys[next]=keys[ip]!!;model.devices.revoke(model.devices.record(next,user)!!,true);assertFalse(model.devices.approved(ip,user))
        } finally {engine.close();model.engine=null}
    }
    @Test fun historySearchFindsOldMessagesAndKeysetPagesDoNotOverlap() {
        val model=Model.get(context);val user="paging-test";val ip="192.0.2.108"
        model.history.writableDatabase.beginTransaction()
        try {repeat(2105){model.history.add(it.toLong(),ip,if(it==0) "oldest special needle" else "message $it",false,"Plaintext","Received",user)};model.history.writableDatabase.setTransactionSuccessful()}
        finally{model.history.writableDatabase.endTransaction()}
        try {
            assertEquals(1,model.repository.search("oldest special needle",ip,user).size)
            val first=model.repository.search("",ip,user);val second=model.repository.search("",ip,user,first.first().id)
            assertEquals(100,first.size);assertEquals(100,second.size);assertTrue(first.none{a->second.any{it.id==a.id}})
            assertTrue(model.repository.summaries("oldest special needle").any{it.ip==ip})
        } finally {model.history.deleteConversation(ip,user);model.reload()}
    }
    @Test fun outboxStaysReviewOnlyAndNonPersistableFilesAreStaged() {
        val model=Model.get(context);val peer=Peer("192.0.2.109","Outbox test","queue-test","pc",0)
        model.restore(peer.ip);model.devices.approve(peer)
        val source=Uri.parse("content://"+InstrumentationRegistry.getInstrumentation().context.packageName+".fixtures/sample")
        var id:Long?=null;var staged:Uri?=null
        try {
            staged=ShareSupport.durable(model,source)
            assertNotEquals(source,staged)
            id=model.repository.enqueue(peer.ip,peer.user,"Review this",listOf(staged.toString()))
            val row=ProductRepository(model).outbox().first{it.id==id}
            assertEquals("REVIEW",row.state);assertTrue(row.expires>row.created)
            assertArrayEquals(byteArrayOf(1,2,3),context.contentResolver.openInputStream(staged)!!.use{it.readBytes()})
            model.devices.revoke(model.devices.record(peer.ip,peer.user)!!)
            assertTrue(runCatching{model.repository.enqueue(peer.ip,peer.user,"No approval",emptyList())}.isFailure)
        } finally {id?.let{model.repository.removeOutbox(it)};context.contentResolver.delete(source,null,null);staged?.let{context.contentResolver.delete(it,null,null)}}
    }
    @Test fun incompleteDestinationIsNotPublishedAndPartialCleanupIsTracked() {
        val model=Model.get(context);val uri=DownloadStorage.create(model,"incomplete-${System.nanoTime()}.zip",9)
        context.contentResolver.openOutputStream(uri)!!.use{it.write(1)}
        assertTrue(runCatching{DownloadStorage.publish(model,uri,9)}.isFailure)
        assertTrue(uri in model.repository.partials())
        DownloadStorage.cleanup(model,listOf(uri))
        assertFalse(uri in model.repository.partials())
    }
    @Test fun conversationNameUsesNicknameOfflineAndRefreshesTheDeviceName() {
        val model=Model.get(context);val peer=Peer("192.0.2.111","Original PC","name-test-${System.nanoTime()}","pc",0)
        model.devices.observed(peer);assertFalse(model.devices.approved(peer.ip,peer.user))
        model.devices.approve(peer);val record=model.devices.record(peer.ip,peer.user)!!
        model.repository.save(record.copy(nickname="Work computer"))
        assertEquals("Work computer",model.devices.label(peer.ip,peer.user))
        model.devices.observed(peer.copy(name="Renamed PC"))
        assertEquals("Work computer",model.devices.label(peer.ip,peer.user))
        model.repository.save(model.devices.record(peer.ip,peer.user)!!.copy(nickname=""))
        assertEquals("Renamed PC",model.devices.label(peer.ip,peer.user))
    }
    @Test fun releaseUpgradeIncludesOriginalSigningCertificate() {
        if(context.applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE != 0) return
        val info=context.packageManager.getPackageInfo(context.packageName,android.content.pm.PackageManager.GET_SIGNING_CERTIFICATES).signingInfo!!
        assertTrue(info.hasPastSigningCertificates())
        assertTrue(info.signingCertificateHistory.any { signature-> java.security.MessageDigest.getInstance("SHA-256").digest(signature.toByteArray()).joinToString(""){"%02x".format(it.toInt() and 255)}=="1a78378acfdc58ae6c68f0ccdb24f9e9163933ae4d31aa071416ec582670e201" })
    }
    @Test fun shareIntentRejectsFileUrisAndOversizedBatches() {
        assertTrue(runCatching{ShareSupport.received(Intent(Intent.ACTION_SEND).putExtra(Intent.EXTRA_STREAM,Uri.parse("file:///data/data/app.lantern.lan/history.db")))}.isFailure)
        val list=ArrayList((0..30).map{Uri.parse("content://example/$it")})
        assertTrue(runCatching{ShareSupport.received(Intent(Intent.ACTION_SEND_MULTIPLE).putParcelableArrayListExtra(Intent.EXTRA_STREAM,list))}.isFailure)
    }
}
